using System.IO;
using System.Text.Json;

namespace StandingsOverlay.Data;

// DTOs for restart survival. Positional records serialize cleanly with System.Text.Json.
public sealed record GapSampleDto(int Lap, float Rel);

public sealed class CarStintDto
{
    public int LastLapSeen { get; set; } = -1;
    public float LastLapValue { get; set; } = -999f;
    public List<float> LapTimes { get; set; } = [];
    public List<bool> PitLaps { get; set; } = [];
    public List<int> StintLengths { get; set; } = [];
    public int CurrentStintStartLap { get; set; }
    public int PitCount { get; set; }
    public int FirstSeenLap { get; set; } = int.MinValue;
    public int GridPos { get; set; }
    public int TireStartLap { get; set; }
    public int TireStints { get; set; } = 1;
    public double MinFillSecPerLap { get; set; } = double.MaxValue;
    public PitInfo? LastPit { get; set; }
}

public sealed class SessionStateDto
{
    public string SubSessionId { get; set; } = "";
    public int SessionNum { get; set; } = -1;
    public double SessionTime { get; set; } = -1;
    public int LastPlayerLap { get; set; } = -1;
    public Dictionary<int, List<GapSampleDto>> Gaps { get; set; } = [];
    public Dictionary<int, CarStintDto> Cars { get; set; } = [];
}

/// <summary>
/// Survives an overlay restart mid-race: the durable parts of GapHistory and StintTracker
/// (delta history, lap times, stint boundaries, pit stats, tire-age inference) are snapshotted
/// to a JSON file next to the exe every ~30 s and restored on startup when the same
/// SubSession + session number is still running. Time-anchored transients (movement timers,
/// pending crossings, surface latches) are deliberately NOT restored — they re-latch within a
/// tick and restoring them stale would misfire the tow/spin heuristics.
/// </summary>
public static class SessionStateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { IncludeFields = true };

    public static void Save(string path, string subSessionId, int sessionNum, double sessionTime,
        GapHistory history, StintTracker stints)
    {
        try
        {
            var dto = new SessionStateDto
            {
                SubSessionId = subSessionId,
                SessionNum = sessionNum,
                SessionTime = sessionTime,
                LastPlayerLap = history.ExportLastPlayerLap(),
                Gaps = history.Export(),
                Cars = stints.Export(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Error("state-save", ex);
        }
    }

    /// <summary>Restores state if the file belongs to this exact session run (same subsession
    /// and session number, saved earlier on the same session clock). Returns true on restore.</summary>
    public static bool TryRestore(string path, string subSessionId, int sessionNum,
        double sessionTime, GapHistory history, StintTracker stints)
    {
        try
        {
            if (!File.Exists(path) || subSessionId.Length == 0) return false;
            var dto = JsonSerializer.Deserialize<SessionStateDto>(File.ReadAllText(path), JsonOpts);
            if (dto is null || dto.SubSessionId != subSessionId || dto.SessionNum != sessionNum)
                return false;
            // Same session clock still counting up — a restarted race starts a new SessionNum,
            // but guard against clock weirdness anyway.
            if (dto.SessionTime <= 0 || dto.SessionTime > sessionTime + 5) return false;

            history.Import(dto.LastPlayerLap, dto.Gaps);
            stints.Import(dto.Cars);
            Log.Write($"state: restored {dto.Cars.Count} cars, {dto.Gaps.Count} gap histories " +
                      $"(saved {sessionTime - dto.SessionTime:0}s ago)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("state-restore", ex);
            return false;
        }
    }
}
