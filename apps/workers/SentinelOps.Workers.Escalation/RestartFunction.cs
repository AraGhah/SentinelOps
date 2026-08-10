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
        Environment.GetEnvironmentVariable("CONNECTION_STRING")
            ?? throw new InvalidOperationException("CONNECTION_STRING environment variable is not set."),
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

    public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        foreach (var record in sqsEvent.Records)
        {
            await HandleAsync(record, context);
        }
    }

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

        if (!await IdempotencyGuard.TryClaimAsync(db, WorkerName, detail.EventId, CancellationToken.None))
        {
            WorkerLog.Info(context, WorkerName, "Duplicate delivery, skipping.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId);
            return;
        }

        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == detail.IncidentId);
        if (incident is null)
        {
            WorkerLog.Warn(context, WorkerName, "Incident no longer exists, skipping escalation restart.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { incidentId = detail.IncidentId });
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        var responderId = await EscalationOrchestrator.AssignAndMaybeEscalateAsync(
            db, _eventPublisher, _escalationStarter, _stateMachineArn,
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
