using System.IO;
using StandingsOverlay.Config;
using Xunit;

namespace StandingsOverlay.Tests;

/// <summary>The spectate state is a sparse override layer over the in-car base: settings you don't
/// deliberately change while spectating inherit the in-car value live, and only genuine differences
/// are stored. These lock in that behavior (and the one-time migration off the old full-clone file).</summary>
public class ConfigProfileTests
{
    [Fact]
    public void Spectate_InheritsBase_UntilDeliberatelyOverridden()
    {
        var dir = Directory.CreateTempSubdirectory("overlay-cfg-test");
        try
        {
            var basePath = Path.Combine(dir.FullName, "config.json");
            var specPath = Path.Combine(dir.FullName, "config.spectate.json");

            var svc = new ConfigService(basePath);
            svc.Base.DriversAhead = 5;
            svc.Base.DriversBehind = 3;
            svc.Base.Position.Race.X = 7;
            svc.SaveProfileAndNotify(false);

            // Nothing jumps on the first switch: spectate is pure inheritance, zero overrides.
            svc.SetSpectating(true);
            Assert.True(svc.Spectating);
            Assert.Equal(5, svc.Current.DriversAhead);
            Assert.Equal(7, svc.Current.Position.Race.X);
            Assert.Equal(0, svc.SpectateOverrideCount);

            // Override exactly one setting while spectating.
            svc.SpectateEffective.DriversAhead = 8;
            svc.SaveProfileAndNotify(true);
            Assert.Equal(1, svc.SpectateOverrideCount);

            // The sparse file stores ONLY the overridden key.
            var json = File.ReadAllText(specPath);
            Assert.Contains("DriversAhead", json);
            Assert.DoesNotContain("DriversBehind", json);

            // Change the base of a NON-overridden key → the spectate value follows it.
            svc.Base.DriversBehind = 9;
            svc.SaveProfileAndNotify(false);
            Assert.Equal(9, svc.SpectateEffective.DriversBehind);
            Assert.Equal(8, svc.SpectateEffective.DriversAhead);   // the override still stands

            // Change the base of the OVERRIDDEN key → spectate keeps its override.
            svc.Base.DriversAhead = 4;
            svc.SaveProfileAndNotify(false);
            Assert.Equal(8, svc.SpectateEffective.DriversAhead);

            svc.Dispose();

            // Survives a reload: override persists, inheritance still live.
            var reloaded = new ConfigService(basePath);
            reloaded.SetSpectating(true);
            Assert.Equal(8, reloaded.Current.DriversAhead);   // overridden
            Assert.Equal(9, reloaded.Current.DriversBehind);  // inherited
            Assert.Equal(1, reloaded.SpectateOverrideCount);
            reloaded.Dispose();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Spectate_ResetToInherit_DropsTheOverride()
    {
        var dir = Directory.CreateTempSubdirectory("overlay-cfg-test");
        try
        {
            var basePath = Path.Combine(dir.FullName, "config.json");
            var svc = new ConfigService(basePath);
            svc.SetSpectating(true);
            svc.SpectateEffective.DriversAhead = 8;
            svc.SaveProfileAndNotify(true);
            Assert.Equal(1, svc.SpectateOverrideCount);

            // Setting the spectate value back to the base value drops it from the overrides.
            svc.SpectateEffective.DriversAhead = svc.Base.DriversAhead;
            svc.SaveProfileAndNotify(true);
            Assert.Equal(0, svc.SpectateOverrideCount);
            svc.Dispose();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FuelTarget_StaysGlobal_NotASpectateOverride()
    {
        var dir = Directory.CreateTempSubdirectory("overlay-cfg-test");
        try
        {
            var basePath = Path.Combine(dir.FullName, "config.json");
            var specPath = Path.Combine(dir.FullName, "config.spectate.json");
            var svc = new ConfigService(basePath);

            // Set the strategy target while spectating.
            svc.SetSpectating(true);
            svc.SpectateEffective.FuelTable.TargetLaps = 25;
            svc.SpectateEffective.FuelTable.TargetMode = "Laps";
            svc.SaveProfileAndNotify(true);

            // It lands in the base (shared), never as a spectate override.
            Assert.Equal(25, svc.Base.FuelTable.TargetLaps);
            if (File.Exists(specPath))
                Assert.DoesNotContain("TargetLaps", File.ReadAllText(specPath));

            // And the in-car state sees it too.
            svc.SetSpectating(false);
            Assert.Equal(25, svc.Current.FuelTable.TargetLaps);
            svc.Dispose();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LegacyConfig_MigratesToPerSessionTogglesAndPositions()
    {
        var dir = Directory.CreateTempSubdirectory("overlay-cfg-test");
        try
        {
            var path = Path.Combine(dir.FullName, "config.json");
            // A pre-per-session config: plain bool toggles, single X/Y, RacesOnly.
            File.WriteAllText(path, """
                {
                  "X": 40, "Y": 50,
                  "ShowSof": false,
                  "ShowWind": true,
                  "Relative": { "Enabled": true, "ShowLicense": true, "X": 900, "Y": 500 },
                  "Traffic": { "Enabled": true, "RacesOnly": true, "X": 10, "Y": 20 },
                  "LapLab": { "Enabled": false }
                }
                """);

            var c = OverlayConfig.Load(path);
            Assert.Equal(Sessions.None, c.ShowSof);
            Assert.Equal(Sessions.All, c.ShowWind);
            Assert.Equal(Sessions.All, c.Relative.ShowLicense);
            Assert.Equal(Sessions.Race, c.Traffic.Enabled);          // RacesOnly folded in
            Assert.Equal(Sessions.None, c.LapLab.Enabled);
            foreach (var kind in new[] { Data.SessionKind.Race, Data.SessionKind.Qualify, Data.SessionKind.Practice })
            {
                Assert.Equal(40, c.Position.For(kind).X);
                Assert.Equal(500, c.Relative.Position.For(kind).Y);
                Assert.Equal(10, c.Traffic.Position.For(kind).X);
            }

            // Saved back in the new shape: no legacy keys, readable session flags.
            c.Save(path);
            var json = File.ReadAllText(path);
            var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            Assert.Null(root["X"]);
            Assert.Null(root["Relative"]!["X"]);
            Assert.DoesNotContain("RacesOnly", json);
            Assert.Contains("\"ShowSof\": \"None\"", json);
            Assert.Contains("\"Enabled\": \"Race\"", json);

            // Positions are then independent per session type.
            var reloaded = OverlayConfig.Load(path);
            reloaded.Relative.Position.Practice.X = 123;
            Assert.Equal(900, reloaded.Relative.Position.Race.X);
            Assert.True(reloaded.Relative.Enabled.In(Data.SessionKind.Qualify));
            Assert.False(reloaded.Traffic.Enabled.In(Data.SessionKind.Practice));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LegacySpectateOverride_MigratesThroughTheTypedModel()
    {
        var dir = Directory.CreateTempSubdirectory("overlay-cfg-test");
        try
        {
            var basePath = Path.Combine(dir.FullName, "config.json");
            var specPath = Path.Combine(dir.FullName, "config.spectate.json");
            new OverlayConfig().Save(basePath);
            // A sparse override written before per-session layouts/toggles existed.
            File.WriteAllText(specPath, """{ "Relative": { "X": 900, "ShowIRating": false } }""");

            var svc = new ConfigService(basePath);
            svc.SetSpectating(true);
            Assert.Equal(900, svc.Current.Relative.Position.Qualify.X);
            Assert.Equal(Sessions.None, svc.Current.Relative.ShowIRating);
            // Rewritten in the new shape: the legacy top-level X became per-session positions.
            var rel = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(specPath))!["Relative"]!;
            Assert.Null(rel["X"]);
            Assert.NotNull(rel["Position"]);
            svc.Dispose();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LegacyFullCloneSpectateFile_NormalizesToSparseOnLoad()
    {
        var dir = Directory.CreateTempSubdirectory("overlay-cfg-test");
        try
        {
            var basePath = Path.Combine(dir.FullName, "config.json");
            var specPath = Path.Combine(dir.FullName, "config.spectate.json");

            // Base defaults + a legacy spectate file that is a FULL copy differing in one key.
            new OverlayConfig().Save(basePath);
            var full = new OverlayConfig { DriversAhead = 8 };
            full.Save(specPath);

            var svc = new ConfigService(basePath);
            // Only the genuine difference survives as an override; the rest becomes inheritance.
            Assert.Equal(1, svc.SpectateOverrideCount);
            Assert.True(File.Exists(specPath + ".bak"));   // original preserved once

            svc.SetSpectating(true);
            Assert.Equal(8, svc.Current.DriversAhead);

            // Inheritance now works where it didn't before: a base change reaches spectate.
            svc.SetSpectating(false);
            svc.Base.DriversBehind = 11;
            svc.SaveProfileAndNotify(false);
            svc.SetSpectating(true);
            Assert.Equal(11, svc.Current.DriversBehind);
            svc.Dispose();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
