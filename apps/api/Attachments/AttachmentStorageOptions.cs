using System.ComponentModel.DataAnnotations;

namespace SentinelOps.Api.Attachments;

public class AttachmentStorageOptions
{
    public const string SectionName = "Aws:Attachments";

    [Required] public string BucketName { get; set; } = "";
    [Required] public string Region { get; set; } = "";
}
