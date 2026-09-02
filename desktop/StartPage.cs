namespace MishaWeb;

internal static class StartPage
{
    public const string HostName = "misha.test";
    public const string Url = "https://misha.test/newtab.html";

    public static bool IsStartPageUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/newtab.html", StringComparison.Ordinal);
    }

}
