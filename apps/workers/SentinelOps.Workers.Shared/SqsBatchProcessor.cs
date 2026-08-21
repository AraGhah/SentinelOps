using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;

namespace SentinelOps.Workers.Shared;

// Every worker's SqsEventSource is configured with reportBatchItemFailures: true
// (see workerFunction() in event-processing-stack.ts) so that one bad message
// (malformed body, unknown schema version, a transient DB error, ...) only
// gets that message redelivered — not the other, unrelated messages in the
// same up-to-10-message batch. That guarantee only holds if the handler
// actually returns a partial SQSBatchResponse instead of letting an exception
// from record 3 abort records 4-10 outright, which is what this wraps.
public static class SqsBatchProcessor
{
    public static async Task<SQSBatchResponse> RunAsync(
        SQSEvent sqsEvent, ILambdaContext context, string worker, Func<SQSEvent.SQSMessage, Task> handleAsync)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();

        foreach (var record in sqsEvent.Records)
        {
            try
            {
                await handleAsync(record);
            }
            catch (Exception ex)
            {
                WorkerLog.Error(context, worker, "Unhandled exception processing message; item will be redelivered.", ex);
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
            }
        }

        return new SQSBatchResponse { BatchItemFailures = failures };
    }
}
