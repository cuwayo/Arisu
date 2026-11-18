using System.Globalization;
using Arisu.Storage;
using Arisu.Utilities;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Arisu.Modules;

[RequireContext(ContextType.Guild)]
public sealed class ModerationModule : ModerationModuleBase
{
    private readonly IModerationHistoryStore _history;
    private readonly ILogger<ModerationModule> _logger;

    public ModerationModule(IModerationHistoryStore history, ILogger<ModerationModule> logger)
    {
        _history = history;
        _logger = logger;
    }

    [SlashCommand("kick", "Remove a member from the server.")]
    [RequireUserPermission(GuildPermission.KickMembers)]
    [RequireBotPermission(GuildPermission.KickMembers)]
    public async Task KickAsync(
        [Summary("member", "The member to remove.")] SocketGuildUser member,
        [Summary("reason", "The reason for the removal.")] string reason = "No reason provided.")
    {
        await DeferAsync(ephemeral: true);
        var normalizedReason = await GetReasonAsync(reason);
        if (normalizedReason is null || !await CanModerateMemberAsync(member, requireBotHierarchy: true))
        {
            return;
        }

        await member.KickAsync(BuildAuditReason(normalizedReason));
        var historySaved = await RecordAsync(member.Id, "Kick", normalizedReason);
        await ReplyAsync(WithHistoryNotice($"Kicked {member.Mention}.", historySaved));
    }

    [SlashCommand("ban", "Ban a user by member or user ID.")]
    [RequireUserPermission(GuildPermission.BanMembers)]
    [RequireBotPermission(GuildPermission.BanMembers)]
    public async Task BanAsync(
        [Summary("user", "The user to ban, including users outside this server.")] IUser user,
        [Summary("reason", "The reason for the ban.")] string reason = "No reason provided.",
        [Summary("delete-days", "Days of recent messages to delete, from 0 to 7."), MinValue(0), MaxValue(7)] int deleteDays = 0)
    {
        await DeferAsync(ephemeral: true);
        var normalizedReason = await GetReasonAsync(reason);
        if (normalizedReason is null || !await CanBanAsync(user.Id))
        {
            return;
        }

        await Context.Guild.AddBanAsync(user.Id, deleteDays, BuildAuditReason(normalizedReason));
        var historySaved = await RecordAsync(user.Id, "Ban", normalizedReason, $"Deleted up to {deleteDays} day(s) of messages.");
        await ReplyAsync(WithHistoryNotice($"Banned <@{user.Id}>.", historySaved));
    }

    [SlashCommand("unban", "Unban a user by their Discord user ID.")]
    [RequireUserPermission(GuildPermission.BanMembers)]
    [RequireBotPermission(GuildPermission.BanMembers)]
    public async Task UnbanAsync(
        [Summary("user-id", "The banned user's Discord ID.")] string userId,
        [Summary("reason", "The reason for the unban.")] string reason = "No reason provided.")
    {
        await DeferAsync(ephemeral: true);
        var normalizedReason = await GetReasonAsync(reason);
        if (normalizedReason is null)
        {
            return;
        }

        if (!ulong.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
        {
            await ReplyAsync("Enter a valid Discord user ID. Enable Developer Mode to copy IDs.");
            return;
        }

        if (await Context.Guild.GetBanAsync(id) is null)
        {
            await ReplyAsync("That user is not banned from this server.");
            return;
        }

        await Context.Guild.RemoveBanAsync(id, new RequestOptions { AuditLogReason = BuildAuditReason(normalizedReason) });
        var historySaved = await RecordAsync(id, "Unban", normalizedReason);
        await ReplyAsync(WithHistoryNotice($"Unbanned <@{id}>.", historySaved));
    }

    [SlashCommand("timeout", "Temporarily prevent a member from communicating.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    [RequireBotPermission(GuildPermission.ModerateMembers)]
    public async Task TimeoutAsync(
        [Summary("member", "The member to time out.")] SocketGuildUser member,
        [Summary("duration", "Use 30s, 10m, 2h, or 7d; maximum 28 days. A number alone means minutes.")] string duration,
        [Summary("reason", "The reason for the timeout.")] string reason = "No reason provided.")
    {
        await DeferAsync(ephemeral: true);
        var normalizedReason = await GetReasonAsync(reason);
        if (normalizedReason is null || !await CanModerateMemberAsync(member, requireBotHierarchy: true))
        {
            return;
        }

        if (!ModerationDuration.TryParse(duration, out var timeout))
        {
            await ReplyAsync("Use a duration such as 30s, 10m, 2h, or 7d, up to 28 days.");
            return;
        }

        if (member.GuildPermissions.Administrator || member.IsBot)
        {
            await ReplyAsync("Administrators and bots cannot be timed out.");
            return;
        }

        await member.SetTimeOutAsync(timeout, new RequestOptions { AuditLogReason = BuildAuditReason(normalizedReason) });
        var historySaved = await RecordAsync(member.Id, "Timeout", normalizedReason, $"Duration: {timeout}.");
        var notified = true;
        try
        {
            await member.SendMessageAsync(
                $"You have been timed out in **{Context.Guild.Name}** for {timeout}.\nReason: {normalizedReason}",
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception exception)
        {
            notified = false;
            _logger.LogWarning(exception, "Could not notify timed-out member {MemberId} in guild {GuildId}.", member.Id, Context.Guild.Id);
        }

        await ReplyAsync(WithHistoryNotice(
            $"Timed out {member.Mention} for {timeout}." + (notified ? " Sent a direct message." : " I could not send a direct message."),
            historySaved));
    }

    [SlashCommand("untimeout", "Remove a member's timeout.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    [RequireBotPermission(GuildPermission.ModerateMembers)]
    public async Task UntimeoutAsync(
        [Summary("member", "The member whose timeout to remove.")] SocketGuildUser member,
        [Summary("reason", "The reason for removing the timeout.")] string reason = "No reason provided.")
    {
        await DeferAsync(ephemeral: true);
        var normalizedReason = await GetReasonAsync(reason);
        if (normalizedReason is null || !await CanModerateMemberAsync(member, requireBotHierarchy: true))
        {
            return;
        }

        await member.RemoveTimeOutAsync(new RequestOptions { AuditLogReason = BuildAuditReason(normalizedReason) });
        var historySaved = await RecordAsync(member.Id, "Timeout removed", normalizedReason);
        await ReplyAsync(WithHistoryNotice($"Removed the timeout for {member.Mention}.", historySaved));
    }

    [SlashCommand("nick", "Change or reset a member's nickname.")]
    [RequireUserPermission(GuildPermission.ManageNicknames)]
    [RequireBotPermission(GuildPermission.ManageNicknames)]
    public async Task NickAsync(
        [Summary("member", "The member whose nickname to change.")] SocketGuildUser member,
        [Summary("nickname", "The new nickname; omit to reset it."), MaxLength(32)] string? nickname = null,
        [Summary("reason", "The reason for the change.")] string reason = "No reason provided.")
    {
        await DeferAsync(ephemeral: true);
        var normalizedReason = await GetReasonAsync(reason);
        if (normalizedReason is null || !await CanModerateMemberAsync(member, requireBotHierarchy: true))
        {
            return;
        }

        var normalizedNickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
        await member.ModifyAsync(properties => properties.Nickname = normalizedNickname,
            new RequestOptions { AuditLogReason = BuildAuditReason(normalizedReason) });
        var historySaved = await RecordAsync(member.Id, "Nickname changed", normalizedReason,
            normalizedNickname is null ? "Nickname reset." : $"New nickname: {normalizedNickname}");
        await ReplyAsync(WithHistoryNotice(
            normalizedNickname is null ? $"Reset {member.Mention}'s nickname." : $"Changed {member.Mention}'s nickname.",
            historySaved));
    }

    private async Task<bool> CanBanAsync(ulong userId)
    {
        // Download membership before treating an uncached user as outside the server.
        var member = await ((IGuild)Context.Guild).GetUserAsync(userId, CacheMode.AllowDownload);
        if (member is null)
        {
            if (userId == Context.User.Id || userId == Context.Guild.OwnerId || userId == Context.Client.CurrentUser.Id)
            {
                await ReplyAsync("You cannot ban yourself, the server owner, or Arisu.");
                return false;
            }

            return true;
        }

        if (member is SocketGuildUser socketMember)
        {
            return await CanModerateMemberAsync(socketMember, requireBotHierarchy: true);
        }

        if (member.Id == Context.User.Id || member.Id == Context.Guild.OwnerId ||
            (Context.User.Id != Context.Guild.OwnerId && ((IGuildUser)Context.User).Hierarchy <= member.Hierarchy) ||
            Context.Guild.CurrentUser.Hierarchy <= member.Hierarchy)
        {
            await ReplyAsync("You and I must both have a higher role than the member being banned.");
            return false;
        }

        return true;
    }

    private static string WithHistoryNotice(string response, bool historySaved) =>
        historySaved ? response : $"{response} Its moderation history could not be saved.";

    private async Task<bool> RecordAsync(ulong targetId, string action, string reason, string? details = null)
    {
        _logger.LogInformation("Moderator {ModeratorId} performed {Action} on user {TargetId} in guild {GuildId}.",
            Context.User.Id, action, targetId, Context.Guild.Id);
        try
        {
            await _history.RecordCaseAsync(Context.Guild.Id, targetId, Context.User.Id, action, reason, details, DateTimeOffset.UtcNow);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Action {Action} succeeded but its moderation case could not be saved in guild {GuildId}.", action, Context.Guild.Id);
            return false;
        }
    }
}
