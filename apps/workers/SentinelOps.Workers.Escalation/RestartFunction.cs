using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;

namespace SentinelOps.Workers.Escalation;

// Consumes `incident.updated` events where Field == "Status" and
// NewValue == "Reopened" (filtered by the EventBridge rule that targets this
// function's queue — see infrastructure-stack.ts). A reopened incident needs
// the exact same "assign + maybe start escalation" sequence a brand-new
// incident gets, since its previous escalation execution already stopped
// itself once the incident was resolved.
public class RestartFunction
{
    public const string WorkerName = "escalation-restart";

    private readonly string _connectionString;
    private readonly IEventPublisher _eventPublisher;
    private readonly IEscalationStarter _escalationStarter;
    private readonly string _stateMachineArn;

    public RestartFunction() : this(
        DbConnectionStringResolver.FromEnvironment(),
        EventBridgeEventPublisher.FromEnvironment(),
        new StepFunctionsEscalationStarter(
            Environment.GetEnvironmentVariable("AWS_REGION")
                ?? throw new InvalidOperationException("AWS_REGION environment variable is not set.")),
        Environment.GetEnvironmentVariable("ESCALATION_STATE_MACHINE_ARN")
            ?? throw new InvalidOperationException("ESCALATION_STATE_MACHINE_ARN environment variable is not set."))
    {
    }

    public RestartFunction(
        string connectionString, IEventPublisher eventPublisher, IEscalationStarter escalationStarter, string stateMachineArn)
    {
        _connectionString = connectionString;
        _eventPublisher = eventPublisher;
        _escalationStarter = escalationStarter;
        _stateMachineArn = stateMachineArn;
    }

    public Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context) =>
        SqsBatchProcessor.RunAsync(sqsEvent, context, WorkerName, record => HandleAsync(record, context));

    private async Task HandleAsync(SQSEvent.SQSMessage record, ILambdaContext context)
    {
        var envelope = EventBridgeEnvelope.Parse(record.Body);
        var detail = envelope.DeserializeDetail<IncidentUpdatedDetail>();
        EventSchemaValidator.Validate(detail);

        if (detail.Field != "Status" || detail.NewValue != "Reopened")
        {
            // Belt and suspenders: the EventBridge rule already filters to
            // this exact field/value, but a broader `incident.updated` rule
            // added later shouldn't silently start re-escalating on an
            // unrelated field change.
            return;
        }

        await using var db = WorkerDbContextFactory.Create(_connectionString, detail.OrganizationId);

        var (claimState, claimRecord) = await IdempotencyGuard.TryClaimAsync(db, WorkerName, detail.EventId, CancellationToken.None);
        if (claimState == ClaimState.AlreadyCompleted)
        {
            WorkerLog.Info(context, WorkerName, "Duplicate delivery, skipping.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId);
            return;
        }

        if (claimState == ClaimState.PendingCompletion)
        {
            // A prior attempt already committed the assignment/notification/
            // escalation-start business writes but crashed/failed before the
            // publish made it out. Don't re-run EscalationOrchestrator (that
            // would re-assign/re-notify) — just replay the captured outbox.
            WorkerLog.Info(context, WorkerName, "Retrying outbound publish for a previously-claimed event.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId);
            var pending = OutboxItem.DeserializeList(claimRecord.PendingOutboxJson);
            await OutboxPublisher.PublishAllAsync(_eventPublisher, null, _escalationStarter, pending, CancellationToken.None);
            await IdempotencyGuard.CompleteAsync(db, WorkerName, detail.EventId, CancellationToken.None);
            return;
        }

        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == detail.IncidentId);
        if (incident is null)
        {
            WorkerLog.Warn(context, WorkerName, "Incident no longer exists, skipping escalation restart.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { incidentId = detail.IncidentId });
            claimRecord.Completed = true;
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        var responderId = await EscalationOrchestrator.AssignAndMaybeEscalateAsync(
            db, _eventPublisher, _escalationStarter, _stateMachineArn,
            WorkerName, detail.EventId, claimRecord,
            detail.OrganizationId, detail.CorrelationId, incident, CancellationToken.None);

        if (responderId is null)
        {
            WorkerLog.Warn(context, WorkerName, "No on-call responder or escalation target could be resolved for reopened incident.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { incidentId = incident.Id });
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        WorkerLog.Info(context, WorkerName, "Escalation restarted for reopened incident.",
            detail.EventId, detail.OrganizationId, detail.CorrelationId, new { incidentId = incident.Id, responderId });
    }
}
