using System.Runtime.CompilerServices;
using Amazon.XRay.Recorder.Handlers.AwsSdk;

namespace SentinelOps.Workers.Shared;

// Every worker Function class references this assembly (for WorkerLog,
// DbConnectionStringResolver, etc.), so the CLR loads
// SentinelOps.Workers.Shared.dll into every worker's execution environment —
// which is what makes a module initializer here run automatically before any
// worker code, with no per-project setup. Patches the AWS SDK's request
// pipeline so calls made during a Lambda invocation (SQS, DynamoDB,
// EventBridge, Secrets Manager, ...) become X-Ray subsegments of the
// invocation's own segment, which Lambda's ACTIVE tracing mode (set per
// function in EventProcessingStack/ApiStack) opens automatically.
internal static class WorkerXRayInitializer
{
    [ModuleInitializer]
    internal static void Initialize() => AWSSDKHandler.RegisterXRayForAllServices();
}
