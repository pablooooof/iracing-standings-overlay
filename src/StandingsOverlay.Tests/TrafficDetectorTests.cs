using System.Globalization;
using StandingsOverlay.Data;
using Xunit;

namespace StandingsOverlay.Tests;

public class TrafficDetectorTests
{
    /// <summary>Advance every car at its own driving pace (seconds per lap, time domain) at
    /// 4 Hz and collect one detector snapshot per tick.</summary>
    private static List<TrafficSnapshot> Run(Rig r, TrafficDetector det, double[] progress,
        Func<double, double[]> paceAt, double seconds)
    {
        var snaps = new List<TrafficSnapshot>();
        for (double t = 0.25; t <= seconds; t += 0.25)
        {
            var pace = paceAt(t);
            for (int i = 0; i < progress.Length; i++)
            {
                progress[i] += 0.25 / pace[i];
                r.Place(i, progress[i]);
            }
            r.Tick.SessionTime = 100 + t;
            snaps.Add(det.Update(r.Tick, r.Roster, r.Cfg));
        }
        return snaps;
    }

    private static double ParseTta(string text) => double.Parse(text, CultureInfo.CurrentCulture);

    [Fact]
    public void FasterClassCarHoldingStation_NeverAlerts_EvenAcrossHairpins()
    {
        // Practice: a GTP hovers a constant 5 s behind the GT3 player (matching pace right now).
        // The distance-pct gap breathes with the track section every lap; the phase gap doesn't.
        // The old blend turned that breathing into 0.7+ s/s phantom closing rates → random pop-ups.
        var r = new Rig(2, sessionType: "Practice");
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 1, 100f, "GTP");
        var progress = new double[] { 10.0, 10.0 - 5.0 / 100 };
        r.Place(0, progress[0]);
        r.Place(1, progress[1]);

        var snaps = Run(r, new TrafficDetector(), progress, _ => [100.0, 100.0], seconds: 120);

        Assert.All(snaps, s => Assert.Empty(s.Rows));
    }

    [Fact]
    public void FasterClassSteadilyClosing_AlertsOnce_WithASaneCountdown()
    {
        // Race: GTP (100 s laps) closes on the GT3 player (120 s laps) at a steady ~0.17 s/s
        // from 8 s back. Expect exactly one alert episode, first countdown at ~the lead time,
        // and a clean disappearance after the pass.
        var r = new Rig(2, sessionType: "Race");
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 1, 100f, "GTP");
        var progress = new double[] { 20.0, 20.0 - 8.0 / 100 };
        r.Place(0, progress[0]);
        r.Place(1, progress[1]);

        var snaps = Run(r, new TrafficDetector(), progress, _ => [120.0, 100.0], seconds: 90);

        int onsets = 0;
        for (int i = 1; i < snaps.Count; i++)
            if (snaps[i - 1].Rows.Count == 0 && snaps[i].Rows.Count > 0) onsets++;
        Assert.Equal(1, onsets);

        var first = snaps.First(s => s.Rows.Count > 0).Rows[0];
        Assert.False(first.IsBlue);
        Assert.InRange(ParseTta(first.TtaText), 4, 12.6);
        Assert.Empty(snaps[^1].Rows);   // long gone after the pass
    }

    [Fact]
    public void PlayerAccelerating_HoldsTheCountdown_NeverShows99()
    {
        // Practice: a GTP charges (90 s laps vs the player's 120) until ~2.5 s behind, then the
        // player suddenly goes faster than the GTP. The measured rate collapses; the countdown
        // used to blow up to a literal "99.9" while the alert lingered. It must hold instead.
        var r = new Rig(2, sessionType: "Practice");
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 1, 100f, "GTP");
        var progress = new double[] { 30.0, 30.0 - 6.0 / 100 };
        r.Place(0, progress[0]);
        r.Place(1, progress[1]);

        var snaps = Run(r, new TrafficDetector(), progress,
            t => t < 13 ? [120.0, 90.0] : [80.0, 90.0], seconds: 30);

        Assert.Contains(snaps, s => s.Rows.Count > 0);   // it did alert while closing
        foreach (var row in snaps.SelectMany(s => s.Rows))
        {
            double tta = ParseTta(row.TtaText);
            Assert.True(tta < 20, $"countdown blew up to {row.TtaText}");
        }
    }

    [Fact]
    public void BlueFlagLeaderGrindingCloser_FiresOnGap_AndShowsTheGap()
    {
        // Race: the class leader is +1 lap and 2.2 s behind, closing at a grind (~1 s/lap).
        // A pure TTA trigger would only fire with them on the bumper (rate ≈ 0.01 s/s); blue
        // must fire on proximity and display the gap, not a meaningless countdown.
        var r = new Rig(2, sessionType: "Race");
        r.AddCar(0, 2, 120f);
        r.AddCar(1, 2, 120f);
        var progress = new double[] { 40.0, 41.0 - 2.2 / 120 };
        r.Place(0, progress[0]);
        r.Place(1, progress[1]);

        var snaps = Run(r, new TrafficDetector(), progress, _ => [120.0, 119.0], seconds: 20);

        var blue = snaps.SelectMany(s => s.Rows).Where(row => row.IsBlue).ToList();
        Assert.NotEmpty(blue);
        Assert.All(blue, row => Assert.InRange(ParseTta(row.TtaText), 1.4, 2.6));
    }
}
