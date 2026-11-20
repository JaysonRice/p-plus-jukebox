using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PPlusTracklistEditor.Core.Audio;

/// <summary>Sample-level helpers: resampling, peak normalizing, and float/short conversion.</summary>
public static class AudioOps
{
    /// <summary>
    /// Changes the sample rate. Uses NAudio's WDL resampler (a windowed-sinc filter, the same one REAPER uses),
    /// which low-pass filters before downsampling so high frequencies don't fold back as noise (aliasing).
    /// </summary>
    public static PcmAudio Resample(PcmAudio audio, int newRate)
    {
        if (newRate == audio.SampleRate) return audio;
        var source = new PcmSampleProvider(audio);
        var resampler = new WdlResamplingSampleProvider(source, newRate);
        long expected = (long)Math.Round((double)audio.Length * newRate / audio.SampleRate);
        var channels = ReadAll(resampler, audio.ChannelCount, expected);
        return new PcmAudio(channels, newRate);
    }

    /// <summary>The loudest sample as a fraction of full scale (0..1).</summary>
    public static double Peak(PcmAudio audio)
    {
        int peak = 0;
        foreach (var ch in audio.Channels)
            foreach (var s in ch)
                peak = Math.Max(peak, Math.Abs((int)s));
        return peak / 32768.0;
    }

    /// <summary>Scales the audio so its loudest sample reaches <paramref name="target"/> of full scale (default ~ -0.5 dB).</summary>
    public static PcmAudio NormalizePeak(PcmAudio audio, double target = 0.95)
    {
        double peak = Peak(audio);
        if (peak <= 0) return audio;
        double gain = target / peak;
        var channels = audio.Channels.Select(ch => ch.Select(s => ToShort(s / 32768.0 * gain)).ToArray()).ToArray();
        return new PcmAudio(channels, audio.SampleRate);
    }

    internal static short ToShort(double v)
    {
        // x32768 (with clamping) is the exact inverse of the /32768 used when reading, so short -> float -> short is lossless.
        double x = Math.Round(v * 32768.0);
        return (short)Math.Clamp(x, short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// Reads an NAudio sample stream to the end and splits it into channels.
    /// More than two channels (e.g. 5.1) are folded into stereo: even channels to the left, odd to the right.
    /// </summary>
    internal static short[][] ReadAll(ISampleProvider provider, int channelsIn, long expectedFrames = 0)
    {
        int outChannels = Math.Min(channelsIn, 2);
        var lists = Enumerable.Range(0, outChannels).Select(_ => new List<short>((int)Math.Min(expectedFrames, int.MaxValue))).ToArray();
        var buffer = new float[channelsIn * 8192];
        double fold = channelsIn > 2 ? 2.0 / channelsIn : 1.0;
        var frame = new double[outChannels];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i + channelsIn <= read; i += channelsIn)
            {
                Array.Clear(frame);
                for (int c = 0; c < channelsIn; c++) frame[c % outChannels] += buffer[i + c];
                for (int c = 0; c < outChannels; c++) lists[c].Add(ToShort(frame[c] * fold));
            }
        }
        return lists.Select(l => l.ToArray()).ToArray();
    }

    /// <summary>
    /// Exposes a <see cref="PcmAudio"/> to NAudio as an interleaved float stream. Also the preview player's source:
    /// with <see cref="Loop"/> set, reaching the loop end jumps back to the loop start, exactly like the game,
    /// so the seam can be heard. Without a loop it stops at the end of the audio.
    /// </summary>
    public sealed class PcmSampleProvider : ISampleProvider
    {
        private readonly PcmAudio _audio;
        private int _position;

        public PcmSampleProvider(PcmAudio audio, LoopPoints? loop = null)
        {
            _audio = audio;
            Loop = loop;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(audio.SampleRate, audio.ChannelCount);
        }

        public WaveFormat WaveFormat { get; }

        /// <summary>Can be changed while playing (the loop editor moves markers live).</summary>
        public LoopPoints? Loop
        {
            get => _loop;
            set { value?.Validate(_audio.Length); _loop = value; }
        }
        private LoopPoints? _loop;
        /// <summary>How many times playback has jumped back to the loop start.</summary>
        public int LoopCount { get; private set; }

        /// <summary>Current position in samples per channel. Set it to seek (clamped to the audio).</summary>
        public int Position
        {
            get => Volatile.Read(ref _position);
            set => Volatile.Write(ref _position, Math.Clamp(value, 0, _audio.Length));
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int ch = _audio.ChannelCount;
            int wanted = count / ch, written = 0;
            int pos = Position;
            var loop = Loop;
            while (written < wanted)
            {
                if (loop is { } l && pos >= l.End) { pos = l.Start; LoopCount++; }
                int end = loop?.End ?? _audio.Length;
                int frames = Math.Min(wanted - written, end - pos);
                if (frames <= 0) break; // end of audio (no loop)
                for (int f = 0; f < frames; f++)
                    for (int c = 0; c < ch; c++)
                        buffer[offset + (written + f) * ch + c] = _audio.Channels[c][pos + f] / 32768f;
                pos += frames;
                written += frames;
            }
            Position = pos;
            return written * ch;
        }
    }
}
