using System.Diagnostics;
using PPlusTracklistEditor.Core.Audio;
using Xunit.Abstractions;

namespace PPlusTracklistEditor.Core.Tests;

public class LoopFinderTests(ITestOutputHelper output)
{
    /// <summary>A made-up melody: random notes from a scale with a few harmonics and a plucked envelope, plus a bass note.</summary>
    private static float[] Melody(int rate, double seconds, int seed)
    {
        var rng = new Random(seed);
        int[] scale = [0, 2, 4, 5, 7, 9, 11];
        var x = new float[(int)(rate * seconds)];
        int i = 0;
        while (i < x.Length)
        {
            int len = (int)(rate * (0.2 + 0.1 * rng.Next(4)));
            double hz = 220 * Math.Pow(2, (scale[rng.Next(7)] + 12 * rng.Next(2)) / 12.0);
            double bass = 55 * Math.Pow(2, scale[rng.Next(7)] / 12.0);
            for (int k = 0; k < len && i < x.Length; k++, i++)
            {
                double t = (double)k / rate, env = Math.Exp(-t * 4);
                x[i] = (float)(env * (0.4 * Math.Sin(2 * Math.PI * hz * t) + 0.15 * Math.Sin(4 * Math.PI * hz * t) + 0.08 * Math.Sin(6 * Math.PI * hz * t))
                               + 0.15 * Math.Sin(2 * Math.PI * bass * t));
            }
        }
        return x;
    }

    private static PcmAudio Song(int rate, params float[][] parts)
    {
        var all = parts.SelectMany(p => p).Select(v => (short)Math.Clamp(v * 32767, short.MinValue, short.MaxValue)).ToArray();
        return new PcmAudio([all, (short[])all.Clone()], rate);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(32000)]
    public void FindsWhereTheLoopComesBackAround(int rate)
    {
        // intro (5 s) + loop (20 s) + loop again + a bit of a third time
        var intro = Melody(rate, 5, seed: 1);
        var body = Melody(rate, 20, seed: 2);
        var audio = Song(rate, intro, body, body, body[..(rate * 4)]);
        int start = intro.Length;

        var sw = Stopwatch.StartNew();
        var suggestions = LoopFinder.SuggestEnds(audio, start);
        output.WriteLine($"{sw.ElapsedMilliseconds} ms; " + string.Join(", ", suggestions.Select(s => $"{s.End} ({s.Match:P0})")));

        var best = suggestions[0];
        Assert.InRange(best.End, start + body.Length - 2, start + body.Length + 2); // to the sample (±2)
        Assert.True(best.Match >= LoopFinder.GoodMatch);
    }

    [Fact]
    public void StartingMidLoop_StillFindsTheSamePointOneLoopLater()
    {
        int rate = 32000;
        var body = Melody(rate, 16, seed: 7);
        var audio = Song(rate, Melody(rate, 3, seed: 8), body, body);
        int start = 3 * rate + 5 * rate; // 5 s into the loop

        var best = LoopFinder.SuggestEnds(audio, start)[0];

        Assert.InRange(best.End, start + body.Length - 2, start + body.Length + 2);
    }

    [Fact]
    public void MusicThatNeverRepeats_HasNoGoodMatch()
    {
        int rate = 32000;
        var audio = Song(rate, Melody(rate, 60, seed: 3));
        var suggestions = LoopFinder.SuggestEnds(audio, 0);
        output.WriteLine(string.Join(", ", suggestions.Select(s => $"{s.End} ({s.Match:P0})")));
        Assert.True(suggestions.Count == 0 || suggestions[0].Match < LoopFinder.GoodMatch);
    }

    [Fact]
    public void NoRoomToRepeat_GivesNothing()
    {
        var audio = Song(32000, Melody(32000, 6, seed: 4));
        Assert.Empty(LoopFinder.SuggestEnds(audio, 0));
        Assert.Empty(LoopFinder.SuggestEnds(audio, audio.Length - 10));
    }

    /// <summary>
    /// Real music: stock P+ songs are stored looped once (file ends at the loop end). Playing the loop a second time onto
    /// the end gives a "downloaded song" with a known right answer: the original loop end.
    /// </summary>
    [Fact]
    public void RealSongs_PlayedTwice_SuggestTheirRealLoopEnd()
    {
        string strm = Path.Combine(RealData.BuildSound, "strm");
        if (!Directory.Exists(strm)) { output.WriteLine("Skipped: no P+ build here"); return; }
        var files = Directory.EnumerateFiles(strm, "*.brstm", SearchOption.AllDirectories).Order().Where((_, i) => i % 15 == 0).Take(20).ToList();
        int right = 0, tried = 0;
        foreach (var file in files)
        {
            var (audio, loop) = BrstmFile.Decode(file);
            if (loop is not { } l || l.Length < audio.SampleRate * 12) continue; // needs a real loop to find
            tried++;
            int tail = Math.Min(l.Length, audio.SampleRate * 20);
            var twice = new PcmAudio(audio.Channels.Select(c => c[..l.End].Concat(c[l.Start..(l.Start + tail)]).ToArray()).ToArray(), audio.SampleRate);
            var best = LoopFinder.SuggestEnds(twice, l.Start).FirstOrDefault();
            bool ok = best is not null && Math.Abs(best.End - l.End) <= audio.SampleRate / 100; // within 10 ms
            if (ok) right++;
            output.WriteLine($"{(ok ? "ok  " : "MISS")} {Path.GetFileNameWithoutExtension(file)}: real {l.End}, suggested {best?.End} ({best?.Match:P0})");
        }
        output.WriteLine($"{right}/{tried} right");
        Assert.True(tried == 0 || right >= tried * 0.9, $"only {right}/{tried} right");
    }
}
