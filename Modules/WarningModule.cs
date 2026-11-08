using Arisu.Services;
using Arisu.Storage;
using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Arisu.Modules;

[RequireContext(ContextType.Guild)]
public sealed class WarningModule : ModerationModuleBase
{
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromDays(28);

    private readonly IAuditChannelService _auditChannelService;
    private readonly IGuildChannelConfigurationStore _configurationStore;
    private readonly ILogger<WarningModule> _logger;

    public WarningModule(
        IAuditChannelService auditChannelService,
        IGuildChannelConfigurationStore configurationStore,
        ILogger<WarningModule> logger)
    {
        _auditChannelService = auditChannelService;
        _configurationStore = configurationStore;
        _logger = logger;
    }

    [SlashCommand("warn", "Issue a persistent warning to a member.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    [RequireBotPermission(GuildPermission.ModerateMembers)]
    public async Task WarnAsync(
        [Summary("member", "The member to warn.")] SocketGuildUser member,
        [Summary("reason", "The reason for the warning.")] string reason)
    {
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
            catch (HttpException exception)
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

        await _auditChannelService.WriteAsync(
            new ModerationAuditEntry(
                Context.Guild.Id,
                "Warning issued",
                Context.User.Id,
                member.Mention,
                normalizedReason,
                BuildDetails(result, timeoutApplied),
                issuedAt));

        var response = $"Warned {member.Mention}. They now have {result.WarningCount} warning(s).";
        if (escalationTimeoutMinutes is { } timeoutMinutes)
        {
            response += timeoutApplied
                ? $" The {timeoutMinutes}-minute escalation timeout was applied."
                : " The configured escalation timeout could not be applied.";
        }

        await RespondAsync(
            response,
            ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("warnings", "List a member's warning history.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    public async Task WarningsAsync(
        [Summary("member", "The member whose warnings to view.")] SocketGuildUser member)
    {
        var warnings = await _configurationStore.GetWarningsAsync(Context.Guild.Id, member.Id);
        if (warnings.Count == 0)
        {
            await RespondAsync(
                $"{member.Mention} has no warnings.",
                ephemeral: true,
                allowedMentions: AllowedMentions.None);
            return;
        }

        var lines = warnings.Take(8).Select(
            warning =>
                $"**#{warning.Id}** · <t:{warning.CreatedAt.ToUnixTimeSeconds()}:d> · " +
                $"<@{warning.ModeratorId}>\n{warning.Reason}");

        var description = string.Join("\n\n", lines);
        if (warnings.Count > 8)
        {
            description += $"\n\nShowing the 8 most recent of {warnings.Count} warnings.";
        }

        var embed = new EmbedBuilder()
            .WithTitle($"Warnings for {member.Username}")
            .WithColor(new Color(254, 231, 92))
            .WithDescription(description)
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();

        await RespondAsync(
            embed: embed,
            ephemeral: true,
            allowedMentions: AllowedMentions.None);
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
