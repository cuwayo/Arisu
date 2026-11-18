using Discord;
using Discord.Interactions;

namespace Arisu.Modules;

[RequireContext(ContextType.Guild)]
public sealed class HelpModule : DeferredInteractionModuleBase
{
    [SlashCommand("help", "Show commands and setup guidance.")]
    public async Task HelpAsync()
    {
        await DeferAsync(ephemeral: true);
        await ReplyAsync(embed: new EmbedBuilder()
            .WithTitle("Arisu commands")
            .WithColor(new Color(88, 101, 242))
            .WithDescription("Moderation commands require the corresponding Discord permissions. Your highest role and Arisu's role must be above the target member.")
            .AddField("Members", "`/kick` · `/ban` · `/unban` · `/timeout` · `/untimeout` · `/nick`\nTimeout durations: `30s`, `10m`, `2h`, `7d` (up to 28 days).")
            .AddField("Warnings and history", "`/warn` · `/warnings` · `/delwarn` · `/clearwarnings` · `/reason` · `/modlogs`")
            .AddField("Voice", "`/voice mute` · `/voice unmute` · `/voice deafen` · `/voice undeafen` · `/voice move` · `/voice disconnect`")
            .AddField("Temporary voice rooms", "`/voicemaster setup` · `/voicemaster disable` (Manage Server)\nJoin the configured lobby, then use `/voicemaster rename`, `limit`, `lock`, `unlock`, `hide`, `show`, `permit`, `reject`, `transfer`, or `claim` in your room.")
            .AddField("Channels", "`/purge` · `/slowmode` · `/lock` · `/unlock`\nPurge checks up to 100 recent messages. Unlock restores the permission saved by lock.")
            .AddField("AutoMod", "`/automod status` · `/automod spam` · `/automod mentions` · `/automod invites`\nUse `/automod words` for blocked words and `/automod exempt` for exemptions. Native Discord rules continue while Arisu is offline.")
            .AddField("Leveling", "`/rank [user]` · `/profile [user]` · `/leaderboard [page]`\nEarn message XP when enabled. Use `/settings leveling` to configure cooldowns, exclusions, and level-role rewards (Manage Server; adding rewards also needs Manage Roles).")
            .AddField("Setup", "`/settings logs` · `/settings member-logs` · `/settings welcome` · `/settings warnings`\nServer administrators and members with Manage Server can configure AutoMod.")
            .Build());
    }
}
