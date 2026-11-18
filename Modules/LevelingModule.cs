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
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class LevelingModule : DeferredInteractionModuleBase
{
    private const int PageSize = 10;
    private readonly ILevelingStore _store;
    private readonly ILevelingCardService _cards;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<LevelingModule> _logger;

    public LevelingModule(
        ILevelingStore store,
        ILevelingCardService cards,
        IHostApplicationLifetime lifetime,
        ILogger<LevelingModule> logger)
    {
        _store = store;
        _cards = cards;
        _lifetime = lifetime;
        _logger = logger;
    }

    [SlashCommand("rank", "Show your server level and XP, or another member's rank.")]
    public Task RankAsync(
        [Summary("user", "The member to view. Defaults to you.")] SocketGuildUser? user = null) =>
        ShowCardAsync(user, profile: false);

    [SlashCommand("profile", "Show a member's server level, awarded messages, and join date.")]
    public Task ProfileAsync(
        [Summary("user", "The member to view. Defaults to you.")] SocketGuildUser? user = null) =>
        ShowCardAsync(user, profile: true);

    [SlashCommand("leaderboard", "Show this server's XP leaderboard, ten members per page.")]
    public Task LeaderboardAsync(
        [Summary("page", "The page to view, from 1 to 1000.")]
        [MinValue(1), MaxValue(1000)] int page = 1) =>
        ExecuteAsync("leaderboard", async cancellationToken =>
        {
            if (page is < 1 or > 1000)
            {
                await ReplyAsync("Choose a leaderboard page from 1 to 1000.");
                return;
            }

            if (!await IsEnabledAsync(cancellationToken))
            {
                return;
            }

            var leaderboard = await _store.GetLeaderboardAsync(Context.Guild.Id, page, cancellationToken);
            var totalPages = Math.Max(1L,
                (leaderboard.TotalMembers / PageSize) + (leaderboard.TotalMembers % PageSize == 0 ? 0 : 1));
            var description = page > totalPages
                ? $"That page is empty. Choose a page from 1 to {LevelingDisplay.Number(Math.Min(1000, totalPages))}."
                : leaderboard.Entries.Count == 0
                    ? "No members have earned XP yet. Eligible messages earn XP while leveling is enabled."
                    : string.Join("\n\n", leaderboard.Entries.Take(PageSize).Select(entry =>
                    {
                        var member = Context.Guild.GetUser(entry.Member.UserId);
                        var name = LevelingDisplay.EmbedLabel(member?.DisplayName ?? $"Member {entry.Member.UserId}", 60);
                        return $"**#{LevelingDisplay.Number(entry.Position)} · {name}**\n" +
                               $"Level {LevelingDisplay.Number(LevelingProgress.GetLevel(entry.Member.TotalXp))} · " +
                               $"{LevelingDisplay.Number(entry.Member.TotalXp)} XP";
                    }));

            var embed = new EmbedBuilder()
                .WithTitle($"{LevelingDisplay.EmbedLabel(Context.Guild.Name)} · XP leaderboard")
                .WithColor(new Color(147, 197, 253))
                .WithDescription(description)
                .WithFooter($"Page {LevelingDisplay.Number(page)} of {LevelingDisplay.Number(totalPages)} · " +
                            $"{LevelingDisplay.Number(leaderboard.TotalMembers)} ranked members")
                .Build();
            await ReplyAsync(embed: embed);
        });

    private Task ShowCardAsync(SocketGuildUser? user, bool profile) =>
        ExecuteAsync(profile ? "profile" : "rank", async cancellationToken =>
        {
            var member = user ?? Context.User as SocketGuildUser;
            if (member is null || member.Guild.Id != Context.Guild.Id)
            {
                await ReplyAsync("Choose a member of this server to view their level.");
                return;
            }

            if (!await IsEnabledAsync(cancellationToken))
            {
                return;
            }

            var rank = await _store.GetRankAsync(Context.Guild.Id, member.Id, cancellationToken);
            var fallback = BuildFallbackEmbed(member, rank, profile);
            try
            {
                await using var stream = await _cards.RenderAsync(
                    member, Context.Guild.Name, rank, profile, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if ((ulong)stream.Length > Context.Interaction.AttachmentSizeLimit)
                {
                    throw new InvalidDataException("The leveling card exceeds the interaction attachment limit.");
                }

                stream.Position = 0;
                using var attachment = new FileAttachment(stream, profile ? "profile.png" : "rank.png");
                await ModifyOriginalResponseAsync(properties =>
                {
                    properties.Content = string.Empty;
                    properties.Embed = null;
                    properties.Attachments = new[] { attachment };
                    properties.AllowedMentions = AllowedMentions.None;
                }, new RequestOptions { CancelToken = cancellationToken });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "Could not render or upload a leveling card for member {MemberId} in guild {GuildId}; using an embed.",
                    member.Id, Context.Guild.Id);
                await SendFallbackAsync(fallback, cancellationToken);
            }
        });

    private async Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await _store.GetSettingsAsync(Context.Guild.Id, cancellationToken);
        if (settings.Enabled)
        {
            return true;
        }

        await ReplyAsync("Leveling is disabled in this server. Ask a server administrator to enable it with " +
                         "/settings leveling enable. XP is earned only while leveling is enabled.");
        return false;
    }

    private Task SendFallbackAsync(Embed embed, CancellationToken cancellationToken) =>
        ModifyOriginalResponseAsync(properties =>
        {
            properties.Content = "The image card is unavailable. Here are the same stats as text.";
            properties.Embed = embed;
            properties.Attachments = Array.Empty<FileAttachment>();
            properties.AllowedMentions = AllowedMentions.None;
        }, new RequestOptions { CancelToken = cancellationToken });

    private Embed BuildFallbackEmbed(SocketGuildUser member, LevelingRank? rank, bool profile)
    {
        var totalXp = rank?.Member.TotalXp ?? 0;
        var level = LevelingProgress.GetLevel(totalXp);
        var embed = new EmbedBuilder()
            .WithTitle($"{LevelingDisplay.EmbedLabel(member.DisplayName)} · {(profile ? "Profile" : "Rank")}")
            .WithDescription(LevelingDisplay.EmbedLabel(Context.Guild.Name))
            .WithColor(new Color(147, 197, 253))
            .AddField("Level", LevelingDisplay.Number(level), inline: true)
            .AddField("Total XP", LevelingDisplay.Number(totalXp), inline: true)
            .AddField("Server rank", rank is null ? "Unranked" : $"#{LevelingDisplay.Number(rank.Position)}", inline: true)
            .AddField("Progress to next level",
                $"{LevelingDisplay.Number(LevelingProgress.GetXpIntoLevel(totalXp))} / " +
                $"{LevelingDisplay.Number(LevelingDisplay.XpForNextLevel(totalXp))} XP");

        if (profile)
        {
            embed.AddField("Messages awarded XP", LevelingDisplay.Number(rank?.Member.MessageCount ?? 0), inline: true);
            if (member.JoinedAt is { } joinedAt)
            {
                embed.AddField("Joined this server", $"<t:{joinedAt.ToUnixTimeSeconds()}:D>", inline: true);
            }

            embed.WithFooter("Only messages awarded XP are counted; this is not a total message count.");
        }

        return embed.Build();
    }

    private async Task ExecuteAsync(string command, Func<CancellationToken, Task> operation)
    {
        await DeferAsync(ephemeral: false);
        try
        {
            await operation(_lifetime.ApplicationStopping);
        }
        catch (OperationCanceledException) when (_lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await ReplyAsync("The bot is shutting down. Please try this command again after it restarts.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception,
                "Could not complete leveling command {Command} by {UserId} in guild {GuildId}.",
                command, Context.User.Id, Context.Guild.Id);
            await ReplyAsync("I could not load the leveling stats. Please try again shortly. " +
                             "If this continues, ask a server administrator to check the bot logs.");
        }
    }
}
