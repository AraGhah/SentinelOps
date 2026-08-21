using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Workers.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SentinelOps.Workers.AttachmentScan;

// Consumes GuardDuty Malware Protection for S3 "Object Scan Result" findings
// (arrive on the account's default EventBridge bus, not sentinelops-events —
// see AttachmentScanResultRule in infrastructure-stack.ts) and updates the
// matching Attachment's ScanStatus. The finding itself doesn't carry an
// OrganizationId, so this recovers one from the object key — every key an
// upload-url request mints follows AttachmentPolicy.StorageKeyPrefix's
// "orgs/{orgId:N}/incidents/{incidentId:N}/..." shape.
public class Function
{
    public const string WorkerName = "attachment-scan";

    private readonly string _connectionString;

    public Function() : this(
        DbConnectionStringResolver.FromEnvironment())
    {
    }

    public Function(string connectionString) => _connectionString = connectionString;

    public Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context) =>
        SqsBatchProcessor.RunAsync(sqsEvent, context, WorkerName, record => HandleAsync(record, context));

    private async Task HandleAsync(SQSEvent.SQSMessage record, ILambdaContext context)
    {
        var finding = GuardDutyMalwareFinding.Parse(record.Body);
        if (finding is null)
        {
            WorkerLog.Warn(context, WorkerName, "Could not parse GuardDuty malware finding, skipping.",
                Guid.NewGuid(), Guid.Empty, Guid.Empty);
            return;
        }

        if (!StorageKey.TryGetOrganizationId(finding.ObjectKey, out var organizationId))
        {
            WorkerLog.Warn(context, WorkerName, "Object key is not a SentinelOps attachment key, skipping.",
                finding.EventId, Guid.Empty, Guid.Empty, new { finding.ObjectKey });
            return;
        }

        await using var db = WorkerDbContextFactory.Create(_connectionString, organizationId);

        var (claimState, claimRecord) = await IdempotencyGuard.TryClaimAsync(db, WorkerName, finding.EventId, CancellationToken.None);
        if (claimState != ClaimState.Claimed)
        {
            WorkerLog.Info(context, WorkerName, "Duplicate delivery, skipping.", finding.EventId, organizationId, Guid.NewGuid());
            return;
        }

        // No outbound publish here — the row write is the entire unit of
        // work, so the claim is Completed in the same commit either way.
        claimRecord.Completed = true;

        var attachment = await db.Attachments.FirstOrDefaultAsync(a => a.StorageKey == finding.ObjectKey);
        if (attachment is null)
        {
            WorkerLog.Warn(context, WorkerName, "No attachment matches this object key, skipping.",
                finding.EventId, organizationId, Guid.NewGuid(), new { finding.ObjectKey });
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        attachment.ScanStatus = finding.Status;
        await db.SaveChangesAsync(CancellationToken.None);

        WorkerLog.Info(context, WorkerName, "Attachment scan result recorded.",
            finding.EventId, organizationId, Guid.NewGuid(), new { attachment.Id, finding.Status });
    }
}
