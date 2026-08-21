using System.Collections.Concurrent;
using SentinelOps.Api.Attachments;

namespace SentinelOps.Api.Tests;

// Replaces S3AttachmentStorageService in the test host — no real bucket in
// tests, just deterministic fake URLs plus a record of what was
// created/deleted so tests can assert against it.
public class FakeAttachmentStorageService : IAttachmentStorageService
{
    private readonly ConcurrentBag<string> _deleted = [];
    private readonly ConcurrentDictionary<string, long> _objectSizes = new();

    public IReadOnlyCollection<string> Deleted => _deleted;

    // Tests default every minted key to a plausible small object so the
    // existing happy-path tests don't need to know about this. Call this to
    // simulate a client that actually uploaded a different number of bytes
    // than it later claims in Create.
    public void SetObjectSize(string storageKey, long sizeBytes) => _objectSizes[storageKey] = sizeBytes;

    public PresignedUpload CreateUploadUrl(Guid organizationId, Guid incidentId, string fileName, string contentType)
    {
        var storageKey = $"{AttachmentPolicy.StorageKeyPrefix(organizationId, incidentId)}{Guid.NewGuid():N}-{fileName}";
        _objectSizes[storageKey] = 2048;
        return new PresignedUpload(storageKey, $"https://fake-s3.test/{storageKey}", DateTimeOffset.UtcNow.AddMinutes(15));
    }

    public (string Url, DateTimeOffset ExpiresAtUtc) CreateDownloadUrl(string storageKey, string fileName) =>
        ($"https://fake-s3.test/{storageKey}", DateTimeOffset.UtcNow.AddMinutes(15));

    public Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        _deleted.Add(storageKey);
        return Task.CompletedTask;
    }

    public Task<long?> GetObjectSizeAsync(string storageKey, CancellationToken ct) =>
        Task.FromResult(_objectSizes.TryGetValue(storageKey, out var size) ? size : (long?)null);
}
