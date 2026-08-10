using System.Reflection;

namespace TwitchOverlayHelper;

/// <summary>
/// Resolves the app version stamped by the release pipeline (year.month.sequence).
/// Local builds have no stamped version and show "dev" instead.
/// </summary>
internal static class AppVersion
{
    /// <summary>The stamped version, or the placeholder 1.0.0.0 that a local build carries.</summary>
    public static Version? Current { get; } = Assembly.GetExecutingAssembly().GetName().Version;

    public static string DisplayText { get; } = Compute();

    private static string Compute()
    {
        var version = Current;

        if (version is null || version.Major < 2000)
        {
            return "dev";
        }

        return $"v{version.Major}.{version.Minor}.{version.Build}";
    }
}
