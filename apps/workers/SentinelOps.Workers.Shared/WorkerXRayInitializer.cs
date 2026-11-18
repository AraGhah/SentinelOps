using System.Runtime.CompilerServices;
using Amazon.XRay.Recorder.Handlers.AwsSdk;

namespace SentinelOps.Workers.Shared;

// Runs automatically before any worker code, since every worker references
// this assembly. Patches the AWS SDK pipeline so SDK calls during an
// invocation become X-Ray subsegments of Lambda's ACTIVE-tracing segment.
internal static class WorkerXRayInitializer
{
    // CA2255 discourages module initializers in libraries. This one is deliberate: the
    // workers are bare Lambda handlers with no startup hook of their own, so this shared
    // assembly is the one place that runs before any worker code.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize() => AWSSDKHandler.RegisterXRayForAllServices();
}
