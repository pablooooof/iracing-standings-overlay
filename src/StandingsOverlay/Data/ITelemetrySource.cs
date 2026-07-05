namespace StandingsOverlay.Data;

public interface ITelemetrySource : IDisposable
{
    /// <summary>Raised from a background thread whenever a new snapshot is available.</summary>
    event Action<StandingsSnapshot>? SnapshotReady;

    void Start();
}
