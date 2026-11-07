using Arisu.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arisu.Storage;

public sealed class FolderObjectStorageAdapter : IObjectStorageAdapter, IDisposable
{
    private const string EventCardArchiveDirectoryName = "guild-event-cards";

    private readonly SemaphoreSlim _archiveCleanupGate = new(1, 1);
    private readonly int _eventCardArchiveRetentionDays;
    private readonly ILogger<FolderObjectStorageAdapter> _logger;
    private readonly string _rootPath;

    public FolderObjectStorageAdapter(
        ObjectStorageOptions options,
        IHostEnvironment environment,
        ILogger<FolderObjectStorageAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        _logger = logger;
        _eventCardArchiveRetentionDays = options.LocalEventCardArchiveRetentionDays;
        _rootPath = Path.GetFullPath(options.FolderPath, environment.ContentRootPath);
    }

    public bool IsEnabled => true;

    public async Task UploadAsync(
        string objectKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        cancellationToken.ThrowIfCancellationRequested();

        if (!content.CanRead)
        {
            throw new ArgumentException("The object content stream must be readable.", nameof(content));
        }

        if (_eventCardArchiveRetentionDays == 0 && IsEventCardArchive(objectKey))
        {
            _logger.LogDebug("Skipped local archive for event card {ObjectKey}.", objectKey);
            return;
        }

        var objectPath = GetObjectPath(objectKey);
        var directoryPath = Path.GetDirectoryName(objectPath)
            ?? throw new InvalidOperationException("The object path must include a directory.");
        var temporaryPath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(objectPath)}.{Guid.NewGuid():N}.tmp");

        Directory.CreateDirectory(directoryPath);

        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                useAsync: true))
            {
                await content.CopyToAsync(destination, cancellationToken);
            }

            File.Move(temporaryPath, objectPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        _logger.LogDebug(
            "Stored object {ObjectKey} in local folder {FolderPath}.",
            objectKey,
            _rootPath);
    }

    public async Task<byte[]> DownloadAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        var objectPath = GetObjectPath(objectKey);
        var content = await File.ReadAllBytesAsync(objectPath, cancellationToken);

        _logger.LogDebug(
            "Read object {ObjectKey} from local folder {FolderPath}.",
            objectKey,
            _rootPath);

        return content;
    }

    public Task DeleteAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var objectPath = GetObjectPath(objectKey);
        File.Delete(objectPath);

        _logger.LogDebug(
            "Deleted object {ObjectKey} from local folder {FolderPath}.",
            objectKey,
            _rootPath);

        return Task.CompletedTask;
    }

    public void Dispose() => _archiveCleanupGate.Dispose();

    public async Task CleanupExpiredEventCardsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_eventCardArchiveRetentionDays == 0)
        {
            return;
        }

        await _archiveCleanupGate.WaitAsync(cancellationToken);

        try
        {
            var archivePath = Path.Combine(_rootPath, EventCardArchiveDirectoryName);
            if (!Directory.Exists(archivePath))
            {
                return;
            }

            var cutoff = GetArchiveRetentionCutoff(
                DateTime.UtcNow,
                _eventCardArchiveRetentionDays);
            var deletedFileCount = 0;

            foreach (var filePath in Directory.EnumerateFiles(
                         archivePath,
                         "*.png",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (File.GetLastWriteTimeUtc(filePath) >= cutoff)
                {
                    continue;
                }

                File.Delete(filePath);
                deletedFileCount++;
            }

            if (deletedFileCount > 0)
            {
                _logger.LogInformation(
                    "Deleted {DeletedFileCount} expired local event-card archive files.",
                    deletedFileCount);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not remove expired local event-card archives.");
        }
        finally
        {
            _archiveCleanupGate.Release();
        }
    }

    private string GetObjectPath(string objectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        if (Path.IsPathRooted(objectKey))
        {
            throw new ArgumentException("The object key must be a relative path.", nameof(objectKey));
        }

        var normalizedKey = objectKey
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var objectPath = Path.GetFullPath(Path.Combine(_rootPath, normalizedKey));
        var relativePath = Path.GetRelativePath(_rootPath, objectPath);

        if (relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException(
                "The object key must resolve inside the storage folder.",
                nameof(objectKey));
        }

        return objectPath;
    }

    private static bool IsEventCardArchive(string objectKey) =>
        objectKey.StartsWith(
            $"{EventCardArchiveDirectoryName}/",
            StringComparison.Ordinal);

    private static DateTime GetArchiveRetentionCutoff(DateTime now, int retentionDays)
    {
        var maximumRetentionDays = (int)Math.Floor((now - DateTime.MinValue).TotalDays);
        return now.AddDays(-Math.Min(retentionDays, maximumRetentionDays));
    }
}
