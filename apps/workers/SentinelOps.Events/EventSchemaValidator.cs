namespace SentinelOps.Events;

// Cheap structural check, not full JSON Schema validation (see infrastructure/event-schemas/
// for the actual wire schema). Just guards unsupported versions and required fields.
public static class EventSchemaValidator
{
    public static readonly IReadOnlySet<string> SupportedVersions = new HashSet<string> { "1.0" };

    public static void Validate(IEventDetail detail)
    {
        if (!SupportedVersions.Contains(detail.SchemaVersion))
        {
            throw new InvalidOperationException(
                $"Unsupported event schema version '{detail.SchemaVersion}' (supported: {string.Join(", ", SupportedVersions)}).");
        }

        if (detail.EventId == Guid.Empty) throw new InvalidOperationException("Event is missing an EventId.");
        if (detail.OrganizationId == Guid.Empty) throw new InvalidOperationException("Event is missing an OrganizationId.");
        if (detail.CorrelationId == Guid.Empty) throw new InvalidOperationException("Event is missing a CorrelationId.");
    }
}
