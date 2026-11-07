using Arisu.Services;
using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Arisu.Modules;

[RequireContext(ContextType.Guild)]
public sealed class ModerationModule : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromDays(28);

    private readonly ILogger<ModerationModule> _logger;

    public ModerationModule(ILogger<ModerationModule> logger)
    {
        _logger = logger;
    }

    [SlashCommand("kick", "Remove a member from the server.")]
    [RequireUserPermission(GuildPermission.KickMembers)]
    [RequireBotPermission(GuildPermission.KickMembers)]
    public async Task KickAsync(
        [Summary("member", "The member to remove.")] SocketGuildUser member,
        [Summary("reason", "The reason for the removal.")] string reason)
    {
        var normalizedReason = await GetReasonAsync(reason);
        if (normalizedReason is null || !await CanModerateMemberAsync(member, requireBotHierarchy: true))
        {
            return;
        }

        await member.KickAsync(BuildAuditReason(normalizedReason));

        _logger.LogInformation(
            "Moderator {ModeratorId} kicked member {MemberId} from guild {GuildId}.",
            Context.User.Id,
            member.Id,
            Context.Guild.Id);

        await RespondAsync(
            $"Kicked {member.Mention}.",
            ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("ban", "Ban a member from the server.")]
    [RequireUserPermission(GuildPermission.BanMembers)]
    [RequireBotPermission(GuildPermission.BanMembers)]
    public async Task BanAsync(
        [Summary("member", "The member to ban.")] SocketGuildUser member,
        [Summary("reason", "The reason for the ban.")] string reason)
    {
        var normalizedReason = await GetReasonAsync(reason);
        if (normalizedReason is null || !await CanModerateMemberAsync(member, requireBotHierarchy: true))
        {
            return;
        }

        await Context.Guild.AddBanAsync(member, reason: BuildAuditReason(normalizedReason));

        _logger.LogInformation(
            "Moderator {ModeratorId} banned member {MemberId} from guild {GuildId}.",
            Context.User.Id,
            member.Id,
            Context.Guild.Id);

        await RespondAsync(
            $"Banned {member.Mention}.",
            ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("timeout", "Temporarily prevent a member from communicating.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    [RequireBotPermission(GuildPermission.ModerateMembers)]
    public async Task TimeoutAsync(
        [Summary("member", "The member to time out.")] SocketGuildUser member,
        [Summary("minutes", "Timeout length in minutes, up to 28 days.")] int minutes,
        [Summary("reason", "The reason for the timeout.")] string reason)
    {
        var normalizedReason = await GetReasonAsync(reason);
        if (normalizedReason is null || !await CanModerateMemberAsync(member, requireBotHierarchy: true))
        {
            return;
        }

        var duration = TimeSpan.FromMinutes(minutes);
        if (duration <= TimeSpan.Zero || duration > MaximumTimeout)
        {
            await RespondAsync(
                "Timeout length must be between 1 minute and 28 days.",
                ephemeral: true);
            return;
        }

        if (member.GuildPermissions.Administrator)
        {
            await RespondAsync(
                "Administrators cannot be timed out.",
                ephemeral: true);
            return;
        }

        await member.SetTimeOutAsync(
            duration,
            new RequestOptions { AuditLogReason = BuildAuditReason(normalizedReason) });

        var timeoutNotificationSent = true;
        try
        {
            await member.SendMessageAsync(
                $"You have been timed out in **{Context.Guild.Name}** for {minutes} minute(s).\n" +
                $"Reason: {normalizedReason}",
                allowedMentions: AllowedMentions.None);
        }
        catch (HttpException exception)
        {
            timeoutNotificationSent = false;
            _logger.LogWarning(
                exception,
                "Could not send timeout direct message to member {MemberId} in guild {GuildId}.",
                member.Id,
                Context.Guild.Id);
        }

        _logger.LogInformation(
            "Moderator {ModeratorId} timed out member {MemberId} for {Duration} in guild {GuildId}.",
            Context.User.Id,
            member.Id,
            duration,
            Context.Guild.Id);

        await RespondAsync(
            timeoutNotificationSent
                ? $"Timed out {member.Mention} for {minutes} minute(s) and notified them by direct message."
                : $"Timed out {member.Mention} for {minutes} minute(s), but I could not direct-message them.",
            ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("untimeout", "Remove a member's timeout.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    [RequireBotPermission(GuildPermission.ModerateMembers)]
    public async Task UntimeoutAsync(
        [Summary("member", "The member whose timeout to remove.")] SocketGuildUser member,
        [Summary("reason", "The reason for removing the timeout.")] string reason)
    {
        var normalizedReason = await GetReasonAsync(reason);
        if (normalizedReason is null || !await CanModerateMemberAsync(member, requireBotHierarchy: true))
        {
            return;
        }

        await member.RemoveTimeOutAsync(
            new RequestOptions { AuditLogReason = BuildAuditReason(normalizedReason) });

        _logger.LogInformation(
            "Moderator {ModeratorId} removed the timeout for member {MemberId} in guild {GuildId}.",
            Context.User.Id,
            member.Id,
            Context.Guild.Id);

        await RespondAsync(
            $"Removed the timeout for {member.Mention}.",
            ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    private async Task<bool> CanModerateMemberAsync(SocketGuildUser member, bool requireBotHierarchy)
    {
        if (member.Id == Context.User.Id)
        {
            await RespondAsync("You cannot moderate yourself.", ephemeral: true);
            return false;
        }

        if (member.Id == Context.Guild.OwnerId)
        {
            await RespondAsync("You cannot moderate the server owner.", ephemeral: true);
            return false;
        }

        if (Context.User.Id != Context.Guild.OwnerId)
        {
            IGuildUser? moderator = Context.Guild.GetUser(Context.User.Id);
            moderator ??= await ((IGuild)Context.Guild).GetUserAsync(
                Context.User.Id,
                CacheMode.AllowDownload);
            if (moderator is null)
            {
                await RespondAsync(
                    "I could not verify your server role. Please try again.",
                    ephemeral: true);
                return false;
            }

            if (moderator.Hierarchy <= member.Hierarchy)
            {
                await RespondAsync(
                    "You can only moderate members below your highest role.",
                    ephemeral: true);
                return false;
            }
        }

        if (requireBotHierarchy &&
            Context.Guild.CurrentUser.Id != Context.Guild.OwnerId &&
            Context.Guild.CurrentUser.Hierarchy <= member.Hierarchy)
        {
            await RespondAsync(
                "My highest role must be above the member's highest role.",
                ephemeral: true);
            return false;
        }

        return true;
    }

    private async Task<string?> GetReasonAsync(string reason)
    {
        var normalizedReason = reason.Trim();
        if (normalizedReason.Length == 0)
        {
            await RespondAsync("A moderation reason is required.", ephemeral: true);
            return null;
        }

        if (normalizedReason.Length > 400)
        {
            await RespondAsync(
                "A moderation reason can contain at most 400 characters.",
                ephemeral: true);
            return null;
        }

        return normalizedReason;
    }

    private string BuildAuditReason(string reason) =>
        $"Moderator {Context.User.Id}: {reason}";
}
