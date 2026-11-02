using Brutal.Logging;

namespace GEE;

/// <summary>
/// Writes to the game's log file (Documents/My Games/Kitten Space Agency/Logs). Info lines only with debug logging on
/// (Settings.DebugLogging); errors always.
/// </summary>
internal static class Log
{
    public static bool Debug;

    public static void Info(string message)
    {
        if (Debug)
            DefaultCategory.Log.Info($"[GEE] {message}");
    }

    public static void Error(string message) => DefaultCategory.Log.Error($"[GEE] {message}");
}
