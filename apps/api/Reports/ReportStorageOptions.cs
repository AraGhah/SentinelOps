using System.ComponentModel.DataAnnotations;

namespace SentinelOps.Api.Reports;

// No Region of its own: reports share the IAmazonS3 client already registered
// for Attachments (same AWS account/region), so this only needs a bucket name.
public class ReportStorageOptions
{
    public const string SectionName = "Aws:Reports";

    [Required] public string BucketName { get; set; } = "";
}
