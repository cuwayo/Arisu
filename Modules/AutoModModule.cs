using System.Net;
using Arisu.Services;
using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arisu.Modules;

[RequireContext(ContextType.Guild)]
[RequireUserPermission(GuildPermission.ManageGuild)]
[RequireBotPermission(GuildPermission.ManageGuild)]
public abstract class AutoModCommandModuleBase : DeferredInteractionModuleBase
{
    protected IDiscordAutoModService AutoMod { get; }
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AutoModModule> _logger;

    protected AutoModCommandModuleBase(
        IDiscordAutoModService autoMod, IHostApplicationLifetime lifetime, ILogger<AutoModModule> logger)
    {
        AutoMod = autoMod;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected async Task ExecuteAsync(Func<CancellationToken, Task<string>> operation)
    {
        await DeferAsync(ephemeral: true);
        try
        {
            await ReplyAsync(await operation(_lifetime.ApplicationStopping));
        }
        catch (AutoModConfigurationException exception)
        {
            await ReplyAsync(exception.Message);
        }
        catch (HttpException exception)
        {
            _logger.LogWarning(exception,
                "Discord rejected AutoMod configuration by {ActorId} in guild {GuildId}; HTTP {StatusCode}, code {DiscordCode}.",
                Context.User.Id, Context.Guild.Id, exception.HttpCode, exception.DiscordCode);
            var message = exception.HttpCode switch
            {
                HttpStatusCode.Forbidden =>
                    "Discord denied this operation. Both you and Arisu need Manage Server. " +
                    "For alerts, check my View Channel, Send Messages, and Embed Links permissions.",
                HttpStatusCode.NotFound =>
                    "The rule or channel no longer exists. Refresh /automod status and try again.",
                HttpStatusCode.BadRequest =>
                    "Discord rejected these rule settings. Check Server Settings → AutoMod for rule quotas, " +
                    "keyword limits, exemptions, and supported actions. " +
                    "Discord allows 1 spam rule, 1 mention rule, 6 keyword rules, 1,000 keywords per rule, " +
                    "20 exempt roles, and 50 exempt channels. Disabled rules also count.",
                HttpStatusCode.TooManyRequests =>
                    "Discord is rate limiting AutoMod changes. Wait briefly, then check /automod status before retrying.",
                _ => "Discord could not complete this operation. Check /automod status before retrying."
            };
            await ReplyAsync(message);
        }
        catch (OperationCanceledException)
        {
            await ReplyAsync("The operation was interrupted. Check /automod status before retrying.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "AutoMod request failed in guild {GuildId}.", Context.Guild.Id);
            await ReplyAsync("Discord could not be reached. Check /automod status before retrying.");
        }
    }
}

[Group("automod", "Configure Discord's native automatic moderation.")]
[DefaultMemberPermissions(GuildPermission.ManageGuild)]
public sealed class AutoModModule : AutoModCommandModuleBase
{
    public AutoModModule(
        IDiscordAutoModService autoMod, IHostApplicationLifetime lifetime, ILogger<AutoModModule> logger)
        : base(autoMod, lifetime, logger)
    {
    }

    [SlashCommand("status", "Show owned native rules, quotas, exemptions, and filter limitations.")]
    public Task StatusAsync() =>
        ExecuteAsync(token => AutoMod.GetStatusAsync(Context.Guild, token));

    [SlashCommand("mentions", "Set the native unique mention limit and mention raid protection.")]
    public Task MentionsAsync(
        [Summary("limit", "Maximum unique mentions per message, from 1 to 50.")]
        [MinValue(1), MaxValue(50)] int limit,
        [Summary("enabled", "Enable blocking and mention raid protection, or disable the owned rule.")] bool enabled = true,
        [Summary("alert-channel", "Optional native alert text channel. Omit to preserve existing alerts.")]
        [ChannelTypes(ChannelType.Text)] SocketTextChannel? alertChannel = null) =>
        ExecuteAsync(token => AutoMod.SetFilterAsync(
            Context.Guild, Context.User.Id, AutoModFilter.Mentions, enabled, limit, alertChannel, token));

    [Group("spam", "Configure Discord's native spam detector; sensitivity is controlled by Discord.")]
    public sealed class SpamModule : AutoModCommandModuleBase
    {
        public SpamModule(
            IDiscordAutoModService autoMod, IHostApplicationLifetime lifetime, ILogger<AutoModModule> logger)
            : base(autoMod, lifetime, logger)
        {
        }

        [SlashCommand("enable", "Enable native spam blocking; Discord controls detection and sensitivity.")]
        public Task EnableAsync(
            [Summary("alert-channel", "Optional native alert text channel. Omit to preserve existing alerts.")]
            [ChannelTypes(ChannelType.Text)] SocketTextChannel? alertChannel = null) =>
            ExecuteAsync(token => AutoMod.SetFilterAsync(
                Context.Guild, Context.User.Id, AutoModFilter.Spam, true, alertChannel: alertChannel,
                cancellationToken: token));

        [SlashCommand("disable", "Disable only Arisu's owned spam rule; retain its settings.")]
        public Task DisableAsync() =>
            ExecuteAsync(token => AutoMod.SetFilterAsync(
                Context.Guild, Context.User.Id, AutoModFilter.Spam, false, cancellationToken: token));
    }

    [Group("invites", "Block ordinary Discord invite links with a native regex rule.")]
    public sealed class InvitesModule : AutoModCommandModuleBase
    {
        public InvitesModule(
            IDiscordAutoModService autoMod, IHostApplicationLifetime lifetime, ILogger<AutoModModule> logger)
            : base(autoMod, lifetime, logger)
        {
        }

        [SlashCommand("enable", "Block discord.gg and discord.com/invite links; obfuscation can bypass this filter.")]
        public Task EnableAsync(
            [Summary("alert-channel", "Optional native alert text channel. Omit to preserve existing alerts.")]
            [ChannelTypes(ChannelType.Text)] SocketTextChannel? alertChannel = null) =>
            ExecuteAsync(token => AutoMod.SetFilterAsync(
                Context.Guild, Context.User.Id, AutoModFilter.Invites, true, alertChannel: alertChannel,
                cancellationToken: token));

        [SlashCommand("disable", "Disable only Arisu's owned invite rule; retain its settings.")]
        public Task DisableAsync() =>
            ExecuteAsync(token => AutoMod.SetFilterAsync(
                Context.Guild, Context.User.Id, AutoModFilter.Invites, false, cancellationToken: token));
    }

    [Group("words", "Manage case-insensitive native keywords and phrases, with optional * at either end.")]
    public sealed class WordsModule : AutoModCommandModuleBase
    {
        public WordsModule(
            IDiscordAutoModService autoMod, IHostApplicationLifetime lifetime, ILogger<AutoModModule> logger)
            : base(autoMod, lifetime, logger)
        {
        }

        [SlashCommand("add", "Add one native keyword or phrase and enable the owned Words rule.")]
        public Task AddAsync(
            [Summary("keyword", "One keyword or phrase; optional * at either end. Not a regex.")]
            [MinLength(1), MaxLength(60)] string keyword,
            [Summary("alert-channel", "Optional native alert text channel. Omit to preserve existing alerts.")]
            [ChannelTypes(ChannelType.Text)] SocketTextChannel? alertChannel = null) =>
            ExecuteAsync(token => AutoMod.AddWordAsync(Context.Guild, Context.User.Id, keyword, alertChannel, token));

        [SlashCommand("remove", "Remove one keyword. Removing the last deletes the Words rule and its settings.")]
        public Task RemoveAsync(
            [Summary("keyword", "The configured keyword or wildcard phrase to remove, ignoring case.")]
            [MinLength(1), MaxLength(60)] string keyword) =>
            ExecuteAsync(token => AutoMod.RemoveWordAsync(Context.Guild, Context.User.Id, keyword, token));

        [SlashCommand("list", "List the owned Words rule's keywords in pages of 10.")]
        public Task ListAsync(
            [Summary("page", "Page number, starting at 1.")]
            [MinValue(1), MaxValue(100)] int page = 1) =>
            ExecuteAsync(token => AutoMod.ListWordsAsync(Context.Guild, page, token));
    }

    [Group("exempt", "Add or remove one explicit exemption on one owned rule. Grants no server permissions.")]
    public sealed class ExemptModule : AutoModCommandModuleBase
    {
        public ExemptModule(
            IDiscordAutoModService autoMod, IHostApplicationLifetime lifetime, ILogger<AutoModModule> logger)
            : base(autoMod, lifetime, logger)
        {
        }

        [SlashCommand("role", "Set one role exemption on one owned rule; @everyone is refused.")]
        public Task RoleAsync(
            [Summary("filter", "The owned rule to change.")] AutoModFilter filter,
            [Summary("role", "A server role to exempt or stop exempting; never @everyone.")] SocketRole role,
            [Summary("exempt", "True adds this exemption; false removes it.")] bool exempt = true) =>
            ExecuteAsync(token => AutoMod.SetExemptionAsync(
                Context.Guild, Context.User.Id, filter, role.Id, true, exempt, token));

        [SlashCommand("channel", "Set one channel exemption on one owned rule; categories include child channels.")]
        public Task ChannelAsync(
            [Summary("filter", "The owned rule to change.")] AutoModFilter filter,
            [Summary("channel", "The server channel to exempt or stop exempting; categories include children.")]
            [ChannelTypes(ChannelType.Text, ChannelType.News, ChannelType.Forum, ChannelType.Category)]
            SocketGuildChannel channel,
            [Summary("exempt", "True adds this exemption; false removes it.")] bool exempt = true) =>
            ExecuteAsync(token => AutoMod.SetExemptionAsync(
                Context.Guild, Context.User.Id, filter, channel.Id, false, exempt, token));
    }
}
