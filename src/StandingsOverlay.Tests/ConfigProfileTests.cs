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
            svc.Base.X = 7;
            svc.SaveProfileAndNotify(false);

            // Nothing jumps on the first switch: spectate is pure inheritance, zero overrides.
            svc.SetSpectating(true);
            Assert.True(svc.Spectating);
            Assert.Equal(5, svc.Current.DriversAhead);
            Assert.Equal(7, svc.Current.X);
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
