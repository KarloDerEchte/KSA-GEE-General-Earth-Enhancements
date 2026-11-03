namespace GEE;

/// <summary>
/// Pauses the downloads from one server once it answers HTTP 403 (or 401): it refuses access, so asking again for
/// every tile only repeats the refusal and fills the log. The refusal is logged once, cached data keeps loading, and
/// downloads try again after a pause that doubles with each refusal in a row (5 min up to 1 h).
/// </summary>
internal sealed class DownloadGate
{
    private static readonly TimeSpan FirstPause = TimeSpan.FromMinutes(5), LongestPause = TimeSpan.FromHours(1);

    private readonly string _name;
    private readonly object _lock = new();
    private DateTime _closedUntil;
    private TimeSpan _pause;

    public DownloadGate(string name) => _name = name;

    public bool IsClosed => DateTime.UtcNow < _closedUntil;
    public DateTime ReopensAt => _closedUntil;

    /// <summary>True if a failed download was refused by the server.</summary>
    public static bool IsRefusal(Exception e) => e.Message.StartsWith("HTTP 403") || e.Message.StartsWith("HTTP 401");

    /// <summary>Throws DownloadPausedException while the gate is closed; call before downloading.</summary>
    public void Check()
    {
        DateTime until = _closedUntil;
        if (DateTime.UtcNow < until)
            throw new DownloadPausedException(_name, until);
    }

    /// <summary>A download went through: the next refusal starts with the shortest pause again.</summary>
    public void Succeeded()
    {
        if (_pause == TimeSpan.Zero)
            return;
        lock (_lock)
        {
            if (_pause != TimeSpan.Zero)
                Log.Notice($"{_name} accepts downloads again");
            _pause = TimeSpan.Zero;
        }
    }

    /// <summary>
    /// The server refused a download: closes the gate (logging it once, also when several workers are refused at
    /// the same moment) and returns the exception to throw in place of the refusal.
    /// </summary>
    public DownloadPausedException Refused(Exception e)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow >= _closedUntil)
            {
                _pause = _pause == TimeSpan.Zero ? FirstPause : TimeSpan.FromTicks(Math.Min(_pause.Ticks * 2, LongestPause.Ticks));
                _closedUntil = DateTime.UtcNow + _pause;
                Log.Error($"{_name} refused access ({e.Message}); its downloads pause for {_pause.TotalMinutes:0} min, cached data still loads");
            }
            return new DownloadPausedException(_name, _closedUntil);
        }
    }
}

/// <summary>A download skipped because its server refused access a short while ago (see DownloadGate).</summary>
internal sealed class DownloadPausedException : Exception
{
    public readonly DateTime Until;

    public DownloadPausedException(string name, DateTime until)
        : base($"{name} refused access (HTTP 403), downloads paused until {until.ToLocalTime():HH:mm}") => Until = until;
}
