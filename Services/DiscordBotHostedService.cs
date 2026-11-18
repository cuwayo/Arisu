using Arisu.Configuration;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arisu.Services;

public sealed class DiscordBotHostedService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordBotHostedService> _logger;
    private bool _started;
    private bool _subscribed;

    public DiscordBotHostedService(
        DiscordSocketClient client,
        IOptions<DiscordOptions> options,
        ILogger<DiscordBotHostedService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Log += LogAsync;
        _client.Ready += SetActivityAsync;
        _subscribed = true;

        try
        {
            await _client.LoginAsync(TokenType.Bot, _options.Token).WaitAsync(cancellationToken);
            await _client.StartAsync().WaitAsync(cancellationToken);

            _started = true;
            _logger.LogInformation("Discord gateway client started.");
        }
        catch
        {
            Unsubscribe();
            try
            {
                await _client.StopAsync();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not stop the Discord gateway client after startup failed.");
            }

            try
            {
                await _client.LogoutAsync();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not log out the Discord gateway client after startup failed.");
            }

            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started && !_subscribed)
        {
            return;
        }

        _started = false;
        Unsubscribe();
        Exception? stopException = null;
        Exception? logoutException = null;

        try
        {
            await _client.StopAsync().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopException = exception;
            _logger.LogError(exception, "Could not stop the Discord gateway client.");
        }
        finally
        {
            try
            {
                await _client.LogoutAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logoutException = exception;
                _logger.LogError(exception, "Could not log out the Discord gateway client.");
            }
        }

        if (stopException is not null || logoutException is not null)
        {
            throw new AggregateException(
                "One or more errors occurred while stopping the Discord gateway client.",
                new[] { stopException, logoutException }.OfType<Exception>());
        }

        _logger.LogInformation("Discord gateway client stopped.");
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _client.Log -= LogAsync;
        _client.Ready -= SetActivityAsync;
        _subscribed = false;
    }

    private async Task SetActivityAsync()
    {
        try
        {
            await _client.SetGameAsync("✦ moonlit melodies", type: ActivityType.Listening);
            _logger.LogInformation("Discord bot activity set to listening to moonlit melodies.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not set the Discord bot activity.");
        }
    }

    private Task LogAsync(LogMessage message)
    {
        _logger.Log(
            ToLogLevel(message.Severity),
            message.Exception,
            "{Source}: {Message}",
            message.Source,
            message.Message);

        return Task.CompletedTask;
    }

    private static LogLevel ToLogLevel(LogSeverity severity) => severity switch
    {
        LogSeverity.Critical => LogLevel.Critical,
        LogSeverity.Error => LogLevel.Error,
        LogSeverity.Warning => LogLevel.Warning,
        LogSeverity.Info => LogLevel.Information,
        _ => LogLevel.Debug
    };
}
