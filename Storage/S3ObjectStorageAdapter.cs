using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Arisu.Configuration;
using Microsoft.Extensions.Logging;

namespace Arisu.Storage;

public interface IObjectStorageAdapter
{
    bool IsEnabled { get; }

    Task CleanupExpiredEventCardsAsync(CancellationToken cancellationToken = default);

    Task UploadAsync(
        string objectKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    Task<byte[]> DownloadAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string objectKey,
        CancellationToken cancellationToken = default);
}

public sealed class S3ObjectStorageAdapter : IObjectStorageAdapter, IDisposable
{
    private readonly string _bucketName;
    private readonly IAmazonS3 _client;
    private readonly ILogger<S3ObjectStorageAdapter> _logger;

    public S3ObjectStorageAdapter(
        ObjectStorageOptions options,
        ILogger<S3ObjectStorageAdapter> logger)
    {
        _bucketName = options.BucketName;
        _logger = logger;

        var configuration = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region)
        };

        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            configuration.ServiceURL = options.ServiceUrl;
        }

        _client = HasConfiguredCredentials(options)
            ? new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKey, options.SecretKey),
                configuration)
            : new AmazonS3Client(configuration);
    }

    public bool IsEnabled => true;

    public Task CleanupExpiredEventCardsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task UploadAsync(
        string objectKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        if (!content.CanRead)
        {
            throw new ArgumentException("The object content stream must be readable.", nameof(content));
        }

        await _client.PutObjectAsync(
            new PutObjectRequest
            {
                AutoCloseStream = false,
                BucketName = _bucketName,
                ContentType = contentType,
                InputStream = content,
                Key = objectKey
            },
            cancellationToken);

        _logger.LogDebug(
            "Uploaded object {ObjectKey} to S3 bucket {BucketName}.",
            objectKey,
            _bucketName);
    }

    public async Task<byte[]> DownloadAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        using var response = await _client.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = objectKey
            },
            cancellationToken);
        await using var source = response.ResponseStream;
        await using var destination = new MemoryStream();
        await source.CopyToAsync(destination, cancellationToken);

        _logger.LogDebug(
            "Downloaded object {ObjectKey} from S3 bucket {BucketName}.",
            objectKey,
            _bucketName);

        return destination.ToArray();
    }

    public async Task DeleteAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        await _client.DeleteObjectAsync(
            new DeleteObjectRequest
            {
                BucketName = _bucketName,
                Key = objectKey
            },
            cancellationToken);

        _logger.LogDebug(
            "Deleted object {ObjectKey} from S3 bucket {BucketName}.",
            objectKey,
            _bucketName);
    }

    public void Dispose() => _client.Dispose();

    private static bool HasConfiguredCredentials(ObjectStorageOptions options) =>
        !string.IsNullOrWhiteSpace(options.AccessKey) &&
        !string.IsNullOrWhiteSpace(options.SecretKey);
}
