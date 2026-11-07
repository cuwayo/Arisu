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

        await _client.LoginAsync(TokenType.Bot, _options.Token);
        await _client.StartAsync();

        _started = true;
        _logger.LogInformation("Discord gateway client started.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return;
        }

        await _client.StopAsync();
        await _client.LogoutAsync();

        _client.Log -= LogAsync;
        _client.Ready -= SetActivityAsync;
        _started = false;
        _logger.LogInformation("Discord gateway client stopped.");
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
