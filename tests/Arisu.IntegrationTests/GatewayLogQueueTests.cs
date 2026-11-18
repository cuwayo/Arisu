using Arisu.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class GatewayLogQueueTests
{
    [Fact]
    public async Task SlowDeliveryDoesNotBlockEnqueueAndShutdownDrainsInOrder()
    {
        using var queue = new GatewayLogQueue(NullLogger<GatewayLogQueue>.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<int>();
        await queue.StartAsync(default);
        Assert.True(queue.TryEnqueue(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            delivered.Add(1);
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(queue.TryEnqueue(_ =>
        {
            delivered.Add(2);
            return Task.CompletedTask;
        }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopping = queue.StopAsync(deadline.Token);
        Assert.False(stopping.IsCompleted);
        release.SetResult();
        await stopping;
        Assert.Equal([1, 2], delivered);
        Assert.False(queue.TryEnqueue(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task FullQueueRejectsNewEntriesWithoutDroppingAcceptedEntries()
    {
        using var queue = new GatewayLogQueue(NullLogger<GatewayLogQueue>.Instance);
        var count = 0;
        for (var index = 0; index < 1024; index++)
        {
            Assert.True(queue.TryEnqueue(_ =>
            {
                count++;
                return Task.CompletedTask;
            }));
        }

        Assert.False(queue.TryEnqueue(_ => throw new InvalidOperationException("Rejected work must not run.")));
        await queue.StartAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.StopAsync(deadline.Token);
        Assert.Equal(1024, count);
    }

    [Fact]
    public async Task FailedDeliveryDoesNotStopLaterEntries()
    {
        using var queue = new GatewayLogQueue(NullLogger<GatewayLogQueue>.Instance);
        var delivered = false;
        Assert.True(queue.TryEnqueue(_ => throw new InvalidOperationException("Simulated delivery failure.")));
        Assert.True(queue.TryEnqueue(_ =>
        {
            delivered = true;
            return Task.CompletedTask;
        }));
        await queue.StartAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.StopAsync(deadline.Token);
        Assert.True(delivered);
    }

    [Fact]
    public async Task ShutdownDeadlineCancelsInFlightDelivery()
    {
        using var queue = new GatewayLogQueue(NullLogger<GatewayLogQueue>.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(queue.TryEnqueue(async token =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled.SetResult();
                throw;
            }
        }));
        await queue.StartAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource();
        var stopping = queue.StopAsync(deadline.Token);
        deadline.Cancel();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
