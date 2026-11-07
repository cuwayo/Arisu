using Arisu.Storage;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Arisu.Services;

public interface IMemberLogChannelService
{
    Task<bool> SetMemberLogChannelAsync(ulong guildId, ulong channelId);

    Task<bool> RemoveMemberLogChannelAsync(ulong guildId);

    Task PublishAsync(MemberLogEntry entry);
}

public sealed record MemberLogEntry(
    ulong GuildId,
    string Event,
    ulong? MemberId,
    string Details,
    string? ThumbnailUrl,
    DateTimeOffset OccurredAt);

public sealed class MemberLogChannelService : IMemberLogChannelService
{
    private readonly DiscordSocketClient _client;
    private readonly IGuildChannelConfigurationStore _channelConfigurationStore;
    private readonly ILogger<MemberLogChannelService> _logger;

    public MemberLogChannelService(
        DiscordSocketClient client,
        IGuildChannelConfigurationStore channelConfigurationStore,
        ILogger<MemberLogChannelService> logger)
    {
        _client = client;
        _channelConfigurationStore = channelConfigurationStore;
        _logger = logger;
    }

    public async Task<bool> SetMemberLogChannelAsync(ulong guildId, ulong channelId)
    {
        try
        {
            await _channelConfigurationStore.SetMemberLogChannelIdAsync(guildId, channelId);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not save member log channel {ChannelId} for guild {GuildId}.",
                channelId,
                guildId);
            return false;
        }
    }

    public async Task<bool> RemoveMemberLogChannelAsync(ulong guildId)
    {
        try
        {
            await _channelConfigurationStore.RemoveMemberLogChannelIdAsync(guildId);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not remove the member log channel for guild {GuildId}.",
                guildId);
            return false;
        }
    }

    public async Task PublishAsync(MemberLogEntry entry)
    {
        var channelId = await GetMemberLogChannelIdAsync(entry.GuildId);
        if (channelId is null)
        {
            return;
        }

        if (_client.GetChannel(channelId.Value) is not ITextChannel channel)
        {
            _logger.LogWarning(
                "Member log channel {ChannelId} for guild {GuildId} is unavailable or is not a text channel.",
                channelId,
                entry.GuildId);
            return;
        }

        try
        {
            await channel.SendMessageAsync(
                embed: BuildEmbed(entry),
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not write member log entry to channel {ChannelId} in guild {GuildId}.",
                channelId,
                entry.GuildId);
        }
    }

    private async Task<ulong?> GetMemberLogChannelIdAsync(ulong guildId)
    {
        try
        {
            return await _channelConfigurationStore.GetMemberLogChannelIdAsync(guildId);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not load the member log channel for guild {GuildId}.",
                guildId);
            return null;
        }
    }

    private static Embed BuildEmbed(MemberLogEntry entry)
    {
        var builder = new EmbedBuilder()
            .WithTitle("Member log")
            .WithColor(GetEventColor(entry.Event))
            .WithTimestamp(entry.OccurredAt)
            .AddField("Event", entry.Event, inline: true)
            .AddField("Details", entry.Details);

        if (entry.MemberId is { } memberId)
        {
            builder.AddField("Member", $"<@{memberId}>", inline: true);
        }

        if (!string.IsNullOrWhiteSpace(entry.ThumbnailUrl))
        {
            builder.WithThumbnailUrl(entry.ThumbnailUrl);
        }

        return builder.Build();
    }

    private static Color GetEventColor(string eventName) => eventName switch
    {
        "Message deleted" => new Color(237, 66, 69),
        "Messages bulk deleted" => new Color(237, 66, 69),
        "Message edited" => new Color(254, 231, 92),
        "Avatar changed" => new Color(88, 101, 242),
        "Server avatar changed" => new Color(88, 101, 242),
        "Member joined" => new Color(87, 242, 135),
        "Member left" => new Color(237, 66, 69),
        "Voice channel joined" => new Color(87, 242, 135),
        "Voice channel left" => new Color(237, 66, 69),
        "Voice channel moved" => new Color(88, 101, 242),
        "Voice state changed" => new Color(88, 101, 242),
        _ => new Color(148, 155, 164)
    };
}
