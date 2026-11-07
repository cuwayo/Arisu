using Arisu.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arisu.Services;

public sealed class ObjectStorageMaintenanceService : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    private readonly ILogger<ObjectStorageMaintenanceService> _logger;
    private readonly IObjectStorageAdapter _objectStorage;

    public ObjectStorageMaintenanceService(
        IObjectStorageAdapter objectStorage,
        ILogger<ObjectStorageMaintenanceService> logger)
    {
        _objectStorage = objectStorage;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _objectStorage.CleanupExpiredEventCardsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not complete object storage maintenance.");
            }

            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
