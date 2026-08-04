using System.IO;
using System.Reflection;

namespace TwitchOverlayHelper.Web;

/// <summary>
/// Serves the dock's HTML/CSS/JS from embedded resources. Files next to the exe are not an option:
/// the app ships as a single self-contained file.
/// </summary>
internal static class StaticAssets
{
    private const string Prefix = "TwitchOverlayHelper.Web.wwwroot.";
    private static readonly Assembly Assembly = typeof(StaticAssets).Assembly;

    public static bool TryRead(string path, out byte[] content, out string contentType)
    {
        content = [];
        contentType = "application/octet-stream";

        string name = path.Trim('/');
        if (name.Length == 0) name = "index.html";
        // Resource names flatten directories to dots, so only flat, simple names can resolve.
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal)) return false;

        using Stream? stream = Assembly.GetManifestResourceStream(Prefix + name);
        if (stream is null) return false;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        content = buffer.ToArray();
        contentType = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
        return true;
    }
}
