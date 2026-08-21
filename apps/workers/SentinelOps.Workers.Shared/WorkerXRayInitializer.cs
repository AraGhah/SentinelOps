using System.Runtime.CompilerServices;
using Amazon.XRay.Recorder.Handlers.AwsSdk;

namespace SentinelOps.Workers.Shared;

// Runs automatically before any worker code, since every worker references
// this assembly. Patches the AWS SDK pipeline so SDK calls during an
// invocation become X-Ray subsegments of Lambda's ACTIVE-tracing segment.
internal static class WorkerXRayInitializer
{
    [ModuleInitializer]
    internal static void Initialize() => AWSSDKHandler.RegisterXRayForAllServices();
}
