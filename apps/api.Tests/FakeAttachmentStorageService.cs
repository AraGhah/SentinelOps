using System.Collections.Concurrent;
using SentinelOps.Api.Attachments;

namespace SentinelOps.Api.Tests;

// Replaces S3AttachmentStorageService in the test host — no real bucket in
// tests, just deterministic fake URLs plus a record of what was
// created/deleted so tests can assert against it.
public class FakeAttachmentStorageService : IAttachmentStorageService
{
    private readonly ConcurrentBag<string> _deleted = [];

    public IReadOnlyCollection<string> Deleted => _deleted;

    public PresignedUpload CreateUploadUrl(Guid organizationId, Guid incidentId, string fileName, string contentType)
    {
        var storageKey = $"{AttachmentPolicy.StorageKeyPrefix(organizationId, incidentId)}{Guid.NewGuid():N}-{fileName}";
        return new PresignedUpload(storageKey, $"https://fake-s3.test/{storageKey}", DateTimeOffset.UtcNow.AddMinutes(15));
    }

    public (string Url, DateTimeOffset ExpiresAtUtc) CreateDownloadUrl(string storageKey, string fileName) =>
        ($"https://fake-s3.test/{storageKey}", DateTimeOffset.UtcNow.AddMinutes(15));

    public Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        _deleted.Add(storageKey);
        return Task.CompletedTask;
    }
}
