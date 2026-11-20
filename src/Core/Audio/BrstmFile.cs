using System.Buffers.Binary;
using VGAudio.Containers.NintendoWare;
using VGAudio.Formats;
using VGAudio.Formats.GcAdpcm;
using VGAudio.Formats.Pcm16;

namespace PPlusTracklistEditor.Core.Audio;

/// <summary>What a BRSTM holds, without decoding its audio.</summary>
public sealed record BrstmInfo(int SampleRate, int ChannelCount, int SampleCount, LoopPoints? Loop)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)SampleCount / SampleRate);
}

/// <summary>An encoded song, ready to save as a .brstm.</summary>
/// <param name="Loop">The loop as stored in the file. Its start is moved forward to a 14336-sample block (see <see cref="BrstmFile"/>).</param>
public sealed record BrstmEncodeResult(byte[] Bytes, int SampleRate, int ChannelCount, int SampleCount, LoopPoints? Loop)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)SampleCount / SampleRate);
}

/// <summary>
/// Options for making a BRSTM.
/// </summary>
/// <param name="MaxSampleRate">
/// Audio above this rate is downsampled to it. 32000 Hz is what almost every P+ song uses (286 of 304 stock songs):
/// it keeps files about a third smaller than 48 kHz, and the Wii mixes at 32 kHz anyway. Lower rates are kept as they are
/// (upsampling only makes the file bigger). Null keeps the original rate.
/// </param>
/// <param name="NormalizePeak">Make the loudest moment reach just under full volume.</param>
public sealed record BrstmEncodeOptions(int? MaxSampleRate = BrstmFile.GameSampleRate, bool NormalizePeak = false);

/// <summary>
/// Reads and writes BRSTM songs (Nintendo's "RSTM" streamed audio, DSP-ADPCM compressed) using VGAudio.
///
/// DSP-ADPCM stores each 14 samples as 8 bytes: a header byte (which of 8 predictor filters + a scale) and 14 four-bit
/// differences from what the filter predicted. That's ~3.5x smaller than 16-bit PCM. The game streams the file in blocks
/// of 14336 samples per channel.
///
/// Loop alignment: every looping song in stock P+ (and in the music repo we checked) starts its loop exactly on a block
/// boundary (a multiple of 14336). VGAudio does the same by default: it moves the loop start forward to the next block
/// boundary and the loop end by the same amount, copying the audio in between from the start of the loop, so the music
/// sounds identical and the loop length doesn't change. The file gets up to ~0.45 s (at 32 kHz) longer.
/// </summary>
public static class BrstmFile
{
    public const int GameSampleRate = 32000;
    public const int BlockSamples = 0x3800; // 14336

    public static BrstmInfo ReadInfo(string path) => ReadInfo(File.ReadAllBytes(path));

    public static BrstmInfo ReadInfo(byte[] bytes)
    {
        var format = ReadFormat(bytes);
        return new BrstmInfo(format.SampleRate, format.ChannelCount, format.SampleCount, LoopOf(format));
    }

    /// <summary>
    /// Like <see cref="ReadInfo(string)"/> but reads only the start of the file (the HEAD section), so listing
    /// hundreds of songs with their lengths is fast. Returns null if the header isn't laid out as expected.
    ///
    /// RSTM layout: file header (0x10 = offset of HEAD). HEAD = "HEAD", size, then 3 references (u32 marker + u32 offset,
    /// relative to HEAD+8). The first points at the stream info: u8 codec, u8 loop flag, u8 channels, pad,
    /// u16 sample rate, pad, u32 loop start, u32 total samples.
    /// </summary>
    public static BrstmInfo? ReadHeaderInfo(string path)
    {
        Span<byte> b = stackalloc byte[4096];
        int n;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1)) n = fs.ReadAtLeast(b, b.Length, throwOnEndOfStream: false);
        b = b[..n];
        if (n < 0x40 || !b[..4].SequenceEqual("RSTM"u8)) return null;
        int head = (int)BinaryPrimitives.ReadUInt32BigEndian(b[0x10..]);
        if (head + 0x10 > n || !b.Slice(head, 4).SequenceEqual("HEAD"u8)) return null;
        int info = head + 8 + (int)BinaryPrimitives.ReadUInt32BigEndian(b[(head + 0x0C)..]);
        if (info + 0x10 > n) return null;
        bool loops = b[info + 1] != 0;
        int channels = b[info + 2];
        int rate = BinaryPrimitives.ReadUInt16BigEndian(b[(info + 4)..]);
        int loopStart = (int)BinaryPrimitives.ReadUInt32BigEndian(b[(info + 8)..]);
        int samples = (int)BinaryPrimitives.ReadUInt32BigEndian(b[(info + 0x0C)..]);
        if (rate == 0 || channels == 0 || samples <= 0) return null;
        return new BrstmInfo(rate, channels, samples, loops ? new LoopPoints(loopStart, samples) : null);
    }

    /// <summary>Decodes a BRSTM to 16-bit PCM plus its loop (null if it doesn't loop).</summary>
    public static (PcmAudio Audio, LoopPoints? Loop) Decode(string path) => Decode(File.ReadAllBytes(path));

    public static (PcmAudio Audio, LoopPoints? Loop) Decode(byte[] bytes)
    {
        var format = ReadFormat(bytes);
        var pcm = format.ToPcm16();
        var audio = new PcmAudio(pcm.Channels, pcm.SampleRate);
        return (audio, LoopOf(format));
    }

    /// <summary>
    /// Encodes audio as a DSP-ADPCM BRSTM. <paramref name="loop"/> is in <paramref name="audio"/>'s samples;
    /// null makes a song that plays once. Audio after the loop end is dropped (it would never play).
    /// </summary>
    public static BrstmEncodeResult Encode(PcmAudio audio, LoopPoints? loop, BrstmEncodeOptions? options = null, IProgress<double>? progress = null)
    {
        options ??= new BrstmEncodeOptions();
        loop?.Validate(audio.Length);

        int rate = OutputRate(audio.SampleRate, options);
        var working = AudioOps.Resample(audio, rate);
        if (options.NormalizePeak) working = AudioOps.NormalizePeak(working);
        var scaledLoop = loop?.Rescale(audio.SampleRate, rate, working.Length);

        var pcm = new Pcm16Format(working.Channels, rate);
        if (scaledLoop is { } l) pcm = pcm.WithLoop(true, l.Start, l.End);

        var config = new BxstmConfiguration
        {
            Codec = NwCodec.GcAdpcm,
            LoopPointAlignment = BlockSamples,
            TrimFile = true,
            Progress = progress is null ? null : new ProgressAdapter(progress),
        };
        var bytes = new BrstmWriter().GetFile(new AudioData(pcm), config);

        var info = ReadInfo(bytes);
        return new BrstmEncodeResult(bytes, info.SampleRate, info.ChannelCount, info.SampleCount, info.Loop);
    }

    /// <summary>The sample rate <see cref="Encode"/> will use for audio at <paramref name="sourceRate"/>.</summary>
    public static int OutputRate(int sourceRate, BrstmEncodeOptions? options = null)
    {
        options ??= new BrstmEncodeOptions();
        return options.MaxSampleRate is int max && sourceRate > max ? max : sourceRate;
    }

    /// <summary>
    /// About how big <see cref="Encode"/>'s file will be, without encoding (for the loop editor's size readout).
    /// DSP-ADPCM is 8 bytes per 14 samples per channel; the file also stores a small seek table (4 bytes per channel
    /// per block) and headers. With a loop, audio after the end is dropped and the start is moved to the next block.
    /// </summary>
    public static long EstimateSize(PcmAudio audio, LoopPoints? loop, BrstmEncodeOptions? options = null)
    {
        int rate = OutputRate(audio.SampleRate, options);
        long length = (long)Math.Round((double)audio.Length * rate / audio.SampleRate);
        long samples = length;
        if (loop is { } l)
        {
            var scaled = l.Rescale(audio.SampleRate, rate, (int)length);
            samples = scaled.End + (BlockSamples - scaled.Start % BlockSamples) % BlockSamples;
        }
        long blocks = (samples + BlockSamples - 1) / BlockSamples;
        long audioBytes = (samples + 13) / 14 * 8;
        return 0x300 + audio.ChannelCount * (audioBytes + blocks * 4 + 32);
    }

    private static LoopPoints? LoopOf(IAudioFormat format) =>
        format.Looping ? new LoopPoints(format.LoopStart, format.LoopEnd) : null;

    private static IAudioFormat ReadFormat(byte[] bytes)
    {
        if (bytes.Length < 0x28 || bytes[0] != 'R' || bytes[1] != 'S' || bytes[2] != 'T' || bytes[3] != 'M')
            throw new InvalidDataException("This isn't a BRSTM file.");
        var data = new BrstmReader().Read(FixDataSize(bytes));
        // GcAdpcm for normal songs; some tools write 8/16-bit PCM BRSTMs, which VGAudio exposes as Pcm16/Pcm8.
        var formats = data.ListAvailableFormats().ToList();
        if (formats.Contains(typeof(GcAdpcmFormat))) return data.GetFormat<GcAdpcmFormat>();
        return data.GetFormat<Pcm16Format>();
    }

    /// <summary>
    /// Some real songs (4 in stock P+ 3.2.1, e.g. SSB64/Bonus) say in the main header that the DATA section is 32 bytes
    /// bigger than the DATA section itself says (trailing padding). The game doesn't care; VGAudio refuses the file.
    /// Returns a copy with the main header's size set to the section's own size, or the original bytes if they agree.
    /// </summary>
    internal static byte[] FixDataSize(byte[] bytes)
    {
        int dataOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0x20));
        if (dataOffset <= 0 || dataOffset + 8 > bytes.Length) return bytes;
        uint headerSays = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0x24));
        uint blockSays = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(dataOffset + 4));
        if (headerSays == blockSays || dataOffset + (long)blockSays > bytes.Length) return bytes;
        var copy = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(0x24), blockSays);
        return copy;
    }

    /// <summary>
    /// VGAudio reports progress per tiny chunk (tens of thousands of times per song), possibly from several threads.
    /// Passing each one on would flood the UI thread with updates (a Progress&lt;T&gt; posts every report to it), so only
    /// whole-percent changes are forwarded.
    /// </summary>
    internal sealed class ProgressAdapter(IProgress<double> target) : VGAudio.IProgressReport
    {
        private int _total = 1, _done, _lastPercent = -1;
        public void SetTotal(int value) => _total = Math.Max(1, value);
        public void Report(int value) { Volatile.Write(ref _done, value); Forward(value); }
        public void ReportAdd(int value) => Forward(Interlocked.Add(ref _done, value));
        public void LogMessage(string message) { }

        private void Forward(int done)
        {
            int percent = (int)Math.Clamp(100L * done / _total, 0, 100);
            int last = Volatile.Read(ref _lastPercent);
            if (percent > last && Interlocked.CompareExchange(ref _lastPercent, percent, last) == last)
                target.Report(percent / 100.0);
        }
    }
}
