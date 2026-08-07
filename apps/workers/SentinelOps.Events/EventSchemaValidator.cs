namespace SentinelOps.Events;

// Cheap structural check, not full JSON Schema validation — the JSON Schema files
// under infrastructure/event-schemas/ are the documented source of truth for the
// wire shape; this just guards against a version this build doesn't know how to
// handle, and the handful of "must never be empty" invariants every event shares.
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
