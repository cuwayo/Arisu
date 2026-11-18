using Discord;
using Discord.WebSocket;

namespace Arisu.Services;

public enum AutoModFilter
{
    Spam,
    Mentions,
    Invites,
    Words
}

public interface IDiscordAutoModService
{
    Task<string> GetStatusAsync(IGuild guild, CancellationToken cancellationToken = default);

    Task<string> SetFilterAsync(
        IGuild guild, ulong actorId, AutoModFilter filter, bool enabled,
        int mentionLimit = 5, ITextChannel? alertChannel = null,
        CancellationToken cancellationToken = default);

    Task<string> AddWordAsync(
        IGuild guild, ulong actorId, string keyword, ITextChannel? alertChannel = null,
        CancellationToken cancellationToken = default);

    Task<string> RemoveWordAsync(
        IGuild guild, ulong actorId, string keyword, CancellationToken cancellationToken = default);

    Task<string> ListWordsAsync(IGuild guild, int page, CancellationToken cancellationToken = default);

    Task<string> SetExemptionAsync(
        IGuild guild, ulong actorId, AutoModFilter filter, ulong targetId, bool isRole, bool exempt,
        CancellationToken cancellationToken = default);
}

public sealed class AutoModConfigurationException(string message) : Exception(message);

/// <summary>
/// Configures Discord's native rules; Discord enforces them even while Arisu is offline.
/// Spam detection is controlled by Discord. Invite matching covers ordinary links, not obfuscation.
/// Discord applies its own administrator, Manage Server, bot, and webhook exemptions.
/// </summary>
public sealed class DiscordAutoModService : IDiscordAutoModService
{
    public const string InvitePattern =
        @"(?i)(?:^|[^a-z0-9_.-])(?:https?://)?(?:www\.)?(?:discord\.gg/|discord(?:app)?\.com/invite/)[a-z0-9-]+";

    private const int WordsPerPage = 10;
    private readonly DiscordSocketClient _client;
    private readonly IAuditChannelService _auditChannelService;
    // Fixed stripes bound memory while serializing read/modify/write within this bot process.
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public DiscordAutoModService(DiscordSocketClient client, IAuditChannelService auditChannelService)
    {
        _client = client;
        _auditChannelService = auditChannelService;
    }

    public async Task<string> GetStatusAsync(IGuild guild, CancellationToken cancellationToken = default)
    {
        var rules = await guild.GetAutoModRulesAsync(Options(cancellationToken));
        var lines = new List<string> { "**Arisu native AutoMod**" };
        foreach (var filter in Enum.GetValues<AutoModFilter>())
        {
            var owned = rules.Where(rule => IsOwned(rule, filter)).ToArray();
            if (owned.Length == 0)
            {
                lines.Add($"{filter}: not configured.");
                continue;
            }

            if (owned.Length > 1)
            {
                lines.Add($"{filter}: duplicate owned rules; resolve in Server Settings before editing.");
                continue;
            }

            var rule = owned[0];
            var configuration = filter switch
            {
                AutoModFilter.Mentions => $"; limit {rule.MentionTotalLimit}, raid protection {rule.MentionRaidProtectionEnabled == true}",
                AutoModFilter.Words => $"; {rule.KeywordFilter.Count} keyword(s)",
                _ => ""
            };
            var alerts = rule.Actions.FirstOrDefault(action => action.Type == AutoModActionType.SendAlertMessage);
            lines.Add($"{filter}: {(rule.Enabled ? "enabled" : "disabled")}{configuration}; " +
                $"exempt roles {rule.ExemptRoles.Count}/20, channels {rule.ExemptChannels.Count}/50; " +
                $"alerts {(alerts?.ChannelId is { } id ? $"<#{id}>" : "off")}; rule {rule.Id}.");
        }

        lines.Add("Discord enforces enabled rules even while Arisu is offline. Spam uses Discord's detector; " +
            "its sensitivity and message rate are not configurable here. Invites covers ordinary discord.gg, " +
            "discord.com/invite and discordapp.com/invite links, not obfuscation. Keywords use Discord's " +
            "case-insensitive matching with optional * at either end, not regex.");
        lines.Add("Discord handles exemptions, including administrators and Manage Server members, bots and webhooks. " +
            "Use Server Settings → AutoMod to inspect exemption IDs and Discord's filter limitations. " +
            "Arisu changes only its own named rules. Actions block messages and optionally send native alerts; no timeouts.");
        return string.Join('\n', lines);
    }

    public Task<string> SetFilterAsync(
        IGuild guild, ulong actorId, AutoModFilter filter, bool enabled,
        int mentionLimit = 5, ITextChannel? alertChannel = null,
        CancellationToken cancellationToken = default) =>
        MutateAsync(guild, actorId, async options =>
        {
            if (filter == AutoModFilter.Words)
            {
                throw new AutoModConfigurationException("Use /automod words add or remove to configure keywords.");
            }

            if (mentionLimit is < 1 or > AutoModRuleProperties.MaxMentionLimit)
            {
                throw new AutoModConfigurationException("The mention limit must be between 1 and 50.");
            }

            await ValidateAlertChannelAsync(guild, actorId, alertChannel, options);
            var rules = await guild.GetAutoModRulesAsync(options);
            var rule = FindOwned(rules, filter);
            if (rule is null && !enabled)
            {
                return new Change($"{filter} is already disabled; no owned rule exists.");
            }

            if (rule is null)
            {
                CheckRuleQuota(rules, filter);
                await guild.CreateAutoModRuleAsync(properties =>
                {
                    Initialize(properties, filter, alertChannel);
                    Configure(properties);
                }, options);
            }
            else
            {
                await rule.ModifyAsync(properties =>
                {
                    Configure(properties);
                    if (enabled)
                    {
                        properties.Actions = Actions(rule, alertChannel);
                    }
                }, options);
            }

            return new Change($"{filter} {(enabled ? "enabled" : "disabled")}." +
                (filter == AutoModFilter.Mentions && enabled
                    ? $" Messages exceeding {mentionLimit} unique mentions are blocked; mention raid protection is enabled."
                    : "") +
                (filter == AutoModFilter.Spam && enabled ? " Discord controls spam detection and sensitivity." : ""),
                $"{filter} {(enabled ? "enabled" : "disabled")}; mention limit {mentionLimit}; " +
                $"alert channel {(alertChannel?.Id.ToString() ?? "preserved or off")}.");

            void Configure(AutoModRuleProperties properties)
            {
                properties.Enabled = enabled;
                if (filter == AutoModFilter.Mentions && enabled)
                {
                    properties.MentionLimit = mentionLimit;
                    properties.MentionRaidProtectionEnabled = true;
                }
                else if (filter == AutoModFilter.Invites && enabled)
                {
                    properties.RegexPatterns = new[] { InvitePattern };
                }
            }
        }, cancellationToken);

    public Task<string> AddWordAsync(
        IGuild guild, ulong actorId, string keyword, ITextChannel? alertChannel = null,
        CancellationToken cancellationToken = default) =>
        MutateAsync(guild, actorId, async options =>
        {
            var normalized = NormalizeKeyword(keyword);
            await ValidateAlertChannelAsync(guild, actorId, alertChannel, options);
            var rules = await guild.GetAutoModRulesAsync(options);
            var rule = FindOwned(rules, AutoModFilter.Words);
            var keywords = rule?.KeywordFilter.ToList() ?? [];
            if (keywords.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return new Change("That keyword is already configured. No settings changed.");
            }

            if (keywords.Count >= AutoModRuleProperties.MaxKeywordCount)
            {
                throw new AutoModConfigurationException("The Words rule already has Discord's maximum of 1,000 keywords.");
            }

            keywords.Add(normalized);
            if (rule is null)
            {
                CheckRuleQuota(rules, AutoModFilter.Words);
                await guild.CreateAutoModRuleAsync(properties =>
                {
                    Initialize(properties, AutoModFilter.Words, alertChannel);
                    properties.KeywordFilter = keywords.ToArray();
                }, options);
            }
            else
            {
                await rule.ModifyAsync(properties =>
                {
                    properties.KeywordFilter = keywords.ToArray();
                    properties.Enabled = true;
                    properties.Actions = Actions(rule, alertChannel);
                }, options);
            }

            return new Change($"Keyword added. Words is enabled with {keywords.Count} keyword(s). " +
                "Discord matches case-insensitively; * at either end enables wildcard matching.",
                $"Words enabled; one keyword added; total {keywords.Count}. " +
                $"Alert channel {(alertChannel?.Id.ToString() ?? "preserved or off")}.");
        }, cancellationToken);

    public Task<string> RemoveWordAsync(
        IGuild guild, ulong actorId, string keyword, CancellationToken cancellationToken = default) =>
        MutateAsync(guild, actorId, async options =>
        {
            var normalized = NormalizeKeyword(keyword);
            var rule = FindOwned(await guild.GetAutoModRulesAsync(options), AutoModFilter.Words);
            var keywords = rule?.KeywordFilter.ToList() ?? [];
            if (rule is null || keywords.RemoveAll(value =>
                    string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return new Change("That keyword is not configured. No settings changed.");
            }

            if (keywords.Count == 0)
            {
                await rule.DeleteAsync(options);
                return new Change("Last keyword removed. The owned Words rule was deleted, including its alerts and exemptions.",
                    "Last keyword removed; owned Words rule deleted, including alerts and exemptions.");
            }

            await rule.ModifyAsync(properties => properties.KeywordFilter = keywords.ToArray(), options);
            return new Change($"Keyword removed. {keywords.Count} keyword(s) remain.",
                $"One keyword removed; total {keywords.Count}.");
        }, cancellationToken);

    public async Task<string> ListWordsAsync(IGuild guild, int page, CancellationToken cancellationToken = default)
    {
        var rule = FindOwned(await guild.GetAutoModRulesAsync(Options(cancellationToken)), AutoModFilter.Words);
        var keywords = rule?.KeywordFilter.Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var pages = Math.Max(1, (keywords.Length + WordsPerPage - 1) / WordsPerPage);
        if (page < 1 || page > pages)
        {
            throw new AutoModConfigurationException($"Choose a page between 1 and {pages}.");
        }

        if (keywords.Length == 0)
        {
            return "No owned Words rule is configured. Add a keyword with /automod words add.";
        }

        // Fixed page size and native 60-character keyword limit keep responses below Discord's limit.
        var lines = keywords.Skip((page - 1) * WordsPerPage).Take(WordsPerPage)
            .Select((word, index) => $"{(page - 1) * WordsPerPage + index + 1}. {Format.Sanitize(word)}");
        return $"**Words: page {page}/{pages}, {keywords.Length} keyword(s)**\n" +
            string.Join('\n', lines) + "\nCase-insensitive native keywords; * is supported only at either end.";
    }

    public Task<string> SetExemptionAsync(
        IGuild guild, ulong actorId, AutoModFilter filter, ulong targetId, bool isRole, bool exempt,
        CancellationToken cancellationToken = default) =>
        MutateAsync(guild, actorId, async options =>
        {
            if (isRole)
            {
                if (targetId == guild.EveryoneRole.Id)
                {
                    throw new AutoModConfigurationException("The @everyone role cannot be exempted through Arisu.");
                }

                if (guild.GetRole(targetId) is null)
                {
                    throw new AutoModConfigurationException("Choose a role in this server.");
                }
            }
            else if (await guild.GetChannelAsync(targetId, options: options) is not { } channel ||
                     channel.GuildId != guild.Id)
            {
                throw new AutoModConfigurationException("Choose a channel in this server.");
            }

            var rule = FindOwned(await guild.GetAutoModRulesAsync(options), filter)
                ?? throw new AutoModConfigurationException("Configure the selected owned rule before changing its exemptions.");
            var ids = (isRole ? rule.ExemptRoles : rule.ExemptChannels).ToHashSet();
            var changed = exempt ? ids.Add(targetId) : ids.Remove(targetId);
            if (!changed)
            {
                return new Change("That exemption is already configured as requested. No settings changed.");
            }

            var maximum = isRole ? AutoModRuleProperties.MaxExemptRoles : AutoModRuleProperties.MaxExemptChannels;
            if (ids.Count > maximum)
            {
                throw new AutoModConfigurationException($"Discord allows at most {maximum} exempt {(isRole ? "roles" : "channels")} per rule.");
            }

            await rule.ModifyAsync(properties =>
            {
                if (isRole)
                {
                    properties.ExemptRoles = ids.Order().ToArray();
                }
                else
                {
                    properties.ExemptChannels = ids.Order().ToArray();
                }
            }, options);
            return new Change($"{filter}: {(isRole ? "role" : "channel")} {targetId} exemption {(exempt ? "added" : "removed")}. " +
                "This changes only this rule's exemptions; it grants no server permissions.",
                $"{filter}: {(isRole ? "role" : "channel")} {targetId} exemption {(exempt ? "added" : "removed")}.");
        }, cancellationToken);

    public static string NormalizeKeyword(string keyword)
    {
        var value = keyword.Trim().ToLowerInvariant();
        var inner = value.Trim('*');
        if (value.Length is < 1 or > AutoModRuleProperties.MaxKeywordLength ||
            value.Any(char.IsControl) || string.IsNullOrWhiteSpace(inner) || inner.Contains('*') ||
            value.StartsWith("**", StringComparison.Ordinal) || value.EndsWith("**", StringComparison.Ordinal))
        {
            throw new AutoModConfigurationException(
                "Enter one keyword or phrase of 1–60 characters. Use at most one * at each end, " +
                "with text between; control characters and regex are not supported.");
        }

        return value;
    }

    private async Task<string> MutateAsync(
        IGuild guild, ulong actorId, Func<RequestOptions, Task<Change>> mutation, CancellationToken cancellationToken)
    {
        var gate = _gates[guild.Id % (ulong)_gates.Length];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var change = await mutation(Options(cancellationToken, actorId));
            if (change.AuditDetails is not null)
            {
                await _auditChannelService.WriteAsync(new ModerationAuditEntry(
                    guild.Id, "AutoMod configuration changed", actorId, null,
                    "Native Discord AutoMod settings changed.", change.AuditDetails, DateTimeOffset.UtcNow));
            }

            return change.Message;
        }
        finally
        {
            gate.Release();
        }
    }

    private IAutoModRule? FindOwned(IEnumerable<IAutoModRule> rules, AutoModFilter filter)
    {
        var matching = rules.Where(rule => IsOwned(rule, filter)).Take(2).ToArray();
        if (matching.Length > 1)
        {
            throw new AutoModConfigurationException(
                "Multiple owned rules have this name and trigger. Resolve duplicates in Server Settings → AutoMod first.");
        }

        return matching.FirstOrDefault();
    }

    private bool IsOwned(IAutoModRule rule, AutoModFilter filter) =>
        rule.CreatorId == _client.CurrentUser.Id && rule.Name == Name(filter) &&
        rule.TriggerType == Trigger(filter) && rule.EventType == AutoModEventType.MessageSend;

    private static string Name(AutoModFilter filter) => $"Arisu: {filter}";

    private static AutoModTriggerType Trigger(AutoModFilter filter) => filter switch
    {
        AutoModFilter.Spam => AutoModTriggerType.Spam,
        AutoModFilter.Mentions => AutoModTriggerType.MentionSpam,
        AutoModFilter.Invites or AutoModFilter.Words => AutoModTriggerType.Keyword,
        _ => throw new AutoModConfigurationException("Choose a supported AutoMod filter.")
    };

    private static void CheckRuleQuota(IEnumerable<IAutoModRule> rules, AutoModFilter filter)
    {
        var trigger = Trigger(filter);
        var maximum = trigger == AutoModTriggerType.Keyword ? 6 : 1;
        if (rules.Count(rule => rule.TriggerType == trigger) >= maximum)
        {
            throw new AutoModConfigurationException(
                $"Discord's {trigger} rule quota is full ({maximum} rule(s), including disabled rules). " +
                "Review Server Settings → AutoMod. Arisu will not change another rule to make room.");
        }
    }

    private static void Initialize(AutoModRuleProperties properties, AutoModFilter filter, ITextChannel? alertChannel)
    {
        properties.Name = Name(filter);
        properties.EventType = AutoModEventType.MessageSend;
        properties.TriggerType = Trigger(filter);
        properties.Enabled = true;
        properties.Actions = Actions(null, alertChannel);
    }

    private static AutoModRuleActionProperties[] Actions(IAutoModRule? rule, ITextChannel? alertChannel)
    {
        var actions = new List<AutoModRuleActionProperties>
        {
            new() { Type = AutoModActionType.BlockMessage }
        };
        var alertId = alertChannel?.Id ?? rule?.Actions
            .FirstOrDefault(action => action.Type == AutoModActionType.SendAlertMessage)?.ChannelId;
        if (alertId is { } id)
        {
            actions.Add(new AutoModRuleActionProperties { Type = AutoModActionType.SendAlertMessage, ChannelId = id });
        }

        return actions.ToArray();
    }

    private static async Task ValidateAlertChannelAsync(
        IGuild guild, ulong actorId, ITextChannel? channel, RequestOptions options)
    {
        if (channel is null)
        {
            return;
        }

        if (channel.GuildId != guild.Id)
        {
            throw new AutoModConfigurationException("Choose an alert text channel in this server.");
        }

        var actor = await guild.GetUserAsync(actorId, options: options);
        var bot = await guild.GetCurrentUserAsync(options: options);
        var permissions = bot.GetPermissions(channel);
        if (actor is null || !actor.GetPermissions(channel).ViewChannel ||
            !permissions.ViewChannel || !permissions.SendMessages || !permissions.EmbedLinks)
        {
            throw new AutoModConfigurationException(
                "You must be able to view the alert channel. I need View Channel, Send Messages, and Embed Links there.");
        }
    }

    private static RequestOptions Options(CancellationToken cancellationToken, ulong? actorId = null) => new()
    {
        CancelToken = cancellationToken,
        AuditLogReason = actorId is { } id ? $"Arisu AutoMod configuration requested by {id}." : null
    };

    private sealed record Change(string Message, string? AuditDetails = null);
}
