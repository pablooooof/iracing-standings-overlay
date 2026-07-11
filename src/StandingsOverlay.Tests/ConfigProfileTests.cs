using System.IO;
using StandingsOverlay.Config;
using Xunit;

namespace StandingsOverlay.Tests;

public class ConfigProfileTests
{
    [Fact]
    public void SpectateProfile_ClonesOnFirstUse_ThenKeepsItsOwnState()
    {
        var dir = Directory.CreateTempSubdirectory("overlay-cfg-test");
        try
        {
            var svc = new ConfigService(Path.Combine(dir.FullName, "config.json"));
            svc.Current.DriversAhead = 3;
            svc.Current.X = 7;
            svc.Save();

            int changes = 0;
            svc.Changed += _ => changes++;

            // First switch clones the driving profile and widens the standings view.
            svc.SetSpectating(true);
            Assert.True(svc.Spectating);
            Assert.Equal(1, changes);
            Assert.True(File.Exists(Path.Combine(dir.FullName, "config.spectate.json")));
            Assert.Equal(8, svc.Current.DriversAhead);
            Assert.Equal(7, svc.Current.X);          // cloned: nothing jumps

            // Edits while spectating land in the spectate profile only.
            svc.Current.X = 500;
            svc.Save();

            svc.SetSpectating(false);
            Assert.Equal(3, svc.Current.DriversAhead);
            Assert.Equal(7, svc.Current.X);          // driving profile untouched

            svc.SetSpectating(true);
            Assert.Equal(500, svc.Current.X);        // spectate edit survived the round trip
            Assert.Equal(3, changes);

            svc.Dispose();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
