using Arisu.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Arisu.Modules;

[RequireContext(ContextType.Guild)]
[Group("settings", "Configure server channels.")]
public sealed class SettingsModule : InteractionModuleBase<SocketInteractionContext>
{
    [Group("moderation-logs", "Configure the server audit channel.")]
    public sealed class ModerationSettingsModule : InteractionModuleBase<SocketInteractionContext>
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
            if (!await _auditChannelService.SetAuditChannelAsync(Context.Guild.Id, channel.Id))
            {
                await RespondAsync(
                    "I could not save the server audit channel. Please try again.",
                    ephemeral: true);
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} set server audit channel {ChannelId} in guild {GuildId}.",
                Context.User.Id,
                channel.Id,
                Context.Guild.Id);

            await RespondAsync(
                $"Server audit entries will be sent to {channel.Mention}.",
                ephemeral: true,
                allowedMentions: AllowedMentions.None);

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
            await WriteSettingsAuditEntryAsync(
                "Server audit channel removal requested",
                "The server audit channel is being removed.");

            if (!await _auditChannelService.RemoveAuditChannelAsync(Context.Guild.Id))
            {
                await RespondAsync(
                    "I could not remove the server audit channel. Please try again.",
                    ephemeral: true);
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} removed server audit channel in guild {GuildId}.",
                Context.User.Id,
                Context.Guild.Id);

            await RespondAsync(
                "Server audit entries will no longer be sent to a channel.",
                ephemeral: true);
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
    public sealed class MemberLogsSettingsModule : InteractionModuleBase<SocketInteractionContext>
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
            if (!await _memberLogChannelService.SetMemberLogChannelAsync(
                    Context.Guild.Id,
                    channel.Id))
            {
                await RespondAsync(
                    "I could not save the member log channel. Please try again.",
                    ephemeral: true);
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} set member log channel {ChannelId} in guild {GuildId}.",
                Context.User.Id,
                channel.Id,
                Context.Guild.Id);

            await RespondAsync(
                $"Member activity logs will be sent to {channel.Mention}.",
                ephemeral: true,
                allowedMentions: AllowedMentions.None);

            await WriteSettingsAuditEntryAsync(
                "Member log channel configured",
                $"Member log channel set to <#{channel.Id}>.");
        }

        [SlashCommand("remove", "Stop sending member activity logs.")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task RemoveAsync()
        {
            if (!await _memberLogChannelService.RemoveMemberLogChannelAsync(Context.Guild.Id))
            {
                await RespondAsync(
                    "I could not remove the member log channel. Please try again.",
                    ephemeral: true);
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} removed member log channel in guild {GuildId}.",
                Context.User.Id,
                Context.Guild.Id);

            await RespondAsync(
                "Member activity logs will no longer be sent to a channel.",
                ephemeral: true);

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

    [Group("member-events", "Configure the welcome, goodbye, and boost channel.")]
    public sealed class MemberEventsSettingsModule : InteractionModuleBase<SocketInteractionContext>
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
            if (!await _eventChannelService.SetEventChannelAsync(Context.Guild.Id, channel.Id))
            {
                await RespondAsync(
                    "I could not save the member event channel. Please try again.",
                    ephemeral: true);
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} set member event channel {ChannelId} in guild {GuildId}.",
                Context.User.Id,
                channel.Id,
                Context.Guild.Id);

            await RespondAsync(
                $"Welcome, goodbye, and boost notices will be sent to {channel.Mention}.",
                ephemeral: true,
                allowedMentions: AllowedMentions.None);

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
            if (!await _eventBackgroundService.ResetAsync(Context.Guild.Id))
            {
                await RespondAsync(
                    "I could not reset the member event card background. Please try again.",
                    ephemeral: true);
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} reset the member event card background in guild {GuildId}.",
                Context.User.Id,
                Context.Guild.Id);

            await RespondAsync(
                "Member event cards will use the default background image.",
                ephemeral: true);

            await WriteSettingsAuditEntryAsync(
                "Member event background reset",
                "The default member event card background was restored.");
        }

        [SlashCommand("remove", "Stop sending welcome, goodbye, and boost notices.")]
        [RequireUserPermission(GuildPermission.ManageGuild)]
        public async Task RemoveAsync()
        {
            if (!await _eventChannelService.RemoveEventChannelAsync(Context.Guild.Id))
            {
                await RespondAsync(
                    "I could not remove the member event channel. Please try again.",
                    ephemeral: true);
                return;
            }

            _logger.LogInformation(
                "Moderator {ModeratorId} removed member event channel in guild {GuildId}.",
                Context.User.Id,
                Context.Guild.Id);

            await RespondAsync(
                "Welcome, goodbye, and boost notices will no longer be sent to a channel.",
                ephemeral: true);

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
}
