using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Arisu.Modules;

public abstract class DeferredInteractionModuleBase : InteractionModuleBase<SocketInteractionContext>
{
    protected async Task ReplyAsync(string? text = null, Embed? embed = null)
    {
        if (Context.Interaction.HasResponded)
        {
            await ModifyOriginalResponseAsync(properties =>
            {
                properties.Content = text;
                properties.Embed = embed;
                properties.AllowedMentions = AllowedMentions.None;
            });
            return;
        }

        await RespondAsync(
            text,
            embed: embed,
            ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }
}
