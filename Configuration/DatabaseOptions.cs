namespace Arisu.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Gets the SQLite database path, relative to the content root unless absolute.
    /// </summary>
    public string Path { get; init; } = "data/arisu.db";
}
