namespace Arisu.Utilities;

internal static class EndpointValidation
{
    public static bool IsSecureServiceEndpoint(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        (uri.Scheme == Uri.UriSchemeHttps ||
         (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    public static bool IsDiscordMediaUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.IdnHost.Equals("cdn.discordapp.com", StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.Equals("media.discordapp.net", StringComparison.OrdinalIgnoreCase));
}
