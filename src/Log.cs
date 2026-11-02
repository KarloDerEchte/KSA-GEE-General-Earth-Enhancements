using Brutal.Logging;

namespace GEE;

/// <summary>Writes to the game's log file (Documents/My Games/Kitten Space Agency/Logs).</summary>
internal static class Log
{
    public static void Info(string message) => DefaultCategory.Log.Info($"[GEE] {message}");

    public static void Error(string message) => DefaultCategory.Log.Error($"[GEE] {message}");
}
