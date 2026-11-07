namespace Arisu.Configuration;

public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    public string Token { get; init; } = string.Empty;

    /// <summary>
    /// Registers commands in this guild during development. Leave unset to register globally.
    /// </summary>
    public ulong? DevelopmentGuildId { get; init; }
}
