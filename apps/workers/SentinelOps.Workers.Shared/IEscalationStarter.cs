using Amazon;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;

namespace SentinelOps.Workers.Shared;

// Narrow wrapper around the one Step Functions operation callers need
// (start the escalation state machine), so test doubles don't need all of
// IAmazonStepFunctions.
public interface IEscalationStarter
{
    Task StartExecutionAsync(string stateMachineArn, string executionName, string inputJson, CancellationToken ct);
}

public class StepFunctionsEscalationStarter : IEscalationStarter
{
    private readonly AmazonStepFunctionsClient _client;

    public StepFunctionsEscalationStarter(string region) =>
        _client = new AmazonStepFunctionsClient(RegionEndpoint.GetBySystemName(region));

    public Task StartExecutionAsync(string stateMachineArn, string executionName, string inputJson, CancellationToken ct) =>
        _client.StartExecutionAsync(new StartExecutionRequest
        {
            StateMachineArn = stateMachineArn,
            Name = executionName,
            Input = inputJson,
        }, ct);
}
