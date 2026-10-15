using System.IO;
using System.Windows.Media;

namespace SyncCue.Services;

/// <summary>
/// Custom cue waveforms (aligned with Electron cue-samples):
/// tick = short double beep; hit = ascending three-note chime.
/// VolumePercent (0-100) maps to MediaPlayer.Volume.
/// </summary>
public sealed class CueSound
{
    private string? _roundId;
    private readonly HashSet<int> _fired = new();
    private readonly MediaPlayer _player = new();
    private string? _tickPath;
    private string? _hitPath;

    public int VolumePercent { get; set; } = 60;

    public void Reset()
    {
        _roundId = null;
        _fired.Clear();
    }

    public void Update(string? roundId, double? remainingMs)
    {
        if (VolumePercent <= 0)
        {
            Reset();
            return;
        }

        if (roundId != _roundId)
        {
            _roundId = roundId;
            _fired.Clear();
        }

        if (roundId is null || remainingMs is null)
            return;

        var rem = remainingMs.Value;
        for (var seconds = 5; seconds >= 0; seconds--)
        {
            if (_fired.Contains(seconds)) continue;
            var boundary = seconds * 1000.0;
            if (rem <= boundary && rem > boundary - 120)
            {
                _fired.Add(seconds);
                Play(seconds == 0);
            }
        }
    }

    private void EnsureWavs()
    {
        if (_tickPath is not null && _hitPath is not null) return;
        var dir = Path.Combine(Path.GetTempPath(), "SyncCueSounds");
        Directory.CreateDirectory(dir);
        _tickPath = Path.Combine(dir, "tick-custom.wav");
        _hitPath = Path.Combine(dir, "hit-custom.wav");
        WriteCueWav(_tickPath, hit: false);
        WriteCueWav(_hitPath, hit: true);
    }

    private void Play(bool hit)
    {
        try
        {
            EnsureWavs();
            var path = hit ? _hitPath! : _tickPath!;
            _player.Stop();
            _player.Volume = Math.Clamp(VolumePercent / 100.0, 0.0, 1.0);
            _player.Open(new Uri(path, UriKind.Absolute));
            _player.Play();
        }
        catch
        {
            // ignore audio failures
        }
    }

    /// <summary>Synthesize Electron-style cue PCM into a mono 16-bit WAV.</summary>
    private static void WriteCueWav(string path, bool hit)
    {
        const int sampleRate = 22050;
        var duration = hit ? 0.46 : 0.15;
        var sampleCount = (int)Math.Ceiling(sampleRate * duration);
        // notes: startSec, freqHz, lengthSec
        var notes = hit
            ? new[] { (0.0, 880.0, 0.32), (0.055, 1320.0, 0.28), (0.11, 1760.0, 0.24) }
            : new[] { (0.0, 880.0, 0.055), (0.08, 1100.0, 0.055) };

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            var dataBytes = sampleCount * 2;
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataBytes);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(dataBytes);

            for (var i = 0; i < sampleCount; i++)
            {
                var t = i / (double)sampleRate;
                var value = 0.0;
                foreach (var (start, frequency, length) in notes)
                {
                    var elapsed = t - start;
                    if (elapsed < 0 || elapsed >= length) continue;
                    var attack = Math.Min(1.0, elapsed / 0.004);
                    var release = Math.Min(1.0, (length - elapsed) / 0.012);
                    value += 0.23 * attack * release * Math.Exp(-elapsed * (hit ? 8 : 5))
                             * Math.Sin(2 * Math.PI * frequency * elapsed);
                }
                var s16 = (short)Math.Clamp((int)(value * short.MaxValue), short.MinValue, short.MaxValue);
                bw.Write(s16);
            }
            bw.Flush();
            File.WriteAllBytes(path, ms.ToArray());
        }
    }
}
