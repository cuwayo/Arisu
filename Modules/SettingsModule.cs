using System.Text;
using Arisu.Services;
using Arisu.Storage;
using Arisu.Utilities;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arisu.Modules;

[RequireContext(ContextType.Guild)]
[Group("settings", "Configure server channels.")]
public sealed class SettingsModule : DeferredInteractionModuleBase
{
    [Group("logs", "Configure the server audit channel.")]
    public sealed class ModerationSettingsModule : DeferredInteractionModuleBase
    {
        private readonly IAuditChannelService _auditChannelService;
        private readonly ILogger<ModerationSettingsModule> _logger;

        public ModerationSettingsModule(
            IAuditChannelService auditChannelService,
            ILogger<ModerationSettingsModule> logger)
        {
            _auditChannelService = auditChannelService;
            _logger = logger;
        }

        [SlashCommand("set", "Set the channel for server audit entries.")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task SetAsync(
            [Summary("channel", "The text channel for server audit entries.")]
            SocketTextChannel channel)
        {
            await DeferAsync(ephemeral: true);

            if (!await _auditChannelService.SetAuditChannelAsync(Context.Guild.Id, channel.Id))
            {
                await ReplyAsync(
                    "I could not save the server audit channel. Please try again.");
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} set server audit channel {ChannelId} in guild {GuildId}.",
                Context.User.Id,
                channel.Id,
                Context.Guild.Id);

            await ReplyAsync(
                $"Server audit entries will be sent to {channel.Mention}.");

            await _auditChannelService.WriteAsync(
                new ModerationAuditEntry(
                    Context.Guild.Id,
                    "Server audit channel configured",
                    Context.User.Id,
                    null,
                    "Bot settings changed.",
                    $"Server audit channel set to <#{channel.Id}>.",
                    DateTimeOffset.UtcNow));
        }

        [SlashCommand("remove", "Stop sending server audit entries.")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task RemoveAsync()
        {
            await DeferAsync(ephemeral: true);

            await WriteSettingsAuditEntryAsync(
                "Server audit channel removal requested",
                "The server audit channel is being removed.");

            if (!await _auditChannelService.RemoveAuditChannelAsync(Context.Guild.Id))
            {
                await ReplyAsync(
                    "I could not remove the server audit channel. Please try again.");
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} removed server audit channel in guild {GuildId}.",
                Context.User.Id,
                Context.Guild.Id);

            await ReplyAsync(
                "Server audit entries will no longer be sent to a channel.");
        }

        private Task WriteSettingsAuditEntryAsync(string action, string details) =>
            _auditChannelService.WriteAsync(
                new ModerationAuditEntry(
                    Context.Guild.Id,
                    action,
                    Context.User.Id,
                    null,
                    "Bot settings changed.",
                    details,
                    DateTimeOffset.UtcNow));
    }

    [Group("member-logs", "Configure the member log channel.")]
    public sealed class MemberLogsSettingsModule : DeferredInteractionModuleBase
    {
        private readonly IAuditChannelService _auditChannelService;
        private readonly IMemberLogChannelService _memberLogChannelService;
        private readonly ILogger<MemberLogsSettingsModule> _logger;

        public MemberLogsSettingsModule(
            IAuditChannelService auditChannelService,
            IMemberLogChannelService memberLogChannelService,
            ILogger<MemberLogsSettingsModule> logger)
        {
            _auditChannelService = auditChannelService;
            _memberLogChannelService = memberLogChannelService;
            _logger = logger;
        }

        [SlashCommand("set", "Set the channel for member activity logs.")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task SetAsync(
            [Summary("channel", "The text channel for member activity logs.")]
            SocketTextChannel channel)
        {
            await DeferAsync(ephemeral: true);

            if (!await _memberLogChannelService.SetMemberLogChannelAsync(
                    Context.Guild.Id,
                    channel.Id))
            {
                await ReplyAsync(
                    "I could not save the member log channel. Please try again.");
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} set member log channel {ChannelId} in guild {GuildId}.",
                Context.User.Id,
                channel.Id,
                Context.Guild.Id);

            await ReplyAsync(
                $"Member activity logs will be sent to {channel.Mention}.");

            await WriteSettingsAuditEntryAsync(
                "Member log channel configured",
                $"Member log channel set to <#{channel.Id}>.");
        }

        [SlashCommand("remove", "Stop sending member activity logs.")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task RemoveAsync()
        {
            await DeferAsync(ephemeral: true);

            if (!await _memberLogChannelService.RemoveMemberLogChannelAsync(Context.Guild.Id))
            {
                await ReplyAsync(
                    "I could not remove the member log channel. Please try again.");
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} removed member log channel in guild {GuildId}.",
                Context.User.Id,
                Context.Guild.Id);

            await ReplyAsync(
                "Member activity logs will no longer be sent to a channel.");

            await WriteSettingsAuditEntryAsync(
                "Member log channel removed",
                "The member log channel was removed.");
        }

        private Task WriteSettingsAuditEntryAsync(string action, string details) =>
            _auditChannelService.WriteAsync(
                new ModerationAuditEntry(
                    Context.Guild.Id,
                    action,
                    Context.User.Id,
                    null,
                    "Bot settings changed.",
                    details,
                    DateTimeOffset.UtcNow));
    }

    [Group("welcome", "Configure the welcome, goodbye, and boost channel.")]
    public sealed class MemberEventsSettingsModule : DeferredInteractionModuleBase
    {
        private readonly IAuditChannelService _auditChannelService;
        private readonly IGuildEventBackgroundService _eventBackgroundService;
        private readonly IGuildEventChannelService _eventChannelService;
        private readonly ILogger<MemberEventsSettingsModule> _logger;

        public MemberEventsSettingsModule(
            IAuditChannelService auditChannelService,
            IGuildEventBackgroundService eventBackgroundService,
            IGuildEventChannelService eventChannelService,
            ILogger<MemberEventsSettingsModule> logger)
        {
            _auditChannelService = auditChannelService;
            _eventBackgroundService = eventBackgroundService;
            _eventChannelService = eventChannelService;
            _logger = logger;
        }

        [SlashCommand("set", "Set the channel for welcome, goodbye, and boost notices.")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task SetAsync(
            [Summary("channel", "The text channel for member event notifications.")]
            SocketTextChannel channel)
        {
            await DeferAsync(ephemeral: true);

            if (!await _eventChannelService.SetEventChannelAsync(Context.Guild.Id, channel.Id))
            {
                await ReplyAsync(
                    "I could not save the member event channel. Please try again.");
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} set member event channel {ChannelId} in guild {GuildId}.",
                Context.User.Id,
                channel.Id,
                Context.Guild.Id);

            await ReplyAsync(
                $"Welcome, goodbye, and boost notices will be sent to {channel.Mention}.");

            await WriteSettingsAuditEntryAsync(
                "Member event channel configured",
                $"Member event channel set to <#{channel.Id}>.");
        }

        [SlashCommand("background", "Set the background image for member event cards.")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task SetBackgroundAsync(
            [Summary("image", "A PNG, JPEG, or WebP card background image up to 8 MB.")]
            IAttachment image)
        {
            await DeferAsync(ephemeral: true);

            var result = await _eventBackgroundService.SetAsync(Context.Guild.Id, image);
            if (!result.Succeeded)
            {
                await ModifyOriginalResponseAsync(properties => properties.Content = result.Message);
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} set the member event card background in guild {GuildId}.",
                Context.User.Id,
                Context.Guild.Id);

            await ModifyOriginalResponseAsync(
                properties => properties.Content = "Member event cards will use the new background image.");

            await WriteSettingsAuditEntryAsync(
                "Member event background configured",
                "The member event card background was updated.");
        }

        [SlashCommand("background-reset", "Restore the default background for member event cards.")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task ResetBackgroundAsync()
        {
            await DeferAsync(ephemeral: true);

            if (!await _eventBackgroundService.ResetAsync(Context.Guild.Id))
            {
                await ReplyAsync(
                    "I could not reset the member event card background. Please try again.");
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} reset the member event card background in guild {GuildId}.",
                Context.User.Id,
                Context.Guild.Id);

            await ReplyAsync(
                "Member event cards will use the default background image.");

            await WriteSettingsAuditEntryAsync(
                "Member event background reset",
                "The default member event card background was restored.");
        }

        [SlashCommand("remove", "Stop sending welcome, goodbye, and boost notices.")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task RemoveAsync()
        {
            await DeferAsync(ephemeral: true);

            if (!await _eventChannelService.RemoveEventChannelAsync(Context.Guild.Id))
            {
                await ReplyAsync(
                    "I could not remove the member event channel. Please try again.");
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} removed member event channel in guild {GuildId}.",
                Context.User.Id,
                Context.Guild.Id);

            await ReplyAsync(
                "Welcome, goodbye, and boost notices will no longer be sent to a channel.");

            await WriteSettingsAuditEntryAsync(
                "Member event channel removed",
                "The member event channel was removed.");
        }

        private Task WriteSettingsAuditEntryAsync(string action, string details) =>
            _auditChannelService.WriteAsync(
                new ModerationAuditEntry(
                    Context.Guild.Id,
                    action,
                    Context.User.Id,
                    null,
                    "Bot settings changed.",
                    details,
                    DateTimeOffset.UtcNow));
    }

    [Group("leveling", "Configure message XP, levels, and cosmetic role rewards.")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public sealed class LevelingSettingsModule : DeferredInteractionModuleBase
    {
        private readonly ILevelingStore _levelingStore;
        private readonly ILogger<LevelingSettingsModule> _logger;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly IAuditChannelService _auditChannelService;

        public LevelingSettingsModule(
            ILevelingStore levelingStore,
            ILogger<LevelingSettingsModule> logger,
            IHostApplicationLifetime lifetime,
            IAuditChannelService auditChannelService)
        {
            _levelingStore = levelingStore;
            _logger = logger;
            _lifetime = lifetime;
            _auditChannelService = auditChannelService;
        }

        [SlashCommand("enable", "Enable message XP and leveling in this server.")]
        public Task EnableAsync() => SetEnabledAsync(true);

        [SlashCommand("disable", "Disable message XP without deleting progress or settings.")]
        public Task DisableAsync() => SetEnabledAsync(false);

        [SlashCommand("status", "Show leveling settings, exclusions, and role rewards.")]
        public async Task StatusAsync()
        {
            await DeferAsync(ephemeral: true);

            var settings = await _levelingStore.GetSettingsAsync(
                Context.Guild.Id, _lifetime.ApplicationStopping);
            var embed = new EmbedBuilder()
                .WithTitle("Leveling settings")
                .WithColor(Color.Blue)
                .AddField("Message XP", settings.Enabled ? "Enabled" : "Disabled", inline: true)
                .AddField("XP cooldown", $"{settings.CooldownSeconds} seconds per member", inline: true)
                .AddField("Excluded channel IDs", FormatList(
                    settings.ExcludedChannelIds.Order().ToArray(), id => $"`{id}`"))
                .AddField("Excluded role IDs", FormatList(
                    settings.ExcludedRoleIds.Order().ToArray(), id => $"`{id}`"))
                .AddField("Role rewards", FormatList(
                    settings.Rewards.OrderBy(reward => reward.Level).ToArray(),
                    reward => $"Level {reward.Level}: role `{reward.RoleId}`"))
                .WithFooter("Use cosmetic reward roles only. Leveling is for fun, not server permissions.")
                .Build();

            await ReplyAsync(embed: embed);
        }

        [SlashCommand("cooldown", "Set the minimum interval between XP awards for each member.")]
        public async Task CooldownAsync(
            [Summary("seconds", "Seconds between XP awards, from 15 to 3600.")]
            [MinValue(15), MaxValue(3600)] int seconds)
        {
            await DeferAsync(ephemeral: true);
            if (seconds is < 15 or > 3600)
            {
                await ReplyAsync("The XP cooldown must be between 15 and 3600 seconds.");
                return;
            }

            await SaveChangeAsync(
                token => _levelingStore.SetCooldownAsync(Context.Guild.Id, seconds, token),
                "cooldown", seconds.ToString(),
                $"Members can earn message XP once every {seconds} seconds.");
        }

        [SlashCommand("exclude-channel", "Stop awarding message XP in a text channel.")]
        public Task ExcludeChannelAsync(
            [Summary("channel", "The server text channel to exclude from message XP.")]
            ITextChannel channel) => SetChannelExcludedAsync(channel, true);

        [SlashCommand("include-channel", "Remove a text channel from the message XP exclusions.")]
        public Task IncludeChannelAsync(
            [Summary("channel", "The server text channel to remove from exclusions.")]
            ITextChannel channel) => SetChannelExcludedAsync(channel, false);

        [SlashCommand("exclude-role", "Stop awarding message XP to members with a role.")]
        public Task ExcludeRoleAsync(
            [Summary("role", "Members with this server role will not earn message XP.")]
            IRole role) => SetRoleExcludedAsync(role, true);

        [SlashCommand("include-role", "Remove a role from the message XP exclusions.")]
        public Task IncludeRoleAsync(
            [Summary("role", "The server role to remove from exclusions.")]
            IRole role) => SetRoleExcludedAsync(role, false);

        [SlashCommand("reward-add", "Set a cosmetic role reward for reaching a level.")]
        [RequireUserPermission(GuildPermission.ManageRoles)]
        public async Task RewardAddAsync(
            [Summary("level", "The reward level, from 1 to 1000.")]
            [MinValue(1), MaxValue(1000)] int level,
            [Summary("role", "A cosmetic server role with no moderation or management permissions.")]
            IRole role)
        {
            await DeferAsync(ephemeral: true);
            if (!await ValidateLevelAsync(level) || !await ValidateRewardRoleAsync(role))
            {
                return;
            }

            await SaveChangeAsync(
                token => _levelingStore.SetRewardAsync(Context.Guild.Id, level, role.Id, token),
                "role reward", $"level {level}: role {role.Id}",
                $"Level {level} will award <@&{role.Id}>. Use cosmetic reward roles only; " +
                "leveling is for fun, not server permissions.");
        }

        [SlashCommand("reward-remove", "Remove the role reward configured for a level.")]
        public async Task RewardRemoveAsync(
            [Summary("level", "The reward level to remove, from 1 to 1000.")]
            [MinValue(1), MaxValue(1000)] int level)
        {
            await DeferAsync(ephemeral: true);
            if (!await ValidateLevelAsync(level))
            {
                return;
            }

            await SaveChangeAsync(
                token => _levelingStore.RemoveRewardAsync(Context.Guild.Id, level, token),
                "role reward", $"level {level}: removed",
                $"The role reward for level {level} was removed. Existing member roles were kept.");
        }

        private async Task SetEnabledAsync(bool enabled)
        {
            await DeferAsync(ephemeral: true);
            await SaveChangeAsync(
                token => _levelingStore.SetEnabledAsync(Context.Guild.Id, enabled, token),
                "enabled", enabled.ToString(),
                enabled ? "Message XP and leveling are enabled." :
                    "Message XP and leveling are disabled. Existing progress and settings were kept.");
        }

        private async Task SetChannelExcludedAsync(ITextChannel channel, bool excluded)
        {
            await DeferAsync(ephemeral: true);
            if (channel.GuildId != Context.Guild.Id)
            {
                await ReplyAsync("Select a text channel from this server.");
                return;
            }

            await SaveChangeAsync(
                token => _levelingStore.SetChannelExcludedAsync(Context.Guild.Id, channel.Id, excluded, token),
                "channel exclusion", $"channel {channel.Id}: {excluded}",
                excluded ? $"Messages in <#{channel.Id}> will not earn XP." :
                    $"<#{channel.Id}> is no longer excluded from message XP. Role exclusions still apply.");
        }

        private async Task SetRoleExcludedAsync(IRole role, bool excluded)
        {
            await DeferAsync(ephemeral: true);
            if (role.Guild.Id != Context.Guild.Id)
            {
                await ReplyAsync("Select a role from this server.");
                return;
            }

            await SaveChangeAsync(
                token => _levelingStore.SetRoleExcludedAsync(Context.Guild.Id, role.Id, excluded, token),
                "role exclusion", $"role {role.Id}: {excluded}",
                excluded ? $"Members with <@&{role.Id}> will not earn message XP." :
                    $"<@&{role.Id}> is no longer excluded from message XP. Other exclusions still apply.");
        }

        private async Task<bool> ValidateLevelAsync(int level)
        {
            if (level is >= 1 and <= 1000)
            {
                return true;
            }

            await ReplyAsync("The reward level must be between 1 and 1000.");
            return false;
        }

        private async Task<bool> ValidateRewardRoleAsync(IRole role)
        {
            if (role.Guild.Id != Context.Guild.Id)
            {
                await ReplyAsync("Select a role from this server.");
                return false;
            }

            if (role.Id == Context.Guild.Id || role.IsManaged)
            {
                await ReplyAsync("Reward roles cannot be @everyone or managed by Discord or an integration.");
                return false;
            }

            if (!LevelingRewardSafety.IsSafe(role.Permissions) ||
                Context.Guild.Channels.Any(channel => channel.GetPermissionOverwrite(role) is { } overwrite &&
                    !LevelingRewardSafety.IsSafe(overwrite)))
            {
                await ReplyAsync(
                    "Use a cosmetic reward role without moderation or server management permissions.");
                return false;
            }

            if (Context.User is not SocketGuildUser caller || !caller.GuildPermissions.ManageRoles)
            {
                await ReplyAsync("You need Manage Roles to configure leveling role rewards.");
                return false;
            }

            var bot = Context.Guild.CurrentUser;
            if (bot is null || !bot.GuildPermissions.ManageRoles)
            {
                await ReplyAsync("I need Manage Roles to award leveling roles.");
                return false;
            }

            if (role.Position >= bot.Hierarchy)
            {
                await ReplyAsync("The reward role must be below my highest role.");
                return false;
            }

            if (Context.User.Id != Context.Guild.OwnerId)
            {
                if (Context.User is not SocketGuildUser moderator ||
                    moderator.Guild.Id != Context.Guild.Id)
                {
                    await ReplyAsync("I could not verify your server role. Please try again.");
                    return false;
                }

                if (role.Position >= moderator.Hierarchy)
                {
                    await ReplyAsync("The reward role must be below your highest role.");
                    return false;
                }
            }

            return true;
        }

        private async Task SaveChangeAsync(
            Func<CancellationToken, Task> update,
            string setting,
            string value,
            string message)
        {
            await update(_lifetime.ApplicationStopping);

            _logger.LogInformation(
                "Moderator {ModeratorId} set leveling {Setting} to {Value} in guild {GuildId}.",
                Context.User.Id, setting, value, Context.Guild.Id);

            await ReplyAsync(message);
            await _auditChannelService.WriteAsync(
                new ModerationAuditEntry(
                    Context.Guild.Id,
                    "Leveling settings changed",
                    Context.User.Id,
                    null,
                    "Bot settings changed.",
                    $"{setting}: {value}.",
                    DateTimeOffset.UtcNow),
                _lifetime.ApplicationStopping);
        }

        private static string FormatList<T>(IReadOnlyList<T> items, Func<T, string> format)
        {
            if (items.Count == 0)
            {
                return "None";
            }

            // Leave room for the omitted count within Discord's 1024-character field limit.
            var text = new StringBuilder();
            var shown = 0;
            while (shown < items.Count && shown < 20)
            {
                var line = format(items[shown]);
                if (text.Length + line.Length + 1 > 960)
                {
                    break;
                }

                text.AppendLine(line);
                shown++;
            }

            if (shown < items.Count)
            {
                text.Append($"… and {items.Count - shown} more.");
            }

            return text.ToString().TrimEnd();
        }
    }

    [Group("warnings", "Configure warning escalation.")]
    public sealed class WarningSettingsModule : DeferredInteractionModuleBase
    {
        private const int MaximumTimeoutMinutes = 28 * 24 * 60;

        private readonly IAuditChannelService _auditChannelService;
        private readonly IGuildChannelConfigurationStore _configurationStore;
        private readonly ILogger<WarningSettingsModule> _logger;

        public WarningSettingsModule(
            IAuditChannelService auditChannelService,
            IGuildChannelConfigurationStore configurationStore,
            ILogger<WarningSettingsModule> logger)
        {
            _auditChannelService = auditChannelService;
            _configurationStore = configurationStore;
            _logger = logger;
        }

        [SlashCommand("set-escalation", "Set the warning count that automatically applies a timeout.")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task SetEscalationAsync(
            [Summary("threshold", "Warnings needed before the timeout is applied.")] int threshold,
            [Summary("timeout-minutes", "Automatic timeout length, up to 28 days.")] int timeoutMinutes)
        {
            await DeferAsync(ephemeral: true);

            if (threshold <= 0)
            {
                await ReplyAsync(
                    "The warning threshold must be at least 1.");
                return;
            }

            if (timeoutMinutes is <= 0 or > MaximumTimeoutMinutes)
            {
                await ReplyAsync(
                    "The escalation timeout must be between 1 minute and 28 days.");
                return;
            }

            await _configurationStore.SetWarningEscalationAsync(
                Context.Guild.Id,
                new WarningEscalation(threshold, timeoutMinutes));

            _logger.LogInformation(
                "Moderator {ModeratorId} set warning escalation to {Threshold} warnings and {TimeoutMinutes} minutes in guild {GuildId}.",
                Context.User.Id,
                threshold,
                timeoutMinutes,
                Context.Guild.Id);

            await ReplyAsync(
                $"Members who receive {threshold} warning(s) will be timed out for {timeoutMinutes} minute(s).");

            await WriteSettingsAuditEntryAsync(
                "Warning escalation configured",
                $"At {threshold} warning(s), apply a {timeoutMinutes}-minute timeout.");
        }

        [SlashCommand("remove-escalation", "Stop automatically timing out members for warnings.")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task RemoveEscalationAsync()
        {
            await DeferAsync(ephemeral: true);

            await _configurationStore.RemoveWarningEscalationAsync(Context.Guild.Id);

            _logger.LogInformation(
                "Moderator {ModeratorId} removed warning escalation in guild {GuildId}.",
                Context.User.Id,
                Context.Guild.Id);

            await ReplyAsync(
                "Warning escalation has been disabled. Existing warnings were kept.");

            await WriteSettingsAuditEntryAsync(
                "Warning escalation removed",
                "Automatic warning timeouts were disabled. Existing warnings were kept.");
        }

        private Task WriteSettingsAuditEntryAsync(string action, string details) =>
            _auditChannelService.WriteAsync(
                new ModerationAuditEntry(
                    Context.Guild.Id,
                    action,
                    Context.User.Id,
                    null,
                    "Bot settings changed.",
                    details,
                    DateTimeOffset.UtcNow));
    }
}
