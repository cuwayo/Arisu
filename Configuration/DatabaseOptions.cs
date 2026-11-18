using Npgsql;

namespace Arisu.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    private const string InvalidConnectionMessage =
        "Supply a valid PostgreSQL URL or Npgsql connection string with a host, database, username, " +
        "finite timeouts, and sensitive diagnostic logging disabled.";

    /// <summary>
    /// Gets the PostgreSQL URL or Npgsql connection string supplied through deployment secrets.
    /// </summary>
    public string ConnectionString { get; init; } = string.Empty;

    public static bool IsValidConnectionString(string? connectionString) =>
        TryNormalizeConnectionString(connectionString, out _);

    public string GetNpgsqlConnectionString()
    {
        if (!TryNormalizeConnectionString(ConnectionString, out var normalized))
        {
            // Parser exceptions can contain credentials. Do not propagate them as inner exceptions.
            throw new ArgumentException(InvalidConnectionMessage);
        }

        return normalized;
    }

    private static bool TryNormalizeConnectionString(string? connectionString, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            var value = connectionString.Trim();
            var options = value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
                          value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
                ? ParseUrl(value)
                : new NpgsqlConnectionStringBuilder(value);
            if (string.IsNullOrWhiteSpace(options.Host) ||
                string.IsNullOrWhiteSpace(options.Database) ||
                string.IsNullOrWhiteSpace(options.Username) ||
                options.Timeout <= 0 || options.CommandTimeout <= 0 ||
                options.LogParameters || options.IncludeErrorDetail)
            {
                return false;
            }

            normalized = options.ConnectionString;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static NpgsqlConnectionStringBuilder ParseUrl(string value)
    {
        ValidatePercentEncoding(value);
        var uri = new Uri(value, UriKind.Absolute);
        if (string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath.Length <= 1 || uri.AbsolutePath[1..].Contains('/'))
        {
            throw new FormatException();
        }

        var credentials = uri.UserInfo.Split(':', 2);
        var options = new NpgsqlConnectionStringBuilder
        {
            Host = uri.IdnHost.Trim('[', ']'),
            Port = uri.Port == -1 ? 5432 : uri.Port,
            Database = Decode(uri.AbsolutePath[1..]),
            Username = Decode(credentials[0])
        };
        if (credentials.Length == 2)
        {
            options.Password = Decode(credentials[1]);
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = parameter.Split('=', 2);
            var key = Decode(pair[0]).ToLowerInvariant();
            if (pair.Length != 2 || !keys.Add(key))
            {
                throw new FormatException();
            }

            // Explicit mappings preserve libpq options without silently ignoring security settings.
            var keyword = key switch
            {
                "sslmode" => "SSL Mode",
                "channel_binding" => "Channel Binding",
                "sslrootcert" => "Root Certificate",
                "connect_timeout" => "Timeout",
                "command_timeout" => "Command Timeout",
                "application_name" => "Application Name",
                "options" => "Options",
                _ => throw new FormatException()
            };
            var parameterValue = Decode(pair[1]);
            options[keyword] = key == "sslmode"
                ? parameterValue.Replace("-", string.Empty, StringComparison.Ordinal)
                : parameterValue;
        }

        return options;
    }

    private static string Decode(string value)
    {
        ValidatePercentEncoding(value);
        // URL credentials use percent encoding; a literal '+' remains a plus.
        return Uri.UnescapeDataString(value);
    }

    private static void ValidatePercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '%' &&
                (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2])))
            {
                throw new FormatException();
            }
        }
    }
}
