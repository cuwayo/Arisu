using Arisu.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Arisu.Modules;

[RequireContext(ContextType.Guild)]
public sealed class ChannelModerationModule : ModerationModuleBase
{
    private readonly IChannelLockService _channelLockService;
    private readonly IAuditChannelService _auditChannelService;
    private readonly ILogger<ChannelModerationModule> _logger;

    public ChannelModerationModule(
        IChannelLockService channelLockService,
        ILogger<ChannelModerationModule> logger,
        IAuditChannelService auditChannelService)
    {
        _channelLockService = channelLockService;
        _logger = logger;
        _auditChannelService = auditChannelService;
    }

    [SlashCommand("purge", "Delete recent messages from this channel.")]
    [RequireUserPermission(ChannelPermission.ManageMessages)]
    [RequireBotPermission(ChannelPermission.ManageMessages)]
    public async Task PurgeAsync(
        [Summary("amount", "Number of messages to delete, from 1 to 100.")]
        [MinValue(1), MaxValue(100)] int amount,
        [Summary("user", "Only delete messages sent by this user.")] SocketGuildUser? user = null)
    {
        await DeferAsync(ephemeral: true);

        if (amount is < 1 or > 100)
        {
            await ReplyAsync("The purge amount must be between 1 and 100.");
            return;
        }

        var channel = await GetCommandChannelAsync();
        if (channel is null || !await CheckPermissionsAsync(channel, GuildPermission.ManageMessages))
        {
            return;
        }

        if (user is not null && user.Guild.Id != Context.Guild.Id)
        {
            await ReplyAsync("Select a user from this server.");
            return;
        }

        var botPermissions = Context.Guild.CurrentUser.GetPermissions(channel);
        if (!botPermissions.ViewChannel || !botPermissions.ReadMessageHistory)
        {
            await ReplyAsync("I need View Channel and Read Message History in this channel to purge messages.");
            return;
        }

        var messages = (await channel.GetMessagesAsync(user is null ? amount : 100).FlattenAsync())
            .OrderByDescending(message => message.Id)
            .ToList();
        var matching = messages
            .Where(message => user is null || message.Author.Id == user.Id)
            .Take(amount)
            .ToList();
        var deletable = matching
            .Where(message => ChannelPurgePolicy.CanDelete(message.Timestamp, DateTimeOffset.UtcNow))
            .ToList();
        var tooOld = matching.Count - deletable.Count;
        var deleted = 0;

        if (deletable.Count > 0)
        {
            try
            {
                var options = new RequestOptions
                {
                    AuditLogReason = BuildAuditReason($"Purge requested {amount} message(s).")
                };
                if (deletable.Count == 1)
                {
                    await channel.DeleteMessageAsync(deletable[0].Id, options);
                }
                else
                {
                    await channel.DeleteMessagesAsync(deletable.Select(message => message.Id), options);
                }

                deleted = deletable.Count;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not purge messages in channel {ChannelId} in guild {GuildId}.",
                    channel.Id,
                    Context.Guild.Id);
                await ReplyAsync("I could not delete the matching messages. Please try again.");
                return;
            }
        }

        var skipped = amount - deleted;
        _logger.LogInformation(
            "Moderator {ModeratorId} purged {DeletedCount} of {RequestedCount} requested messages in channel {ChannelId} in guild {GuildId}; skipped {SkippedCount}.",
            Context.User.Id,
            deleted,
            amount,
            channel.Id,
            Context.Guild.Id,
            skipped);

        await ReplyAsync(
            $"Requested: {amount}. Deleted: {deleted}. Skipped/unavailable: {skipped}. " +
            $"Scanned {messages.Count} recent message(s); {tooOld} matching message(s) were too close to or beyond the 14-day limit. " +
            $"Found {matching.Count} matching message(s).");
        await WriteAuditAsync(
            "Messages purged",
            channel,
            $"Requested {amount}; deleted {deleted}; skipped/unavailable {skipped}. " +
            $"Scanned {messages.Count} messages; user filter: {(user is null ? "none" : user.Id.ToString())}.");
    }

    [SlashCommand("slowmode", "Set the message slowmode for a text channel.")]
    public async Task SlowmodeAsync(
        [Summary("seconds", "Slowmode duration, from 0 to 21600 seconds.")]
        [MinValue(0), MaxValue(21600)] int seconds,
        [Summary("channel", "The text channel to update; defaults to this channel.")] SocketTextChannel? channel = null)
    {
        await DeferAsync(ephemeral: true);

        if (seconds is < 0 or > 21600)
        {
            await ReplyAsync("Slowmode must be between 0 and 21600 seconds.");
            return;
        }

        var target = await GetCommandChannelAsync(channel);
        if (target is null || !await CheckPermissionsAsync(target, GuildPermission.ManageChannels))
        {
            return;
        }

        try
        {
            await target.ModifyAsync(
                properties => properties.SlowModeInterval = seconds,
                new RequestOptions { AuditLogReason = BuildAuditReason($"Set slowmode to {seconds} second(s).") });
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not set slowmode for channel {ChannelId} in guild {GuildId}.",
                target.Id,
                Context.Guild.Id);
            await ReplyAsync("I could not update slowmode. Please try again.");
            return;
        }

        await ReplyAsync(
            seconds == 0
                ? $"Slowmode is disabled for {target.Mention}."
                : $"Slowmode is set to {seconds} second(s) for {target.Mention}.");
        await WriteAuditAsync("Channel slowmode updated", target, $"Set slowmode to {seconds} second(s).");
    }

    [SlashCommand("lock", "Prevent the @everyone role from sending messages in a channel.")]
    public Task LockAsync(
        [Summary("channel", "The text channel to lock; defaults to this channel.")] SocketTextChannel? channel = null,
        [Summary("reason", "Reason for locking the channel.")] string reason = "No reason provided.") =>
        SetLockAsync(channel, reason, unlock: false);

    [SlashCommand("unlock", "Restore a channel's original @everyone send permission.")]
    public Task UnlockAsync(
        [Summary("channel", "The text channel to unlock; defaults to this channel.")] SocketTextChannel? channel = null,
        [Summary("reason", "Reason for unlocking the channel.")] string reason = "No reason provided.") =>
        SetLockAsync(channel, reason, unlock: true);

    private async Task SetLockAsync(SocketTextChannel? channel, string reason, bool unlock)
    {
        await DeferAsync(ephemeral: true);

        var normalizedReason = await GetReasonAsync(reason);
        if (normalizedReason is null)
        {
            return;
        }

        var target = await GetCommandChannelAsync(channel);
        if (target is null || !await CheckPermissionsAsync(target, GuildPermission.ManageChannels))
        {
            return;
        }

        IGuildUser? moderator = Context.Guild.GetUser(Context.User.Id);
        moderator ??= await ((IGuild)Context.Guild).GetUserAsync(Context.User.Id, CacheMode.AllowDownload);
        if (moderator?.GetPermissions(target).ManageRoles != true)
        {
            await ReplyAsync("You need Manage Permissions in that channel to update its permission overwrites.");
            return;
        }

        if (!Context.Guild.CurrentUser.GetPermissions(target).ManageRoles)
        {
            await ReplyAsync("I need Manage Permissions in that channel to update its permission overwrites.");
            return;
        }

        ChannelLockResult result;
        try
        {
            result = unlock
                ? await _channelLockService.UnlockAsync(target, BuildAuditReason(normalizedReason))
                : await _channelLockService.LockAsync(target, BuildAuditReason(normalizedReason));
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not {Action} channel {ChannelId} in guild {GuildId}.",
                unlock ? "unlock" : "lock",
                target.Id,
                Context.Guild.Id);
            await ReplyAsync("I could not update the channel lock. The saved permission state was retained; please try again.");
            return;
        }

        switch (result)
        {
            case ChannelLockResult.Locked:
                await ReplyAsync($"Locked {target.Mention}.");
                await WriteAuditAsync("Channel locked", target, normalizedReason);
                break;
            case ChannelLockResult.Unlocked:
                await ReplyAsync($"Unlocked {target.Mention} and restored its previous @everyone send permission.");
                await WriteAuditAsync("Channel unlocked", target, normalizedReason);
                break;
            case ChannelLockResult.AlreadyLocked:
                await ReplyAsync($"{target.Mention} is already locked.");
                break;
            case ChannelLockResult.NotLocked:
                await ReplyAsync($"{target.Mention} has no saved lock state to restore.");
                break;
            case ChannelLockResult.PermissionsChanged:
                await ReplyAsync("The channel permissions changed while the lock was being managed. I left them unchanged for safety.");
                break;
        }
    }

    private async Task<SocketTextChannel?> GetCommandChannelAsync(SocketTextChannel? requested = null)
    {
        var target = requested ?? Context.Channel as SocketTextChannel;
        if (target is null || target is IThreadChannel || target.Guild.Id != Context.Guild.Id)
        {
            await ReplyAsync("That channel is not a text channel in this server.");
            return null;
        }

        return target;
    }

    private async Task<bool> CheckPermissionsAsync(
        SocketTextChannel channel,
        GuildPermission requiredPermission)
    {
        IGuildUser? moderator = Context.Guild.GetUser(Context.User.Id);
        moderator ??= await ((IGuild)Context.Guild).GetUserAsync(
            Context.User.Id, CacheMode.AllowDownload);
        var bot = Context.Guild.CurrentUser;
        var moderatorPermissions = moderator?.GetPermissions(channel);
        var botPermissions = bot?.GetPermissions(channel);
        var moderatorAllowed = requiredPermission == GuildPermission.ManageMessages
            ? moderatorPermissions?.ManageMessages == true
            : moderatorPermissions?.ManageChannel == true;
        var botAllowed = requiredPermission == GuildPermission.ManageMessages
            ? botPermissions?.ManageMessages == true
            : botPermissions?.ManageChannel == true;

        if (!moderatorAllowed)
        {
            await ReplyAsync("You do not have the required permission in that channel.");
            return false;
        }

        if (!botAllowed)
        {
            await ReplyAsync("I do not have the required permission in that channel.");
            return false;
        }

        return true;
    }

    private async Task WriteAuditAsync(string action, SocketTextChannel channel, string reason)
    {
        _logger.LogInformation(
            "Moderator {ModeratorId} performed {Action} on channel {ChannelId} in guild {GuildId}.",
            Context.User.Id,
            action,
            channel.Id,
            Context.Guild.Id);
        try
        {
            await _auditChannelService.WriteAsync(
                new ModerationAuditEntry(
                    Context.Guild.Id,
                    action,
                    Context.User.Id,
                    $"<#{channel.Id}>",
                    reason,
                    null,
                    DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not write channel moderation audit entry in guild {GuildId}.", Context.Guild.Id);
        }
    }
}

public static class ChannelPurgePolicy
{
    // Leave room for network delays and Discord's clock when bulk deleting.
    public static bool CanDelete(DateTimeOffset messageTimestamp, DateTimeOffset now) =>
        messageTimestamp > now - TimeSpan.FromDays(14) + TimeSpan.FromMinutes(1);
}
