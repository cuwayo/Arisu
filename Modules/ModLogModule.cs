using Arisu.Storage;
using Discord;
using Discord.Interactions;

namespace Arisu.Modules;

[RequireContext(ContextType.Guild)]
public sealed class ModLogModule : DeferredInteractionModuleBase
{
    private readonly IModerationHistoryStore _history;

    public ModLogModule(IModerationHistoryStore history) => _history = history;

    [SlashCommand("modlogs", "View recent moderation cases for a user.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    public async Task ModLogsAsync(
        [Summary("user", "The user whose recent moderation cases to view.")] IUser user)
    {
        await DeferAsync(ephemeral: true);
        var cases = await _history.GetCasesAsync(Context.Guild.Id, user.Id);
        var description = cases.Count == 0
            ? "No moderation cases have been recorded for this user."
            : string.Join("\n\n", cases.Select(entry =>
                $"**#{entry.Id} · {entry.Action}** · <t:{entry.CreatedAt.ToUnixTimeSeconds()}:d>\n" +
                $"Moderator: <@{entry.ModeratorId}>\n{entry.Reason}"));
        if (description.Length > EmbedBuilder.MaxDescriptionLength)
        {
            description = description[..(EmbedBuilder.MaxDescriptionLength - 2)] + "…";
        }

        await ReplyAsync(embed: new EmbedBuilder()
            .WithTitle($"Moderation history · {user.Id}")
            .WithDescription(description)
            .WithColor(new Color(88, 101, 242))
            .WithFooter("Most recent 8 cases recorded by Arisu; older actions are not imported.")
            .Build());
    }
}
