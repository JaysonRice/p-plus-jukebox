using PPlusTracklistEditor.Core.Audio;
using PPlusTracklistEditor.Core.Library;

namespace PPlusTracklistEditor.Core.Tests;

public class LoopToolsTests
{
    private static PcmAudio Sine(int rate, double seconds, double hz = 100, int channels = 2)
    {
        int n = (int)(rate * seconds);
        var data = new short[channels][];
        for (int c = 0; c < channels; c++)
        {
            data[c] = new short[n];
            for (int i = 0; i < n; i++) data[c][i] = (short)(16000 * Math.Sin(2 * Math.PI * hz * i / rate));
        }
        return new PcmAudio(data, rate);
    }

    private static PcmAudio Noise(int rate, double seconds, int seed = 1)
    {
        int n = (int)(rate * seconds);
        var rng = new Random(seed);
        var data = new short[2][];
        for (int c = 0; c < 2; c++) { data[c] = new short[n]; for (int i = 0; i < n; i++) data[c][i] = (short)rng.Next(-20000, 20000); }
        return new PcmAudio(data, rate);
    }

    [Fact]
    public void Waveform_SummaryMatchesRawSamples_AtEveryZoom()
    {
        var audio = Noise(32000, 3);
        var wave = new WaveformData(audio);
        foreach (var (from, to, cols) in new[] { (0.0, (double)audio.Length, 800), (1234.0, 90000.0, 300), (5000.0, 5100.0, 50), (100.0, 110.0, 40) })
        {
            var min = new float[cols]; var max = new float[cols];
            wave.Peaks(from, to, min, max);
            double per = (to - from) / cols;
            for (int i = 0; i < cols; i++)
            {
                int a = (int)Math.Floor(from + i * per), b = Math.Max(a + 1, (int)Math.Floor(from + (i + 1) * per));
                short lo = 0, hi = 0;
                foreach (var ch in audio.Channels) for (int s = a; s < b; s++) { lo = Math.Min(lo, ch[s]); hi = Math.Max(hi, ch[s]); }
                Assert.Equal(lo / 32768f, min[i]);
                Assert.Equal(hi / 32768f, max[i]);
            }
        }
    }

    [Fact]
    public void Waveform_PastTheEndIsSilent()
    {
        var wave = new WaveformData(Noise(32000, 0.1));
        var min = new float[10]; var max = new float[10];
        wave.Peaks(wave.Length, wave.Length * 2, min, max);
        Assert.All(min, v => Assert.Equal(0, v));
        Assert.All(max, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Snap_FindsTheNearestRisingZeroCrossing()
    {
        var audio = Sine(32000, 1, hz: 100); // rising crossings every 320 samples: 0, 320, 640...
        Assert.Equal(640, LoopTools.SnapToZeroCrossing(audio, 600, LoopTools.SnapRadius(32000)));
        Assert.Equal(640, LoopTools.SnapToZeroCrossing(audio, 700, 640));
        Assert.Equal(700, LoopTools.SnapToZeroCrossing(audio, 700, 10));        // none close enough: unchanged
        Assert.Equal(0, LoopTools.SnapToZeroCrossing(audio, 3, 10));            // the very start counts
        Assert.Equal(audio.Length, LoopTools.SnapToZeroCrossing(audio, audio.Length, 10));
        Assert.True(audio.Channels[0][639] < 0 && audio.Channels[0][640] >= 0);
    }

    [Fact]
    public void Step_MovesToTheNextOrPreviousCrossing()
    {
        var audio = Sine(32000, 1, hz: 100);
        Assert.Equal(960, LoopTools.StepToZeroCrossing(audio, 640, +1));
        Assert.Equal(320, LoopTools.StepToZeroCrossing(audio, 640, -1));
        Assert.Equal(0, LoopTools.StepToZeroCrossing(audio, 100, -1));
        var silent = new PcmAudio([new short[64000]], 32000);                  // no crossings: one second at most
        Assert.Equal(42000, LoopTools.StepToZeroCrossing(silent, 10000, +1));
    }

    [Fact]
    public void TestStart_IsAFewSecondsBeforeTheLoopEnd()
    {
        Assert.Equal(32000 * 7, LoopTools.TestStart(new LoopPoints(32000, 32000 * 10), 32000));
        Assert.Equal(0, LoopTools.TestStart(new LoopPoints(0, 1000), 32000));
    }

    [Fact]
    public void DefaultLoop_KeepsABrstmsLoop_ElseLoopsTheWholeSong()
    {
        var audio = Sine(32000, 1);
        Assert.Equal(new LoopPoints(0, audio.Length), LoopTools.DefaultLoop(new ImportedAudio(audio, null, "a.wav")));
        Assert.Equal(new LoopPoints(5, 50), LoopTools.DefaultLoop(new ImportedAudio(audio, new LoopPoints(5, 50), "a.brstm")));
    }

    [Theory]
    [InlineData("1:23.456", 83.456)]
    [InlineData("0:05", 5)]
    [InlineData("83.5", 83.5)]
    [InlineData("83,5", 83.5)]
    [InlineData(" 2:00.000 ", 120)]
    public void TimeText_Parses(string text, double seconds)
    {
        Assert.True(TimeText.TryParse(text, out var t));
        Assert.Equal(seconds, t.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:2:3")]
    [InlineData("1:75")]
    [InlineData("-5")]
    public void TimeText_RejectsNonsense(string text) => Assert.False(TimeText.TryParse(text, out _));

    [Fact]
    public void TimeText_FormatsAndRoundTrips()
    {
        var t = TimeSpan.FromSeconds(83.456);
        Assert.Equal("1:23.456", TimeText.Format(t));
        Assert.True(TimeText.TryParse(TimeText.Format(t), out var back));
        Assert.Equal(t.TotalMilliseconds, back.TotalMilliseconds, 0);
    }

    [Theory]
    [InlineData(44100, 2, true, 2.0, 8.0)]
    [InlineData(48000, 1, true, 0.3, 4.0)]
    [InlineData(32000, 2, false, 0, 0)]
    [InlineData(22050, 2, true, 1.0, 5.0)]
    public void EstimateSize_IsCloseToTheRealFile(int rate, int channels, bool loops, double start, double end)
    {
        var src = Noise(rate, 10);
        var audio = channels == 2 ? src : new PcmAudio([src.Channels[0]], rate);
        LoopPoints? loop = loops ? new LoopPoints(audio.SampleAt(TimeSpan.FromSeconds(start)), audio.SampleAt(TimeSpan.FromSeconds(end))) : null;

        long estimate = BrstmFile.EstimateSize(audio, loop);
        long actual = BrstmFile.Encode(audio, loop).Bytes.Length;

        Assert.InRange(estimate, actual * 0.97, actual * 1.03);
        Assert.Equal(48000, BrstmFile.OutputRate(48000, new BrstmEncodeOptions(MaxSampleRate: null)));
        Assert.Equal(32000, BrstmFile.OutputRate(48000));
    }

    [Theory]
    [InlineData(@"C:\dl\02_green_hill_zone.mp3", "green hill zone")]
    [InlineData(@"C:\dl\07 - Gourmet Race.wav", "Gourmet Race")]
    [InlineData(@"C:\dl\Battle Theme.flac", "Battle Theme")]
    [InlineData(@"C:\dl\1999.mp3", "1999")]           // a title that's just a number stays
    public void TitleFromFileName_CleansUpDownloads(string path, string expected) =>
        Assert.Equal(expected, SongFileNames.TitleFromFileName(path));
}
