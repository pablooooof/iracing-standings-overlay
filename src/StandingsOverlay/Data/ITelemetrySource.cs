namespace StandingsOverlay.Data;

public interface ITelemetrySource : IDisposable
{
    /// <summary>Raised from a background thread whenever a new snapshot is available.</summary>
    event Action<StandingsSnapshot>? SnapshotReady;

    /// <summary>Raised alongside SnapshotReady with the traffic alerter's view of the same tick.</summary>
    event Action<TrafficSnapshot>? TrafficReady;

    /// <summary>Raised alongside SnapshotReady with the relative box's view of the same tick.</summary>
    event Action<RelativeSnapshot>? RelativeReady;

    /// <summary>Raised alongside SnapshotReady with the fuel calculator's view of the same tick.</summary>
    event Action<FuelSnapshot>? FuelReady;

    void Start();
}
