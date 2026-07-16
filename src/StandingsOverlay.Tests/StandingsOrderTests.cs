using StandingsOverlay.Config;
using StandingsOverlay.Data;
using Xunit;

namespace StandingsOverlay.Tests;

public class StandingsOrderTests
{
    /// <summary>Regression for the 2026-07-16 live bug: iRacing's session-YAML results encode
    /// ClassPosition 0-BASED (class leader = 0). Sorting by it with a &gt;0 guard dumped every
    /// class leader onto the overall-position fallback — a Cup class leader with six GT3s
    /// ahead overall showed P7 in the standings while the relative (live 1-based
    /// CarIdxClassPosition) correctly said P1. Shape mirrors that session's real YAML.</summary>
    [Fact]
    public void MulticlassPractice_ClassLeaderIsP1_DespiteZeroBasedYamlClassPosition()
    {
        var rig = new Rig(9, "Practice", playerIdx: 6);
        for (int i = 0; i < 6; i++) rig.AddCar(i, classId: 1, classLap: 137f, className: "GT3");
        for (int i = 6; i < 9; i++) rig.AddCar(i, classId: 2, classLap: 139f, className: "Cup");

        // Results exactly as the sim writes them: overall Position 1-based, ClassPosition
        // 0-based. Six GT3s ahead of the player overall; the player LEADS the Cup class.
        for (int i = 0; i < 6; i++)
            rig.Roster.Results[i] = new SessionResult(136.9f + i * 0.2f, 137f, 9, Position: i + 1, ClassPosition: i);
        rig.Roster.Results[6] = new SessionResult(138.9f, 139f, 5, Position: 7, ClassPosition: 0);   // the player
        rig.Roster.Results[7] = new SessionResult(139.4f, 140f, 5, Position: 8, ClassPosition: 1);
        rig.Roster.Results[8] = new SessionResult(140.1f, 141f, 4, Position: 9, ClassPosition: 2);

        for (int i = 0; i < 9; i++)
        {
            rig.Place(i, 3 + i * 0.05);
            rig.Tick.BestLap[i] = rig.Roster.Results[i].BestLap;
        }

        var snap = SnapshotBuilder.Build(rig.Tick, rig.Roster, new GapHistory(), new StintTracker(),
                                         new WeatherTracker(), new DriverSwapTracker(), new OverlayConfig());

        var player = snap.Rows.Single(r => r.IsPlayer);
        Assert.Equal("1", player.PosText);

        // And the player's row must physically precede both slower Cup cars' rows
        // (Rig car numbers are idx+1: the player is #7, classmates #8 and #9).
        var normals = snap.Rows.Where(r => r.Kind == RowKind.Normal).ToList();
        int me = normals.FindIndex(r => r.IsPlayer);
        Assert.True(me >= 0);
        foreach (var mate in new[] { "8", "9" })
        {
            int other = normals.FindIndex(r => r.CarNumber == mate);
            Assert.True(other < 0 || me < other, $"player row sits below Cup car #{mate}");
        }
    }
}
