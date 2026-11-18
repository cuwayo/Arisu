using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arisu.Services;

/// <summary>
/// Runs bounded log delivery outside Discord's serial gateway callbacks.
/// </summary>
public sealed class GatewayLogQueue : BackgroundService
{
    private const int Capacity = 1024;
    private readonly Channel<Func<CancellationToken, Task>> _pending =
        Channel.CreateBounded<Func<CancellationToken, Task>>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    private readonly ILogger<GatewayLogQueue> _logger;
    private long _dropped;

    public GatewayLogQueue(ILogger<GatewayLogQueue> logger) => _logger = logger;

    public bool TryEnqueue(Func<CancellationToken, Task> deliver)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        if (_pending.Writer.TryWrite(deliver))
        {
            return true;
        }

        var dropped = Interlocked.Increment(ref _dropped);
        if (dropped == 1 || dropped % 100 == 0)
        {
            _logger.LogWarning("Gateway log queue is full or stopping; {DroppedCount} log entries were dropped.", dropped);
        }

        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var deliver in _pending.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await deliver(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Could not deliver a queued gateway log entry.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _pending.Writer.TryComplete();
        try
        {
            if (ExecuteTask is { } worker)
            {
                await worker.WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Shutdown deadline reached before queued gateway logs finished; pending entries will be lost.");
        }
        finally
        {
            await base.StopAsync(cancellationToken);
        }
    }
}
