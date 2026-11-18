using System.Reflection;
using Arisu.Configuration;
using Arisu.Modules;
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
    private readonly ComfyUiOptions _comfyUiOptions;
    private readonly DiscordOptions _options;
    private readonly ILogger<InteractionHandler> _logger;
    private readonly object _registrationLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private const int MaximumRememberedFailureIds = 1_024;
    private readonly HashSet<ulong> _reportedFailures = [];
    private readonly Queue<ulong> _reportedFailureOrder = new();
    private readonly object _failureReportLock = new();
    private Task? _registrationTask;
    private bool _started;

    public InteractionHandler(
        DiscordSocketClient client,
        InteractionService commands,
        IServiceProvider services,
        IOptions<ComfyUiOptions> comfyUiOptions,
        IOptions<DiscordOptions> options,
        ILogger<InteractionHandler> logger)
    {
        _client = client;
        _commands = commands;
        _services = services;
        _comfyUiOptions = comfyUiOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var assembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("Could not locate the entry assembly.");

        if (_comfyUiOptions.Enabled)
        {
            await _commands.AddModulesAsync(assembly, _services);
        }
        else
        {
            foreach (var moduleType in assembly.DefinedTypes.Where(type =>
                         type is { IsAbstract: false, IsClass: true } &&
                         type.DeclaringType is null &&
                         type.AsType() != typeof(ImagineModule) &&
                         typeof(IInteractionModuleBase).IsAssignableFrom(type)))
            {
                await _commands.AddModuleAsync(moduleType, _services);
            }
        }

        _client.InteractionCreated += HandleInteractionAsync;
        _client.Ready += OnReadyAsync;
        _commands.Log += LogAsync;
        _commands.SlashCommandExecuted += HandleCommandExecutedAsync;
        _commands.ContextCommandExecuted += HandleCommandExecutedAsync;
        _commands.ComponentCommandExecuted += HandleCommandExecutedAsync;
        _commands.ModalCommandExecuted += HandleCommandExecutedAsync;
        _started = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        Unsubscribe();

        Task? registrationTask;
        lock (_registrationLock)
        {
            registrationTask = _registrationTask;
        }

        if (registrationTask is not null)
        {
            try
            {
                await registrationTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Dispose()
    {
        Unsubscribe();
        _stopping.Dispose();
    }

    private void Unsubscribe()
    {
        if (!_started)
        {
            return;
        }

        _client.InteractionCreated -= HandleInteractionAsync;
        _client.Ready -= OnReadyAsync;
        _commands.Log -= LogAsync;
        _commands.SlashCommandExecuted -= HandleCommandExecutedAsync;
        _commands.ContextCommandExecuted -= HandleCommandExecutedAsync;
        _commands.ComponentCommandExecuted -= HandleCommandExecutedAsync;
        _commands.ModalCommandExecuted -= HandleCommandExecutedAsync;
        _started = false;
    }

    private async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        try
        {
            var context = new SocketInteractionContext(_client, interaction);
            var result = await _commands.ExecuteCommandAsync(context, _services);

            if (!result.IsSuccess)
            {
                var commandName = GetInteractionName(interaction);
                LogCommandFailure(interaction, commandName, result);
                await ReportFailureAsync(interaction, result);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not dispatch interaction {InteractionId} of type {InteractionType}.",
                interaction.Id,
                interaction.Type);
            await ReportFailureSafelyAsync(interaction, "unknown", exception);
        }
    }

    private Task OnReadyAsync()
    {
        lock (_registrationLock)
        {
            if (_registrationTask is null || _registrationTask.IsCompleted)
            {
                _registrationTask = RegisterCommandsWithRetryAsync(_stopping.Token);
            }
        }

        return Task.CompletedTask;
    }

    private async Task RegisterCommandsWithRetryAsync(CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
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

                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == maxAttempts)
                {
                    _logger.LogError(
                        exception,
                        "Could not register interaction commands after {AttemptCount} attempts.",
                        maxAttempts);
                    return;
                }

                var delay = TimeSpan.FromSeconds(Math.Min(30, 2 * attempt));
                _logger.LogWarning(
                    exception,
                    "Could not register interaction commands on attempt {Attempt} of {MaxAttempts}; retrying in {DelaySeconds} seconds.",
                    attempt,
                    maxAttempts,
                    delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task HandleCommandExecutedAsync(ICommandInfo? command, IInteractionContext context, IResult result)
    {
        if (result.IsSuccess)
        {
            return;
        }

        var interaction = context.Interaction;
        var commandName = command?.Name ?? GetInteractionName(interaction);

        try
        {
            LogCommandFailure(interaction, commandName, result);
            await ReportFailureAsync(interaction, result);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not handle completion for command {CommandName}, interaction {InteractionId}.",
                commandName,
                interaction.Id);
        }
    }

    private static string GetInteractionName(IDiscordInteraction interaction) => interaction switch
    {
        ISlashCommandInteraction slash => slash.Data.Name,
        IUserCommandInteraction user => user.Data.Name,
        IMessageCommandInteraction message => message.Data.Name,
        IComponentInteraction component => component.Data.CustomId,
        IModalInteraction modal => modal.Data.CustomId,
        _ => interaction.Type.ToString()
    };

    private void LogCommandFailure(IDiscordInteraction interaction, string commandName, IResult result)
    {
        if (result is ExecuteResult { Exception: { } exception })
        {
            _logger.LogError(
                exception,
                "Command {CommandName} failed for interaction {InteractionId}.",
                commandName,
                interaction.Id);
            return;
        }

        _logger.LogWarning(
            "Command {CommandName} failed for interaction {InteractionId} with {Error}: {ErrorReason}.",
            commandName,
            interaction.Id,
            result.Error,
            result.ErrorReason);
    }

    private async Task ReportFailureSafelyAsync(IDiscordInteraction interaction, string commandName, Exception exception)
    {
        try
        {
            await ReportFailureAsync(interaction, ExecuteResult.FromError(exception));
        }
        catch (Exception reportException)
        {
            _logger.LogWarning(
                reportException,
                "Could not report failure for command {CommandName}, interaction {InteractionId}.",
                commandName,
                interaction.Id);
        }
    }

    private async Task ReportFailureAsync(IDiscordInteraction interaction, IResult result)
    {
        lock (_failureReportLock)
        {
            if (!_reportedFailures.Add(interaction.Id))
            {
                return;
            }

            _reportedFailureOrder.Enqueue(interaction.Id);
            if (_reportedFailureOrder.Count > MaximumRememberedFailureIds)
            {
                _reportedFailures.Remove(_reportedFailureOrder.Dequeue());
            }
        }

        var response = result.Error == InteractionCommandError.UnmetPrecondition
            ? LimitResponse(result.ErrorReason ?? "You do not have permission to use this command.")
            : "I could not complete that command.";

        try
        {
            if (!interaction.HasResponded)
            {
                await interaction.RespondAsync(
                    response,
                    ephemeral: true,
                    allowedMentions: AllowedMentions.None);
                return;
            }

            var original = await interaction.GetOriginalResponseAsync();
            if ((original.Flags & MessageFlags.Ephemeral) != 0 ||
                (original.Flags & MessageFlags.Loading) != 0)
            {
                await interaction.ModifyOriginalResponseAsync(properties =>
                {
                    properties.Content = response;
                    properties.AllowedMentions = AllowedMentions.None;
                });
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not send failure response for interaction {InteractionId}.",
                interaction.Id);
        }
    }

    private static string LimitResponse(string response)
    {
        const int maxLength = 1800;
        return response.Length <= maxLength ? response : response[..maxLength];
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
