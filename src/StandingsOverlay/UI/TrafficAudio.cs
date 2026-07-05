using System.IO;
using System.Media;
using StandingsOverlay.Config;
using StandingsOverlay.Data;

namespace StandingsOverlay.UI;

/// <summary>
/// Plays the traffic alerter's audio cues. The three cues are tiny WAVs synthesized once
/// (and again if the volume changes) and played via System.Media.SoundPlayer — base library,
/// no packages. Arbitration (cooldowns, once-per-car) already happened in TrafficDetector;
/// this class only applies the per-cue config toggles and picks the most urgent cue when
/// several fire on the same tick (SoundPlayer can only play one sound at a time anyway).
///
/// Cue design (docs/TRAFFIC-ALERTER.md): watch = two soft rising chirps; imminent = urgent
/// triple beep; blue = calm descending two-tone that never escalates.
/// </summary>
public sealed class TrafficAudio : IDisposable
{
    private const int SampleRate = 22050;

    private SoundPlayer? _watch, _imminent, _blue;
    private int _builtVolume = -1;

    public void Handle(TrafficCues cues, TrafficAudioConfig cfg)
    {
        if (!cfg.Enabled || cues == TrafficCues.None || cfg.Volume <= 0) return;
        EnsureBuilt(cfg.Volume);

        if (cues.HasFlag(TrafficCues.Imminent) && cfg.ImminentCue) _imminent?.Play();
        else if (cues.HasFlag(TrafficCues.Watch) && cfg.WatchCue) _watch?.Play();
        else if (cues.HasFlag(TrafficCues.Blue) && cfg.BlueCue) _blue?.Play();
    }

    private void EnsureBuilt(int volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        if (volume == _builtVolume) return;
        _builtVolume = volume;
        DisposePlayers();

        double amp = volume / 100.0 * 0.55; // headroom so square waves don't clip harshly

        _watch = Player(Synth(amp,
            (Freq: 780, Start: 0.00, Dur: 0.14, Square: false),
            (Freq: 1170, Start: 0.17, Dur: 0.18, Square: false)));
        _imminent = Player(Synth(amp * 0.55, // squares are loud; pull them back
            (Freq: 1560, Start: 0.00, Dur: 0.10, Square: true),
            (Freq: 1560, Start: 0.14, Dur: 0.10, Square: true),
            (Freq: 1560, Start: 0.28, Dur: 0.10, Square: true)));
        _blue = Player(Synth(amp,
            (Freq: 660, Start: 0.00, Dur: 0.22, Square: false),
            (Freq: 495, Start: 0.24, Dur: 0.30, Square: false)));
    }

    private static SoundPlayer Player(byte[] wav)
    {
        var p = new SoundPlayer(new MemoryStream(wav));
        p.Load();
        return p;
    }

    /// <summary>16-bit mono PCM WAV: a handful of enveloped tones (15 ms attack, exponential decay).</summary>
    private static byte[] Synth(double amp, params (double Freq, double Start, double Dur, bool Square)[] notes)
    {
        double total = notes.Max(n => n.Start + n.Dur) + 0.05;
        int samples = (int)(total * SampleRate);
        var mix = new double[samples];

        foreach (var n in notes)
        {
            int from = (int)(n.Start * SampleRate);
            int len = (int)(n.Dur * SampleRate);
            for (int i = 0; i < len && from + i < samples; i++)
            {
                double tSec = (double)i / SampleRate;
                double env = Math.Min(tSec / 0.015, 1.0) * Math.Exp(-3.0 * tSec / n.Dur);
                double s = Math.Sin(2 * Math.PI * n.Freq * tSec);
                if (n.Square) s = Math.Sign(s);
                mix[from + i] += s * env;
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = samples * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in mix)
            w.Write((short)(Math.Clamp(s * amp, -1.0, 1.0) * short.MaxValue));
        return ms.ToArray();
    }

    private void DisposePlayers()
    {
        _watch?.Dispose(); _imminent?.Dispose(); _blue?.Dispose();
        _watch = _imminent = _blue = null;
    }

    public void Dispose() => DisposePlayers();
}
