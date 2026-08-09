using System.IO;
using System.Text;

namespace TwitchOverlayHelper.Diagnostics;

/// <summary>
/// The app's only log. Deliberately a plain text file rather than a logging framework: what it has
/// to answer is "why did it disappear while she was live", and that question is asked hours later by
/// someone reading the file in Notepad on another machine.
///
/// <para>Nothing here may throw. A logger that takes the app down while reporting that the app is
/// going down is worse than no logger at all, so every path swallows its own failures – a missing
/// line is a cost worth paying, a crash inside the crash handler is not.</para>
/// </summary>
public static class AppLog
{
    /// <summary>Days of logs to keep. Long enough to cover "it happened some evening last week".</summary>
    private const int KeepDays = 7;

    private static readonly Lock Gate = new();

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TwitchOverlayHelper", "logs");

    private static string TodayPath => Path.Combine(Folder, $"app-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>Something worth knowing happened – a connection, a channel, a decision.</summary>
    public static void Info(string message) => Write("INFO ", message);

    /// <summary>Something went wrong but the app carried on.</summary>
    public static void Warn(string message) => Write("WARN ", message);

    /// <summary>
    /// Something threw. <paramref name="context"/> says where we were, because a stack trace on its
    /// own rarely says what the app was in the middle of.
    /// </summary>
    public static void Error(string context, Exception? exception)
    {
        var text = new StringBuilder(context);
        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
            text.Append(Environment.NewLine).Append("    ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message)
                .Append(Environment.NewLine).Append(ex.StackTrace);
        Write("ERROR", text.ToString());
    }

    /// <summary>
    /// Opens the log folder in Explorer. Creates it first – a button that opens nothing because
    /// nothing has been logged yet looks broken.
    /// </summary>
    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Folder) { UseShellExecute = true });
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Marks the start of a run and clears out old files. The version and the process id are on the
    /// first line so two runs in the same file can be told apart – which is exactly the case when
    /// the app died and was started again, and the whole question is where the first run stopped.
    /// </summary>
    public static void StartSession()
    {
        Write("START", $"Twitch Overlay Helper {AppVersion.DisplayText} • pid {Environment.ProcessId} • {Environment.OSVersion}");
        PruneOldLogs();
    }

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(TodayPath, line, Encoding.UTF8);
        }
        catch (Exception) { }
    }

    private static void PruneOldLogs()
    {
        try
        {
            DateTime cutoff = DateTime.Now.Date.AddDays(-KeepDays);
            foreach (string file in Directory.GetFiles(Folder, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch (Exception) { }
    }
}
