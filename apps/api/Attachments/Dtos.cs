using System.ComponentModel.DataAnnotations;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Attachments;

public record CreateUploadUrlRequest(
    [Required, MaxLength(260)] string FileName,
    [Required, MaxLength(150)] string ContentType,
    [Range(1, long.MaxValue)] long SizeBytes);

public record UploadUrlResponse(string StorageKey, string UploadUrl, DateTimeOffset ExpiresAtUtc);

// StorageKey must match this org/incident's presign prefix (see AttachmentPolicy.StorageKeyPrefix).
public record CreateAttachmentRequest(
    [Required, MaxLength(260)] string FileName,
    [Required, MaxLength(150)] string ContentType,
    [Range(1, long.MaxValue)] long SizeBytes,
    [Required, MaxLength(500)] string StorageKey);

public record AttachmentResponse(
    Guid Id, Guid IncidentId, string FileName, string ContentType, long SizeBytes, string StorageKey,
    Guid UploadedByUserId, AttachmentScanStatus ScanStatus, DateTimeOffset CreatedAtUtc);

public record DownloadUrlResponse(string DownloadUrl, DateTimeOffset ExpiresAtUtc);
