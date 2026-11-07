using Discord.Interactions;

namespace Arisu.Modules;

public sealed class PingModule : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("ping", "Check whether the bot is online.")]
    public Task PingAsync() => RespondAsync("Pong!");
}
