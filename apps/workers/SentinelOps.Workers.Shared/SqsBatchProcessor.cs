using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;

namespace SentinelOps.Workers.Shared;

// Workers run with reportBatchItemFailures: true, so only failed messages get
// redelivered. Must return a partial SQSBatchResponse instead of throwing,
// or one bad record fails the whole batch.
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
