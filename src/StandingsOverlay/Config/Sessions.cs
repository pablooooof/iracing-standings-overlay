using System.Text.Json;
using System.Text.Json.Serialization;
using StandingsOverlay.Data;

namespace StandingsOverlay.Config;

/// <summary>Which session types (race / qualifying / practice) a setting is on in — the R/Q/P
/// checkboxes in settings. Stored as readable flags ("All", "Race, Practice", "None"); a legacy
/// plain <c>true</c>/<c>false</c> from an older config reads as All/None, so every former bool
/// toggle upgraded in place without a migration step.</summary>
[Flags, JsonConverter(typeof(SessionsJsonConverter))]
public enum Sessions
{
    None = 0,
    Race = 1,
    Qualify = 2,
    Practice = 4,
    All = Race | Qualify | Practice,
}

public static class SessionsExt
{
    public static Sessions Of(SessionKind kind) => kind switch
    {
        SessionKind.Race => Sessions.Race,
        SessionKind.Qualify => Sessions.Qualify,
        _ => Sessions.Practice,
    };

    /// <summary>Is this on in the given session type?</summary>
    public static bool In(this Sessions s, SessionKind kind) => (s & Of(kind)) != 0;
}

public sealed class SessionsJsonConverter : JsonConverter<Sessions>
{
    public override Sessions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => Sessions.All,
            JsonTokenType.False => Sessions.None,
            JsonTokenType.Number => (Sessions)reader.GetInt32() & Sessions.All,
            JsonTokenType.String => Enum.TryParse<Sessions>(reader.GetString(), ignoreCase: true, out var s)
                ? s & Sessions.All : Sessions.All,
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for Sessions"),
        };

    public override void Write(Utf8JsonWriter writer, Sessions value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>A widget's on-screen spot (DIPs).</summary>
public sealed class ScreenPos
{
    public double X { get; set; }
    public double Y { get; set; }

    public ScreenPos() { }
    public ScreenPos(double x, double y) { X = x; Y = y; }
}

/// <summary>A widget position per session type: dragging a widget in Move-overlays mode saves it
/// for the session type on screen (<see cref="ConfigService.Session"/>), so the race, qualifying
/// and practice layouts are independent of each other.</summary>
public sealed class SessionPositions
{
    public ScreenPos Race { get; set; } = new();
    public ScreenPos Qualify { get; set; } = new();
    public ScreenPos Practice { get; set; } = new();

    public ScreenPos For(SessionKind kind) => kind switch
    {
        SessionKind.Race => Race,
        SessionKind.Qualify => Qualify,
        _ => Practice,
    };

    public static SessionPositions All(double x, double y) =>
        new() { Race = new(x, y), Qualify = new(x, y), Practice = new(x, y) };

    /// <summary>Fold a pre-per-session single position (legacy top-level <c>X</c>/<c>Y</c> keys)
    /// into all three sessions. A sparse spectate override may carry only one of the two.</summary>
    public static SessionPositions FromLegacy(SessionPositions current, double? x, double? y) =>
        x is null && y is null ? current : All(x ?? current.Race.X, y ?? current.Race.Y);
}

/// <summary>What every standalone widget shares: which session types it shows in, and where it
/// sits per session type. Legacy single-position configs migrate on load.</summary>
public abstract class WidgetConfig : IJsonOnDeserialized
{
    // Show the widget in these session types (the "Visible" switch + R/Q/P boxes in settings).
    public Sessions Enabled { get; set; } = Sessions.All;

    public SessionPositions Position { get; set; }

    // Legacy single position (before per-session layouts). Read once to seed all three sessions,
    // then dropped — never written back.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? X { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Y { get; set; }

    protected WidgetConfig(double x, double y) => Position = SessionPositions.All(x, y);

    public virtual void OnDeserialized()
    {
        Position = SessionPositions.FromLegacy(Position, X, Y);
        X = Y = null;
    }
}
