namespace PPlusTracklistEditor.Core.Audio;

/// <summary>
/// Uncompressed 16-bit audio, one array per channel (1 = mono, 2 = stereo).
/// This is what every import produces and what the encoder and the preview player take.
/// </summary>
public sealed class PcmAudio
{
    public PcmAudio(short[][] channels, int sampleRate)
    {
        if (channels.Length is < 1 or > 2) throw new ArgumentException("Audio must be mono or stereo.", nameof(channels));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels.Any(c => c.Length != channels[0].Length)) throw new ArgumentException("Channels must be the same length.", nameof(channels));
        Channels = channels;
        SampleRate = sampleRate;
    }

    public short[][] Channels { get; }
    public int SampleRate { get; }
    public int ChannelCount => Channels.Length;
    /// <summary>Samples per channel.</summary>
    public int Length => Channels[0].Length;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Length / SampleRate);

    public TimeSpan TimeOf(int sample) => TimeSpan.FromSeconds((double)sample / SampleRate);
    public int SampleAt(TimeSpan time) => (int)Math.Round(time.TotalSeconds * SampleRate);
}

/// <summary>
/// A loop: when playback reaches <see cref="End"/> it jumps back to <see cref="Start"/>.
/// Both are sample positions; <see cref="End"/> is exclusive (the last sample played is End - 1).
/// </summary>
public readonly record struct LoopPoints(int Start, int End)
{
    public int Length => End - Start;

    public void Validate(int audioLength)
    {
        if (Start < 0 || End > audioLength || End <= Start)
            throw new ArgumentException($"Loop {Start}-{End} doesn't fit audio of {audioLength} samples (needs 0 <= start < end <= length).");
    }

    /// <summary>The same loop at another sample rate (positions scaled and rounded).</summary>
    public LoopPoints Rescale(int fromRate, int toRate, int newLength)
    {
        if (fromRate == toRate) return this;
        int s = (int)Math.Round((long)Start * (double)toRate / fromRate);
        int e = (int)Math.Round((long)End * (double)toRate / fromRate);
        e = Math.Min(e, newLength);
        s = Math.Min(s, e - 1);
        return new LoopPoints(Math.Max(0, s), e);
    }
}
