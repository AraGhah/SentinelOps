using System.ComponentModel.DataAnnotations;

namespace SentinelOps.Api.Attachments;

// Placeholder for a future presigned-upload flow: the caller supplies metadata
// for an already-uploaded object rather than bytes going through this API.
public record CreateAttachmentRequest(
    [Required, MaxLength(260)] string FileName,
    [Required, MaxLength(150)] string ContentType,
    [Range(1, long.MaxValue)] long SizeBytes,
    [Required, MaxLength(500)] string StorageKey);

public record AttachmentResponse(
    Guid Id, Guid IncidentId, string FileName, string ContentType, long SizeBytes, string StorageKey,
    Guid UploadedByUserId, DateTimeOffset CreatedAtUtc);
