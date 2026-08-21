namespace SentinelOps.Workers.AttachmentScan;

// Mirrors AttachmentPolicy.StorageKeyPrefix in apps/api/Attachments; only needs
// to recognize the shape here, not construct it.
public static class StorageKey
{
    public static bool TryGetOrganizationId(string storageKey, out Guid organizationId)
    {
        organizationId = Guid.Empty;

        var segments = storageKey.Split('/');
        if (segments.Length < 4 || segments[0] != "orgs" || segments[2] != "incidents")
        {
            return false;
        }

        return Guid.TryParseExact(segments[1], "N", out organizationId);
    }
}
