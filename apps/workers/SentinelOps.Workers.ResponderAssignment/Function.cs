using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SentinelOps.Workers.ResponderAssignment;

// Consumes `incident.created`. Resolves the on-call responder from the
// schedule, falling back to EscalationPolicy level-1 targets. Assigns,
// notifies, and starts the escalation state machine when a policy applies
// (see SentinelOps.Workers.Escalation).
public class Function
{
    public const string WorkerName = "responder-assignment";

    private readonly string _connectionString;
    private readonly IEventPublisher _eventPublisher;
    private readonly IEscalationStarter _escalationStarter;
    private readonly string _stateMachineArn;

    public Function() : this(
        DbConnectionStringResolver.FromEnvironment(),
        EventBridgeEventPublisher.FromEnvironment(),
        new StepFunctionsEscalationStarter(
            Environment.GetEnvironmentVariable("AWS_REGION")
                ?? throw new InvalidOperationException("AWS_REGION environment variable is not set.")),
        Environment.GetEnvironmentVariable("ESCALATION_STATE_MACHINE_ARN")
            ?? throw new InvalidOperationException("ESCALATION_STATE_MACHINE_ARN environment variable is not set."))
    {
    }

    public Function(string connectionString, IEventPublisher eventPublisher, IEscalationStarter escalationStarter, string stateMachineArn)
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
        var detail = envelope.DeserializeDetail<IncidentCreatedDetail>();
        EventSchemaValidator.Validate(detail);

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
            // Business writes already committed; only the publish failed. Replay
            // the captured outbox instead of re-running EscalationOrchestrator,
            // which would re-assign/re-notify.
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
            WorkerLog.Warn(context, WorkerName, "Incident no longer exists, skipping assignment.",
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
            WorkerLog.Warn(context, WorkerName, "No on-call responder or escalation target could be resolved.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { incidentId = incident.Id });
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        WorkerLog.Info(context, WorkerName, "Responder assigned.",
            detail.EventId, detail.OrganizationId, detail.CorrelationId, new { incidentId = incident.Id, responderId });
    }
}
