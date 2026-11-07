using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arisu.Services;

public sealed class GuildEventNotificationService : IHostedService
{
    private readonly IGuildEventChannelService _eventChannelService;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<GuildEventNotificationService> _logger;
    private bool _started;

    public GuildEventNotificationService(
        IGuildEventChannelService eventChannelService,
        DiscordSocketClient client,
        ILogger<GuildEventNotificationService> logger)
    {
        _eventChannelService = eventChannelService;
        _client = client;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _client.UserJoined += HandleUserJoinedAsync;
        _client.UserLeft += HandleUserLeftAsync;
        _client.GuildMemberUpdated += HandleGuildMemberUpdatedAsync;
        _started = true;

        _logger.LogInformation(
            "Guild event notifications subscribed to member join, leave, and boost events.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return Task.CompletedTask;
        }

        _client.UserJoined -= HandleUserJoinedAsync;
        _client.UserLeft -= HandleUserLeftAsync;
        _client.GuildMemberUpdated -= HandleGuildMemberUpdatedAsync;
        _started = false;

        return Task.CompletedTask;
    }

    private Task HandleUserJoinedAsync(SocketGuildUser member)
    {
        _logger.LogInformation(
            "Member {MemberId} joined guild {GuildId}.",
            member.Id,
            member.Guild.Id);

        return _eventChannelService.PublishAsync(
            new GuildEventNotification(
                member.Guild.Id,
                "Member joined",
                member.Id,
                GetMemberName(member),
                member.GetDisplayAvatarUrl(ImageFormat.Png, 256),
                $"Welcome to {member.Guild.Name}.",
                DateTimeOffset.UtcNow));
    }

    private Task HandleUserLeftAsync(SocketGuild guild, SocketUser user)
    {
        _logger.LogInformation(
            "Member {MemberId} left guild {GuildId}.",
            user.Id,
            guild.Id);

        return _eventChannelService.PublishAsync(
            new GuildEventNotification(
                guild.Id,
                "Member left",
                user.Id,
                GetMemberName(user),
                user.GetDisplayAvatarUrl(ImageFormat.Png, 256),
                $"Goodbye from {guild.Name}.",
                DateTimeOffset.UtcNow));
    }

    private Task HandleGuildMemberUpdatedAsync(
        Cacheable<SocketGuildUser, ulong> before,
        SocketGuildUser after)
    {
        if (!before.HasValue ||
            before.Value.PremiumSince is not null ||
            after.PremiumSince is null)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Member {MemberId} boosted guild {GuildId}.",
            after.Id,
            after.Guild.Id);

        return _eventChannelService.PublishAsync(
            new GuildEventNotification(
                after.Guild.Id,
                "Server boost",
                after.Id,
                GetMemberName(after),
                after.GetDisplayAvatarUrl(ImageFormat.Png, 256),
                $"Thanks for boosting {after.Guild.Name}!",
                DateTimeOffset.UtcNow));
    }

    private static string GetMemberName(IUser user) =>
        user.Discriminator == "0000"
            ? user.Username
            : $"{user.Username}#{user.Discriminator}";
}
