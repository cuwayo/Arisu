namespace Arisu.Configuration;

public sealed class ObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";

    /// <summary>
    /// Gets a value indicating whether event cards and custom backgrounds use S3 object storage.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the folder used for event cards and custom backgrounds when S3 is disabled.
    /// </summary>
    public string FolderPath { get; init; } = "data/storage";

    /// <summary>
    /// Gets the number of days that local event-card archives are retained. Set to zero to disable
    /// local event-card archival.
    /// </summary>
    public int LocalEventCardArchiveRetentionDays { get; init; } = 30;

    /// <summary>
    /// Gets the S3 bucket that stores event-card archives and custom backgrounds.
    /// </summary>
    public string BucketName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the AWS region used to sign S3 requests.
    /// </summary>
    public string Region { get; init; } = "us-east-1";

    /// <summary>
    /// Gets an optional S3-compatible endpoint, such as MinIO or Cloudflare R2.
    /// </summary>
    public string? ServiceUrl { get; init; }

    /// <summary>
    /// Gets a value indicating whether the S3 client uses path-style bucket URLs.
    /// </summary>
    public bool ForcePathStyle { get; init; }

    /// <summary>
    /// Gets the optional access key. When omitted, the AWS default credential chain is used.
    /// </summary>
    public string? AccessKey { get; init; }

    /// <summary>
    /// Gets the optional secret key. When omitted, the AWS default credential chain is used.
    /// </summary>
    public string? SecretKey { get; init; }
}
