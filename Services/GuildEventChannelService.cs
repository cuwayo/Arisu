using Arisu.Storage;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Arisu.Services;

public interface IGuildEventChannelService
{
    Task<bool> SetEventChannelAsync(ulong guildId, ulong channelId);

    Task<bool> RemoveEventChannelAsync(ulong guildId);

    Task PublishAsync(GuildEventNotification notification);
}

public sealed record GuildEventNotification(
    ulong GuildId,
    string Event,
    ulong MemberId,
    string MemberName,
    string AvatarUrl,
    string Details,
    DateTimeOffset OccurredAt);

public sealed class GuildEventChannelService : IGuildEventChannelService
{
    private readonly DiscordSocketClient _client;
    private readonly IGuildChannelConfigurationStore _channelConfigurationStore;
    private readonly IGuildEventCardService _eventCardService;
    private readonly ILogger<GuildEventChannelService> _logger;
    private readonly IObjectStorageAdapter _objectStorage;

    public GuildEventChannelService(
        DiscordSocketClient client,
        IGuildChannelConfigurationStore channelConfigurationStore,
        IGuildEventCardService eventCardService,
        IObjectStorageAdapter objectStorage,
        ILogger<GuildEventChannelService> logger)
    {
        _client = client;
        _channelConfigurationStore = channelConfigurationStore;
        _eventCardService = eventCardService;
        _objectStorage = objectStorage;
        _logger = logger;
    }

    public async Task<bool> SetEventChannelAsync(ulong guildId, ulong channelId)
    {
        try
        {
            await _channelConfigurationStore.SetEventChannelIdAsync(guildId, channelId);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not save event channel {ChannelId} for guild {GuildId}.",
                channelId,
                guildId);
            return false;
        }
    }

    public async Task PublishAsync(GuildEventNotification notification)
    {
        var channelId = await GetEventChannelIdAsync(notification.GuildId);
        if (channelId is null)
        {
            _logger.LogWarning(
                "No event notification channel is configured for guild {GuildId}; {Event} for member {MemberId} was not sent.",
                notification.GuildId,
                notification.Event,
                notification.MemberId);
            return;
        }

        if (_client.GetChannel(channelId.Value) is not ITextChannel channel)
        {
            _logger.LogWarning(
                "Event channel {ChannelId} for guild {GuildId} is unavailable or is not a text channel.",
                channelId,
                notification.GuildId);
            return;
        }

        try
        {
            await using var card = await _eventCardService.CreateAsync(notification);
            await ArchiveCardAsync(card, notification);
            await channel.SendFileAsync(
                card,
                "member-event.png",
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not render or write a server event card to channel {ChannelId} in guild {GuildId}; sending the standard notification instead.",
                channelId,
                notification.GuildId);

            await PublishFallbackAsync(channel, notification, channelId.Value);
        }
    }

    public async Task<bool> RemoveEventChannelAsync(ulong guildId)
    {
        try
        {
            await _channelConfigurationStore.RemoveEventChannelIdAsync(guildId);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not remove the event channel for guild {GuildId}.",
                guildId);
            return false;
        }
    }

    private async Task<ulong?> GetEventChannelIdAsync(ulong guildId)
    {
        try
        {
            return await _channelConfigurationStore.GetEventChannelIdAsync(guildId);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not load the event channel for guild {GuildId}.",
                guildId);
            return null;
        }
    }

    private async Task ArchiveCardAsync(
        Stream card,
        GuildEventNotification notification)
    {
        var timestamp = notification.OccurredAt.UtcDateTime;
        var objectKey =
            $"guild-event-cards/{notification.GuildId}/{timestamp:yyyy/MM/dd}/" +
            $"{timestamp:yyyyMMddTHHmmssfffZ}-{notification.MemberId}-{Guid.NewGuid():N}.png";

        try
        {
            await _objectStorage.UploadAsync(objectKey, card, "image/png");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not archive event card {ObjectKey}; continuing with Discord delivery.",
                objectKey);
        }
        finally
        {
            if (card.CanSeek)
            {
                card.Position = 0;
            }
        }
    }

    private static Embed BuildEmbed(GuildEventNotification notification) =>
        new EmbedBuilder()
            .WithTitle("Server event")
            .WithColor(GetEventColor(notification.Event))
            .WithTimestamp(notification.OccurredAt)
            .AddField("Event", notification.Event, inline: true)
            .AddField("Member", $"<@{notification.MemberId}>", inline: true)
            .AddField("Details", notification.Details)
            .Build();

    private async Task PublishFallbackAsync(
        ITextChannel channel,
        GuildEventNotification notification,
        ulong channelId)
    {
        try
        {
            await channel.SendMessageAsync(
                embed: BuildEmbed(notification),
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not write the fallback server event notification to channel {ChannelId} in guild {GuildId}.",
                channelId,
                notification.GuildId);
        }
    }

    private static Color GetEventColor(string eventName) => eventName switch
    {
        "Member joined" => new Color(87, 242, 135),
        "Member left" => new Color(237, 66, 69),
        "Server boost" => new Color(255, 115, 250),
        _ => new Color(148, 155, 164)
    };
}
