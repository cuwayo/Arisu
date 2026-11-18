using Arisu.Storage;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Arisu.Modules;

[Group("voice", "Moderate members connected to voice channels.")]
[RequireContext(ContextType.Guild)]
public sealed class VoiceModerationModule : ModerationModuleBase
{
    private readonly IModerationHistoryStore _history;
    private readonly ILogger<VoiceModerationModule> _logger;

    public VoiceModerationModule(IModerationHistoryStore history, ILogger<VoiceModerationModule> logger)
    {
        _history = history;
        _logger = logger;
    }

    [SlashCommand("mute", "Server-mute a member in voice.")]
    public Task MuteAsync(SocketGuildUser member, string reason = "No reason provided.") =>
        ApplyAsync(member, "Voice mute", reason, ChannelPermission.MuteMembers, properties => properties.Mute = true);

    [SlashCommand("unmute", "Remove a member's server voice mute.")]
    public Task UnmuteAsync(SocketGuildUser member, string reason = "No reason provided.") =>
        ApplyAsync(member, "Voice unmute", reason, ChannelPermission.MuteMembers, properties => properties.Mute = false);

    [SlashCommand("deafen", "Server-deafen a member in voice.")]
    public Task DeafenAsync(SocketGuildUser member, string reason = "No reason provided.") =>
        ApplyAsync(member, "Voice deafen", reason, ChannelPermission.DeafenMembers, properties => properties.Deaf = true);

    [SlashCommand("undeafen", "Remove a member's server voice deafen.")]
    public Task UndeafenAsync(SocketGuildUser member, string reason = "No reason provided.") =>
        ApplyAsync(member, "Voice undeafen", reason, ChannelPermission.DeafenMembers, properties => properties.Deaf = false);

    [SlashCommand("disconnect", "Disconnect a member from voice.")]
    public Task DisconnectAsync(SocketGuildUser member, string reason = "No reason provided.") =>
        ApplyAsync(member, "Voice disconnect", reason, ChannelPermission.MoveMembers, properties => properties.Channel = null);

    [SlashCommand("move", "Move a connected member to another voice channel.")]
    public Task MoveAsync(SocketGuildUser member, SocketVoiceChannel channel, string reason = "No reason provided.") =>
        ApplyAsync(member, "Voice move", reason, ChannelPermission.MoveMembers, properties => properties.Channel = channel, channel);

    private async Task ApplyAsync(
        SocketGuildUser member, string action, string reason, ChannelPermission permission,
        Action<GuildUserProperties> change, SocketVoiceChannel? destination = null)
    {
        await DeferAsync(ephemeral: true);
        var normalizedReason = await GetReasonAsync(reason);
        if (normalizedReason is null || !await CanModerateMemberAsync(member, requireBotHierarchy: true))
        {
            return;
        }

        var source = member.VoiceChannel;
        if (source is null)
        {
            await ReplyAsync("That member is not connected to a voice channel.");
            return;
        }

        var moderator = (SocketGuildUser)Context.User;
        if (!moderator.GetPermissions(source).Has(permission) || !Context.Guild.CurrentUser.GetPermissions(source).Has(permission))
        {
            await ReplyAsync("You and I both need the voice moderation permission in the member's channel.");
            return;
        }

        if (destination is not null &&
            (destination.Guild.Id != Context.Guild.Id ||
             !moderator.GetPermissions(destination).MoveMembers || !moderator.GetPermissions(destination).Connect ||
             !Context.Guild.CurrentUser.GetPermissions(destination).MoveMembers || !Context.Guild.CurrentUser.GetPermissions(destination).Connect))
        {
            await ReplyAsync("You and I both need Connect and Move Members in the destination channel.");
            return;
        }

        await member.ModifyAsync(change, new RequestOptions { AuditLogReason = BuildAuditReason(normalizedReason) });
        try
        {
            await _history.RecordCaseAsync(Context.Guild.Id, member.Id, Context.User.Id, action, normalizedReason,
                destination is null ? $"Voice channel: {source.Id}." : $"Moved from {source.Id} to {destination.Id}.", DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Voice action succeeded but case recording failed in guild {GuildId}.", Context.Guild.Id);
            await ReplyAsync($"{action} completed for {member.Mention}, but its moderation history could not be saved.");
            return;
        }

        await ReplyAsync($"{action} completed for {member.Mention}.");
    }
}
