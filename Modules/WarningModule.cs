using Arisu.Services;
using Arisu.Storage;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Arisu.Modules;

[RequireContext(ContextType.Guild)]
public sealed class WarningModule : ModerationModuleBase
{
    private readonly IAuditChannelService _auditChannelService;
    private readonly IGuildChannelConfigurationStore _configurationStore;
    private readonly IModerationHistoryStore _historyStore;
    private readonly ILogger<WarningModule> _logger;

    public WarningModule(
        IAuditChannelService auditChannelService,
        IGuildChannelConfigurationStore configurationStore,
        IModerationHistoryStore historyStore,
        ILogger<WarningModule> logger)
    {
        _auditChannelService = auditChannelService;
        _configurationStore = configurationStore;
        _historyStore = historyStore;
        _logger = logger;
    }

    [SlashCommand("warn", "Issue a persistent warning to a member.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    [RequireBotPermission(GuildPermission.ModerateMembers)]
    public async Task WarnAsync(
        [Summary("member", "The member to warn.")] SocketGuildUser member,
        [Summary("reason", "The reason for the warning.")] string reason)
    {
        await DeferAsync(ephemeral: true);

        var normalizedReason = await GetReasonAsync(reason);
        if (normalizedReason is null || !await CanModerateMemberAsync(member, requireBotHierarchy: true))
        {
            return;
        }

        var issuedAt = DateTimeOffset.UtcNow;
        var result = await _configurationStore.AddWarningAsync(
            Context.Guild.Id,
            member.Id,
            Context.User.Id,
            normalizedReason,
            issuedAt);

        int? escalationTimeoutMinutes = null;
        var timeoutApplied = false;
        if (result.Escalation is { } escalation && result.ShouldApplyEscalation)
        {
            escalationTimeoutMinutes = escalation.TimeoutMinutes;
            try
            {
                await member.SetTimeOutAsync(
                    TimeSpan.FromMinutes(escalation.TimeoutMinutes),
                    new RequestOptions
                    {
                        AuditLogReason = BuildAuditReason(
                            $"Reached {escalation.Threshold} warnings: {normalizedReason}")
                    });
                timeoutApplied = true;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not apply warning escalation timeout to member {MemberId} in guild {GuildId}.",
                    member.Id,
                    Context.Guild.Id);

                try
                {
                    await _configurationStore.RemoveWarningEscalationApplicationAsync(
                        Context.Guild.Id,
                        member.Id);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogError(
                        rollbackException,
                        "Could not make warning escalation eligible for retry for member {MemberId} in guild {GuildId}.",
                        member.Id,
                        Context.Guild.Id);
                }
            }
        }

        _logger.LogInformation(
            "Moderator {ModeratorId} issued warning {WarningId} to member {MemberId} in guild {GuildId}.",
            Context.User.Id,
            result.Warning.Id,
            member.Id,
            Context.Guild.Id);

        await RecordAndAuditAsync(
            member.Id, "Warning issued", normalizedReason,
            BuildDetails(result, timeoutApplied), issuedAt);

        var response = $"Warned {member.Mention} (warning #{result.Warning.Id}). They now have {result.WarningCount} warning(s).";
        if (escalationTimeoutMinutes is { } timeoutMinutes)
        {
            response += timeoutApplied
                ? $" The {timeoutMinutes}-minute escalation timeout was applied."
                : " The configured escalation timeout could not be applied.";
        }

        await ReplyAsync(
            response);
    }

    [SlashCommand("warnings", "List a member's warning history.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    public async Task WarningsAsync(
        [Summary("member", "The member whose warnings to view.")] SocketGuildUser member)
    {
        await DeferAsync(ephemeral: true);

        var warnings = await _configurationStore.GetWarningsAsync(Context.Guild.Id, member.Id);
        if (warnings.TotalCount == 0)
        {
            await ReplyAsync($"{member.Mention} has no warnings.");
            return;
        }

        var lines = warnings.RecentWarnings.Select(
            warning =>
                $"**#{warning.Id}** · <t:{warning.CreatedAt.ToUnixTimeSeconds()}:d> · " +
                $"<@{warning.ModeratorId}>\n{warning.Reason}");

        var description = string.Join("\n\n", lines);
        if (warnings.TotalCount > warnings.RecentWarnings.Count)
        {
            description += $"\n\nShowing the {warnings.RecentWarnings.Count} most recent of {warnings.TotalCount} warnings.";
        }

        if (description.Length > EmbedBuilder.MaxDescriptionLength)
        {
            var length = EmbedBuilder.MaxDescriptionLength - 1;
            if (char.IsHighSurrogate(description[length - 1]))
            {
                length--;
            }

            description = $"{description[..length]}…";
        }

        var embed = new EmbedBuilder()
            .WithTitle($"Warnings for {member.Username}")
            .WithColor(new Color(254, 231, 92))
            .WithDescription(description)
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();

        await ReplyAsync(
            embed: embed);
    }

    [SlashCommand("delwarn", "Delete a warning by its warning ID.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    public async Task DeleteWarningAsync(
        [Summary("warning-id", "The warning ID shown by /warnings.")] long warningId)
    {
        await DeferAsync(ephemeral: true);
        if (!await ValidateWarningIdAsync(warningId))
        {
            return;
        }

        if (!await CanManageWarningAsync(warningId))
        {
            return;
        }

        var warning = await _historyStore.RemoveWarningAsync(Context.Guild.Id, warningId);
        if (warning is null)
        {
            await ReplyAsync("That warning was not found in this server.");
            return;
        }

        await RecordAndAuditAsync(
            warning.MemberId, "Warning deleted", warning.Reason,
            $"Deleted warning #{warning.Id}, originally issued by <@{warning.ModeratorId}>.",
            DateTimeOffset.UtcNow);
        await ReplyAsync($"Deleted warning #{warning.Id} for <@{warning.MemberId}>. " + EscalationPolicyNotice);
    }

    [SlashCommand("clearwarnings", "Clear a member's warnings after explicit confirmation.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    public async Task ClearWarningsAsync(
        [Summary("member", "The member whose warnings to clear.")] SocketGuildUser member,
        [Summary("confirmed", "Set to true to confirm deleting all this member's warnings.")] bool confirmed)
    {
        await DeferAsync(ephemeral: true);
        if (!confirmed)
        {
            await ReplyAsync("To clear warnings, run /clearwarnings with confirmed set to true.");
            return;
        }

        if (member.Guild.Id != Context.Guild.Id)
        {
            await ReplyAsync("Select a member from this server.");
            return;
        }

        if (!await CanModerateUserAsync(member.Id, requireBotHierarchy: false))
        {
            return;
        }

        var count = await _historyStore.ClearWarningsAsync(Context.Guild.Id, member.Id);
        if (count == 0)
        {
            await ReplyAsync($"{member.Mention} has no warnings.");
            return;
        }

        await RecordAndAuditAsync(
            member.Id, "Warnings cleared", "Moderator confirmed clearing all warnings.",
            $"Deleted {count} warning(s). {EscalationPolicyNotice}", DateTimeOffset.UtcNow);
        await ReplyAsync($"Cleared {count} warning(s) for {member.Mention}. " + EscalationPolicyNotice);
    }

    [SlashCommand("reason", "Change the reason on a warning.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    public async Task ReasonAsync(
        [Summary("warning-id", "The warning ID shown by /warnings.")] long warningId,
        [Summary("reason", "The new warning reason (up to 400 characters).")] string reason)
    {
        await DeferAsync(ephemeral: true);
        if (!await ValidateWarningIdAsync(warningId))
        {
            return;
        }

        var normalizedReason = await GetReasonAsync(reason);
        if (normalizedReason is null)
        {
            return;
        }

        if (!await CanManageWarningAsync(warningId))
        {
            return;
        }

        var change = await _historyStore.UpdateWarningReasonAsync(Context.Guild.Id, warningId, normalizedReason);
        if (change is null)
        {
            await ReplyAsync("That warning was not found in this server.");
            return;
        }

        await RecordAndAuditAsync(
            change.Warning.MemberId, "Warning reason updated", normalizedReason,
            $"Warning #{warningId}. Previous reason: {change.PreviousReason}", DateTimeOffset.UtcNow);
        await ReplyAsync($"Updated the reason for warning #{warningId} for <@{change.Warning.MemberId}>.");
    }

    private const string EscalationPolicyNotice =
        "Removing warnings does not reset escalation; it applies once per member for the current policy.";

    private async Task<bool> CanManageWarningAsync(long warningId)
    {
        var warning = await _historyStore.GetWarningAsync(Context.Guild.Id, warningId);
        if (warning is null)
        {
            await ReplyAsync("That warning was not found in this server.");
            return false;
        }

        // History edits perform no Discord action, so the bot's role height does
        // not apply. Self, owner, bot and moderator hierarchy protections still do.
        return await CanModerateUserAsync(warning.MemberId, requireBotHierarchy: false);
    }

    private async Task<bool> ValidateWarningIdAsync(long warningId)
    {
        if (warningId > 0)
        {
            return true;
        }

        await ReplyAsync("A warning ID must be a positive number.");
        return false;
    }

    private async Task RecordAndAuditAsync(
        ulong targetId,
        string action,
        string reason,
        string details,
        DateTimeOffset occurredAt)
    {
        try
        {
            await _historyStore.RecordCaseAsync(
                Context.Guild.Id, targetId, Context.User.Id, action, reason, details, occurredAt);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not record moderation action {Action} for member {MemberId} in guild {GuildId}.",
                action, targetId, Context.Guild.Id);
        }

        try
        {
            await _auditChannelService.WriteAsync(new ModerationAuditEntry(
                Context.Guild.Id, action, Context.User.Id, $"<@{targetId}>", reason, details, occurredAt));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Warning action succeeded but audit delivery failed in guild {GuildId}.", Context.Guild.Id);
        }
    }

    private static string BuildDetails(WarningIssueResult result, bool timeoutApplied)
    {
        var details = $"Warning #{result.Warning.Id}. Total warnings: {result.WarningCount}.";
        if (result.Escalation is { } escalation && result.ShouldApplyEscalation)
        {
            details += timeoutApplied
                ? $" Reached the warning threshold; applied a {escalation.TimeoutMinutes}-minute timeout."
                : " Reached the warning threshold, but the timeout could not be applied.";
        }

        return details;
    }
}
