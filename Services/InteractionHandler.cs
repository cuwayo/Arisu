using System.Reflection;
using Arisu.Configuration;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arisu.Services;

public sealed class InteractionHandler : IHostedService, IDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _commands;
    private readonly IServiceProvider _services;
    private readonly DiscordOptions _options;
    private readonly ILogger<InteractionHandler> _logger;
    private readonly SemaphoreSlim _registrationLock = new(1, 1);
    private bool _commandsRegistered;
    private bool _started;

    public InteractionHandler(
        DiscordSocketClient client,
        InteractionService commands,
        IServiceProvider services,
        IOptions<DiscordOptions> options,
        ILogger<InteractionHandler> logger)
    {
        _client = client;
        _commands = commands;
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("Could not locate the entry assembly.");

        await _commands.AddModulesAsync(assembly, _services);

        _client.InteractionCreated += HandleInteractionAsync;
        _client.Ready += RegisterCommandsAsync;
        _commands.Log += LogAsync;
        _started = true;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return Task.CompletedTask;
        }

        _client.InteractionCreated -= HandleInteractionAsync;
        _client.Ready -= RegisterCommandsAsync;
        _commands.Log -= LogAsync;
        _started = false;

        return Task.CompletedTask;
    }

    public void Dispose() => _registrationLock.Dispose();

    private async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        var context = new SocketInteractionContext(_client, interaction);
        var result = await _commands.ExecuteCommandAsync(context, _services);

        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "Interaction {InteractionId} failed: {Error}",
                interaction.Id,
                result.ErrorReason);

            if (!interaction.HasResponded)
            {
                var response = result.Error == InteractionCommandError.UnmetPrecondition
                    ? result.ErrorReason ?? "You do not have permission to use this command."
                    : "I could not complete that command.";

                await interaction.RespondAsync(response, ephemeral: true);
            }
        }
    }

    private async Task RegisterCommandsAsync()
    {
        await _registrationLock.WaitAsync();

        try
        {
            if (_commandsRegistered)
            {
                return;
            }

            if (_options.DevelopmentGuildId is { } guildId)
            {
                await _commands.RegisterCommandsToGuildAsync(guildId, deleteMissing: true);
                _logger.LogInformation("Registered interaction commands in development guild {GuildId}.", guildId);
            }
            else
            {
                await _commands.RegisterCommandsGloballyAsync(deleteMissing: true);
                _logger.LogInformation("Registered interaction commands globally.");
            }

            _commandsRegistered = true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not register interaction commands.");
        }
        finally
        {
            _registrationLock.Release();
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
