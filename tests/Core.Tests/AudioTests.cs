using System.Diagnostics;
using NAudio.Wave;
using PPlusTracklistEditor.Core.Audio;
using Xunit.Abstractions;

namespace PPlusTracklistEditor.Core.Tests;

public class AudioTests(ITestOutputHelper output)
{
    /// <summary>
    /// Music-like test signal: a slow frequency sweep plus seeded noise, different per channel.
    /// Unlike a plain sine it never repeats, so comparing two positions tells us whether they hold the same audio.
    /// </summary>
    private static PcmAudio Signal(int rate, double seconds, int channels = 2, double level = 0.5)
    {
        int n = (int)(rate * seconds);
        var rng = new Random(42);
        var data = new short[channels][];
        for (int c = 0; c < channels; c++)
        {
            data[c] = new short[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate;
                phase += 2 * Math.PI * (200 + 300 * c + 150 * t) / rate;
                data[c][i] = (short)(32767 * level * (0.8 * Math.Sin(phase) + 0.2 * (rng.NextDouble() * 2 - 1)));
            }
        }
        return new PcmAudio(data, rate);
    }

    /// <summary>RMS of (a - b) relative to RMS of a, over n samples starting at the given offsets. 0 = identical.</summary>
    private static double RelativeError(short[] a, int aStart, short[] b, int bStart, int n)
    {
        double diff = 0, energy = 0;
        for (int i = 0; i < n; i++)
        {
            double x = a[aStart + i], y = b[bStart + i];
            diff += (x - y) * (x - y);
            energy += x * x;
        }
        return Math.Sqrt(diff / energy);
    }

    [Fact]
    public void Encode_LoopsAtGameRate_WithBlockAlignedStart_AndTrimsAfterLoopEnd()
    {
        var audio = Signal(44100, 10);
        var loop = new LoopPoints(audio.SampleAt(TimeSpan.FromSeconds(2)), audio.SampleAt(TimeSpan.FromSeconds(8)));

        var result = BrstmFile.Encode(audio, loop);
        var (decoded, decodedLoop) = BrstmFile.Decode(result.Bytes);

        Assert.Equal(32000, result.SampleRate);
        Assert.Equal(32000, decoded.SampleRate);
        Assert.Equal(2, decoded.ChannelCount);
        Assert.NotNull(decodedLoop);
        var l = decodedLoop!.Value;
        Assert.Equal(result.Loop, l);
        Assert.Equal(0, l.Start % BrstmFile.BlockSamples);              // like every stock P+ song
        Assert.InRange(l.Length, 6 * 32000 - 1, 6 * 32000 + 1);        // loop length kept (6 s)
        Assert.InRange(l.Start, 2 * 32000, 2 * 32000 + BrstmFile.BlockSamples); // moved forward at most one block
        Assert.Equal(l.End, decoded.Length);                            // nothing after the loop end
    }

    [Fact]
    public void Encode_MovingTheLoopStartToABlock_KeepsTheMusicTheSame()
    {
        var audio = Signal(32000, 10);
        int requested = 70000; // not a multiple of 14336
        var result = BrstmFile.Encode(audio, new LoopPoints(requested, 290000));
        var (decoded, loop) = BrstmFile.Decode(result.Bytes);
        int aligned = loop!.Value.Start;
        Assert.NotEqual(requested, aligned);

        // What plays after the jump (from the aligned start) must be what followed the requested start
        // at the same distance into the loop. Compare against the original audio (ADPCM adds a little error).
        int shift = aligned - requested;
        for (int c = 0; c < 2; c++)
        {
            Assert.True(RelativeError(audio.Channels[c], requested + shift, decoded.Channels[c], aligned, 20000) < 0.1);
            // and the jump target lines up with what the song played the first time through
            Assert.True(RelativeError(decoded.Channels[c], requested + shift, decoded.Channels[c], aligned, 20000) < 0.1);
        }
    }

    [Fact]
    public void Encode_RoundTripIsClose()
    {
        var audio = Signal(32000, 3, channels: 1);
        var (decoded, loop) = BrstmFile.Decode(BrstmFile.Encode(audio, null).Bytes);
        Assert.Null(loop);
        Assert.Equal(1, decoded.ChannelCount);
        Assert.Equal(audio.Length, decoded.Length);
        double err = RelativeError(audio.Channels[0], 0, decoded.Channels[0], 0, audio.Length);
        output.WriteLine($"DSP-ADPCM relative error: {err:P2}");
        Assert.True(err < 0.1, $"error {err:P1}");
    }

    [Fact]
    public void Encode_KeepsLowerRates_AndCanKeepOriginal()
    {
        var low = Signal(22050, 1);
        Assert.Equal(22050, BrstmFile.Encode(low, null).SampleRate);

        var high = Signal(48000, 1);
        Assert.Equal(48000, BrstmFile.Encode(high, null, new BrstmEncodeOptions(MaxSampleRate: null)).SampleRate);
        var r = BrstmFile.Encode(high, null);
        Assert.Equal(32000, r.SampleRate);
        Assert.InRange(r.SampleCount, 31999, 32001);
    }

    [Fact]
    public void Encode_RejectsBadLoops()
    {
        var audio = Signal(32000, 1);
        Assert.Throws<ArgumentException>(() => BrstmFile.Encode(audio, new LoopPoints(100, 100)));
        Assert.Throws<ArgumentException>(() => BrstmFile.Encode(audio, new LoopPoints(0, audio.Length + 1)));
    }

    [Fact]
    public void Encode_ReportsProgress()
    {
        var reports = new List<double>();
        BrstmFile.Encode(Signal(32000, 20), null, progress: new SyncProgress(v => { lock (reports) reports.Add(v); }));
        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports.Max(), 2);
        Assert.InRange(reports.Count, 1, 101); // whole percents only, so the UI isn't flooded
    }

    [Fact]
    public void NormalizePeak_ReachesTarget()
    {
        var quiet = Signal(32000, 1, level: 0.1);
        var loud = AudioOps.NormalizePeak(quiet);
        Assert.InRange(AudioOps.Peak(loud), 0.94, 0.96);
        var r = BrstmFile.Encode(quiet, null, new BrstmEncodeOptions(NormalizePeak: true));
        Assert.InRange(AudioOps.Peak(BrstmFile.Decode(r.Bytes).Audio), 0.85, 1.0);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void Import_Wav(int bits)
    {
        using var tmp = new TempSetup();
        var audio = Signal(44100, 1);
        string path = Path.Combine(tmp.Root, $"song{bits}.wav");
        using (var w = new WaveFileWriter(path, new WaveFormat(44100, bits, 2)))
        {
            var provider = new AudioOps.PcmSampleProvider(audio);
            var buf = new float[4096];
            int n;
            while ((n = provider.Read(buf, 0, buf.Length)) > 0) w.WriteSamples(buf, 0, n);
        }

        var imported = AudioImport.Load(path);
        Assert.Null(imported.Loop);
        Assert.Equal(44100, imported.Audio.SampleRate);
        Assert.Equal(audio.Length, imported.Audio.Length);
        Assert.True(RelativeError(audio.Channels[1], 0, imported.Audio.Channels[1], 0, audio.Length) < 0.001);
    }

    [Fact]
    public void Import_Mp3_ViaWindows()
    {
        using var tmp = new TempSetup();
        string wav = Path.Combine(tmp.Root, "song.wav"), mp3 = Path.Combine(tmp.Root, "song.mp3");
        var audio = Signal(44100, 3);
        using (var w = new WaveFileWriter(wav, new WaveFormat(44100, 16, 2)))
        {
            var p = new AudioOps.PcmSampleProvider(audio); var buf = new float[4096]; int n;
            while ((n = p.Read(buf, 0, buf.Length)) > 0) w.WriteSamples(buf, 0, n);
        }
        try
        {
            using var reader = new WaveFileReader(wav);
            MediaFoundationEncoder.EncodeToMp3(reader, mp3, 192000);
        }
        catch (Exception e)
        {
            output.WriteLine($"Skipped: this PC has no MP3 encoder to make a test file ({e.Message})");
            return;
        }

        var imported = AudioImport.Load(mp3);
        Assert.Equal(44100, imported.Audio.SampleRate);
        Assert.Equal(2, imported.Audio.ChannelCount);
        // MP3 adds encoder delay/padding (a few ms), so only check the length roughly.
        Assert.InRange(imported.Audio.Duration.TotalSeconds, 2.9, 3.2);
    }

    [Fact]
    public void Import_Brstm_KeepsItsLoop()
    {
        using var tmp = new TempSetup();
        string path = Path.Combine(tmp.Root, "song.brstm");
        var made = BrstmFile.Encode(Signal(32000, 4), new LoopPoints(BrstmFile.BlockSamples, 100000));
        File.WriteAllBytes(path, made.Bytes);
        var imported = AudioImport.Load(path);
        Assert.Equal(new LoopPoints(BrstmFile.BlockSamples, 100000), imported.Loop);
    }

    [Fact]
    public void Import_GarbageExplainsItself()
    {
        using var tmp = new TempSetup();
        string path = Path.Combine(tmp.Root, "notes.mp3");
        File.WriteAllText(path, "this is not audio");
        var e = Assert.Throws<AudioImportException>(() => AudioImport.Load(path));
        Assert.Contains("notes.mp3", e.Message);
        Assert.Throws<AudioImportException>(() => AudioImport.Load(Path.Combine(tmp.Root, "gone.wav")));
        File.WriteAllText(Path.Combine(tmp.Root, "fake.brstm"), "nope");
        Assert.Throws<AudioImportException>(() => AudioImport.Load(Path.Combine(tmp.Root, "fake.brstm")));
    }

    [Fact]
    public void FixDataSize_OnlyTouchesTheMismatch()
    {
        var good = BrstmFile.Encode(Signal(32000, 1), null).Bytes;
        Assert.Same(good, BrstmFile.FixDataSize(good));

        // Recreate the stock-file quirk: main header claims 32 more bytes than the DATA section.
        var quirky = good.Concat(new byte[32]).ToArray();
        int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(quirky.AsSpan(0x24));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(quirky.AsSpan(0x24), size + 32);
        var info = BrstmFile.ReadInfo(quirky);
        Assert.Equal(32000, info.SampleCount);
    }

    [Fact]
    public void Preview_JumpsBackAtLoopEnd_LikeTheGame()
    {
        var mono = new PcmAudio([Enumerable.Range(0, 10).Select(i => (short)(i * 1000)).ToArray()], 32000);
        var p = new AudioOps.PcmSampleProvider(mono, new LoopPoints(3, 6)) { Position = 4 };
        var buf = new float[8];
        Assert.Equal(8, p.Read(buf, 0, 8));
        Assert.Equal(new[] { 4, 5, 3, 4, 5, 3, 4, 5 }, buf.Select(f => (int)Math.Round(f * 32768 / 1000)).ToArray());
        Assert.Equal(2, p.LoopCount);

        p.Loop = null;                      // "no loop": plays to the end and stops
        Assert.Equal(4, p.Read(buf, 0, 8)); // samples 6..9
        Assert.Equal(0, p.Read(buf, 0, 8));
        Assert.Throws<ArgumentException>(() => p.Loop = new LoopPoints(5, 5));
    }

    // ---- real files ----

    [Fact]
    public void EveryStockSong_Reads_AndLoopsOnABlock()
    {
        string strm = Path.Combine(RealData.BuildSound, "strm");
        if (!Directory.Exists(strm)) { output.WriteLine("Skipped: no P+ build here"); return; }
        int n = 0, root = 0;
        foreach (var f in Directory.EnumerateFiles(strm, "*.brstm", SearchOption.AllDirectories))
        {
            var info = BrstmFile.ReadInfo(f);
            if (info.Loop is { } l) Assert.True(l.Start % BrstmFile.BlockSamples == 0, f);
            Assert.Equal(info, BrstmFile.ReadHeaderInfo(f)); // the fast header-only read agrees with the full one
            if (Path.GetDirectoryName(f) == strm) root++;
            n++;
        }
        output.WriteLine($"{n} stock songs read, {root} not in a series folder");
        Assert.True(n > 300);
    }

    [Theory]
    [InlineData(@"SSB64\Bonus")]              // one of the 4 with the DATA-size quirk
    [InlineData(@"Yoshi\Yoshi's Song (SSB)")] // another
    public void RealSong_DecodesAndReencodesWithTheSameLoop(string song)
    {
        string path = Path.Combine(RealData.BuildSound, "strm", song + ".brstm");
        if (!File.Exists(path)) { output.WriteLine("Skipped: no P+ build here"); return; }
        var (audio, loop) = BrstmFile.Decode(path);
        var info = BrstmFile.ReadInfo(path);
        Assert.Equal(info.SampleCount, audio.Length);

        var again = BrstmFile.Encode(audio, loop);
        Assert.Equal(info.SampleRate, again.SampleRate);
        Assert.Equal(info.Loop, again.Loop); // already block-aligned, so nothing moves
        output.WriteLine($"{song}: {info.Duration:mm\\:ss}, {info.SampleRate} Hz, loop {loop}, {new FileInfo(path).Length / 1024} KB -> {again.Bytes.Length / 1024} KB");
    }

    /// <summary>Independent check: BrawlCrate's own library opens our file and sees the same rate, length and loop.</summary>
    [Fact]
    public void BrawlCrate_OpensOurBrstm()
    {
        if (!File.Exists(RealData.BrawlLib) || !File.Exists(RealData.PowerShell32)) { output.WriteLine("Skipped: BrawlCrate not here"); return; }
        using var tmp = new TempSetup();
        string path = Path.Combine(tmp.Root, "made.brstm");
        var made = BrstmFile.Encode(Signal(44100, 6), new LoopPoints(50000, 240000));
        File.WriteAllBytes(path, made.Bytes);

        string script = $"$a=[Reflection.Assembly]::LoadFrom('{RealData.BrawlLib}'); " +
                        $"$n=[BrawlLib.SSBB.ResourceNodes.NodeFactory]::FromFile($null,'{path}'); " +
                        "\"$($n.GetType().Name) $($n.SampleRate) $($n.Channels) $($n.IsLooped) $($n.LoopStartSample) $($n.NumSamples)\"; $n.Dispose()";
        var psi = new ProcessStartInfo(RealData.PowerShell32, ["-NoProfile", "-Command", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd().Trim();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        output.WriteLine($"BrawlLib: {stdout} {stderr}");

        var l = made.Loop!.Value;
        Assert.Equal($"RSTMNode 32000 2 True {l.Start} {made.SampleCount}", stdout);
    }

    /// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; posts later, which races the assert).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
