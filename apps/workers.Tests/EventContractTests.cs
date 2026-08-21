using System.Text.Json;
using Json.Schema;
using SentinelOps.Events;

namespace SentinelOps.Workers.Tests;

// Contract tests for the wire shape SentinelOps.Events publishes onto
// EventBridge. infrastructure/event-schemas/*.schema.json is the documented
// source of truth an external consumer would validate against — these tests
// catch drift between that published contract and what the C# records in
// EventDetails.cs actually serialize to, which nothing else in the build
// verifies (EventSchemaValidator only checks a handful of runtime invariants,
// not the full shape).
public class EventContractTests
{
    private static readonly string SchemaDirectory = FindSchemaDirectory();

    public static IEnumerable<object[]> EventDetailSamples()
    {
        yield return new object[] { EventTypes.AlertReceived, SampleAlertReceived() };
        yield return new object[] { EventTypes.AlertValidated, SampleAlertValidated() };
        yield return new object[] { EventTypes.AlertRejected, SampleAlertRejected() };
        yield return new object[] { EventTypes.IncidentCreated, SampleIncidentCreated() };
        yield return new object[] { EventTypes.IncidentUpdated, SampleIncidentUpdated() };
        yield return new object[] { EventTypes.IncidentAcknowledged, SampleIncidentAcknowledged() };
        yield return new object[] { EventTypes.IncidentEscalated, SampleIncidentEscalated() };
        yield return new object[] { EventTypes.IncidentResolved, SampleIncidentResolved() };
        yield return new object[] { EventTypes.NotificationRequested, SampleNotificationRequested() };
        yield return new object[] { EventTypes.NotificationDelivered, SampleNotificationDelivered() };
        yield return new object[] { EventTypes.NotificationFailed, SampleNotificationFailed() };
    }

    [Theory]
    [MemberData(nameof(EventDetailSamples))]
    public void SerializedDetail_MatchesPublishedSchema(string detailType, IEventDetail detail)
    {
        var schema = LoadSchema(detailType);
        var json = JsonSerializer.SerializeToElement(detail, detail.GetType(), EventJson.Options);

        var result = schema.Evaluate(json, new EvaluationOptions
        {
            RequireFormatValidation = true,
            OutputFormat = OutputFormat.List,
        });

        Assert.True(result.IsValid, Describe(detailType, json, result));
    }

    [Theory]
    [MemberData(nameof(EventDetailSamples))]
    public void SchemaFile_DeclaresVersionSupportedByRuntimeValidator(string detailType, IEventDetail _)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SchemaPath(detailType)));
        var version = doc.RootElement.GetProperty("version").GetString();

        Assert.NotNull(version);
        Assert.Contains(version, EventSchemaValidator.SupportedVersions);
    }

    // Event-version compatibility: a detail stamped with a version this build
    // doesn't know about must fail both the runtime check every worker makes
    // before acting on a message, and schema validation against the "const"
    // the published contract pins schemaVersion to — the two checks should
    // never disagree about whether a version is acceptable.
    [Fact]
    public void UnknownSchemaVersion_FailsBothRuntimeAndSchemaValidation()
    {
        var detail = SampleAlertRejected() with { SchemaVersion = "2.0" };

        var ex = Assert.Throws<InvalidOperationException>(() => EventSchemaValidator.Validate(detail));
        Assert.Contains("Unsupported event schema version", ex.Message);

        var schema = LoadSchema(EventTypes.AlertRejected);
        var json = JsonSerializer.SerializeToElement(detail, detail.GetType(), EventJson.Options);
        var result = schema.Evaluate(json, new EvaluationOptions { RequireFormatValidation = true });
        Assert.False(result.IsValid);
    }

    [Fact]
    public void EveryDetailType_HasExactlyOneCorrespondingSchemaFile()
    {
        var detailTypes = new[]
        {
            EventTypes.AlertReceived, EventTypes.AlertValidated, EventTypes.AlertRejected,
            EventTypes.IncidentCreated, EventTypes.IncidentUpdated, EventTypes.IncidentAcknowledged,
            EventTypes.IncidentEscalated, EventTypes.IncidentResolved,
            EventTypes.NotificationRequested, EventTypes.NotificationDelivered, EventTypes.NotificationFailed,
        };

        foreach (var detailType in detailTypes)
        {
            Assert.True(File.Exists(SchemaPath(detailType)), $"Missing schema file for '{detailType}'.");
        }

        var schemaFileCount = Directory.GetFiles(SchemaDirectory, "*.schema.json").Length;
        Assert.Equal(detailTypes.Length, schemaFileCount);
    }

    private static AlertReceivedDetail SampleAlertReceived() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        Guid.NewGuid(), Guid.NewGuid(), "ext-1", "datadog", "High latency detected",
        Severity.High, DateTimeOffset.UtcNow, "production", "us-east-1");

    private static AlertValidatedDetail SampleAlertValidated() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        Guid.NewGuid(), "ext-1", "datadog", "High latency detected", Severity.High, "production", "us-east-1");

    private static AlertRejectedDetail SampleAlertRejected() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        Guid.NewGuid(), "Alert timestamp is too old.");

    private static IncidentCreatedDetail SampleIncidentCreated() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "High latency detected", Severity.High);

    private static IncidentUpdatedDetail SampleIncidentUpdated() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        Guid.NewGuid(), "Status", "Resolved", "Reopened");

    private static IncidentAcknowledgedDetail SampleIncidentAcknowledged() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid());

    private static IncidentEscalatedDetail SampleIncidentEscalated() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        Guid.NewGuid(), 1, 2, Guid.NewGuid());

    private static IncidentResolvedDetail SampleIncidentResolved() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid());

    private static NotificationRequestedDetail SampleNotificationRequested() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "email");

    private static NotificationDeliveredDetail SampleNotificationDelivered() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid());

    private static NotificationFailedDetail SampleNotificationFailed() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), "SES throttled the request.");

    private static string SchemaPath(string detailType) => Path.Combine(SchemaDirectory, $"{detailType}.schema.json");

    private static JsonSchema LoadSchema(string detailType) => JsonSchema.FromText(File.ReadAllText(SchemaPath(detailType)));

    private static string Describe(string detailType, JsonElement json, EvaluationResults result)
    {
        var errors = result.Details
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key} - {e.Value}"));
        return $"'{detailType}' failed schema validation for {json}:\n{string.Join('\n', errors)}";
    }

    private static string FindSchemaDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "infrastructure", "event-schemas");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate infrastructure/event-schemas above '{AppContext.BaseDirectory}'.");
    }
}
