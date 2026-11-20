using System.Globalization;

namespace PPlusTracklistEditor.Core.Audio;

/// <summary>
/// The shape of a song for drawing: for each pixel column, the lowest and highest sample in that stretch of audio.
///
/// Drawing a 3-minute song means summarizing ~6 million samples into ~1000 columns, and the loop editor redraws while
/// zooming. So the constructor makes a summary once: min/max of every <see cref="SummaryBlock"/> samples (both channels
/// together). Zoomed out, a column covers thousands of samples and is built from a few summary entries instead of the
/// raw audio, so each redraw costs about the same no matter how long the song is. Zoomed in past the summary's detail,
/// the raw samples are read directly (there are few of them on screen then).
/// </summary>
public sealed class WaveformData
{
    public const int SummaryBlock = 256;
    private readonly PcmAudio _audio;
    private readonly short[] _min;
    private readonly short[] _max;

    public WaveformData(PcmAudio audio)
    {
        _audio = audio;
        int blocks = (audio.Length + SummaryBlock - 1) / SummaryBlock;
        _min = new short[blocks];
        _max = new short[blocks];
        for (int b = 0; b < blocks; b++)
        {
            int from = b * SummaryBlock, to = Math.Min(from + SummaryBlock, audio.Length);
            RawRange(from, to, out _min[b], out _max[b]);
        }
    }

    public PcmAudio Audio => _audio;
    public int Length => _audio.Length;

    /// <summary>
    /// Fills <paramref name="min"/>/<paramref name="max"/> (one entry per column, -1..1) for samples [from, to).
    /// Zoomed in so far that a column is less than one sample, columns repeat the sample they fall on.
    /// </summary>
    public void Peaks(double from, double to, Span<float> min, Span<float> max)
    {
        int columns = min.Length;
        if (columns == 0 || max.Length != columns) throw new ArgumentException("min and max need the same, non-zero length.");
        double perColumn = (to - from) / columns;
        for (int i = 0; i < columns; i++)
        {
            int a = (int)Math.Floor(from + i * perColumn);
            int b = (int)Math.Floor(from + (i + 1) * perColumn);
            if (b <= a) b = a + 1;
            a = Math.Clamp(a, 0, Length);
            b = Math.Clamp(b, 0, Length);
            short lo, hi;
            if (a >= b) lo = hi = 0; // outside the audio
            else if (b - a >= SummaryBlock * 2) SummaryRange(a, b, out lo, out hi);
            else RawRange(a, b, out lo, out hi);
            min[i] = lo / 32768f;
            max[i] = hi / 32768f;
        }
    }

    private void SummaryRange(int from, int to, out short lo, out short hi)
    {
        // Whole summary blocks inside the range, plus raw samples for the partial blocks at either edge.
        int firstFull = (from + SummaryBlock - 1) / SummaryBlock, lastFull = to / SummaryBlock; // [firstFull, lastFull)
        RawRange(from, Math.Min(to, firstFull * SummaryBlock), out lo, out hi);
        for (int b = firstFull; b < lastFull; b++)
        {
            if (_min[b] < lo) lo = _min[b];
            if (_max[b] > hi) hi = _max[b];
        }
        RawRange(Math.Max(from, lastFull * SummaryBlock), to, out short lo2, out short hi2);
        if (lo2 < lo) lo = lo2;
        if (hi2 > hi) hi = hi2;
    }

    private void RawRange(int from, int to, out short lo, out short hi)
    {
        lo = 0; hi = 0; // silence baseline, so an empty range draws nothing
        foreach (var ch in _audio.Channels)
            for (int i = from; i < to; i++)
            {
                short s = ch[i];
                if (s < lo) lo = s;
                if (s > hi) hi = s;
            }
    }
}

/// <summary>Helpers for picking loop points.</summary>
public static class LoopTools
{
    /// <summary>How far snapping looks for a zero crossing on either side: 20 ms (about one cycle of a low bass note).</summary>
    public static int SnapRadius(int sampleRate) => sampleRate / 50;

    /// <summary>
    /// The nearest "rising zero crossing" to <paramref name="sample"/> within <paramref name="radius"/>, or
    /// <paramref name="sample"/> itself if there's none. 0 and the end of the audio also count.
    ///
    /// Why: at the loop end, playback jumps from sample End-1 straight to sample Start. If the wave is at very different
    /// heights at those two points, the speaker cone has to jump instantly, which you hear as a click. A rising zero
    /// crossing is a spot where the wave goes from below zero to zero-or-above (both channels mixed). If both markers
    /// sit on one, the wave continues smoothly across the jump.
    /// </summary>
    public static int SnapToZeroCrossing(PcmAudio audio, int sample, int radius)
    {
        sample = Math.Clamp(sample, 0, audio.Length);
        if (IsCrossing(audio, sample)) return sample;
        for (int d = 1; d <= radius; d++)
        {
            if (sample - d >= 0 && IsCrossing(audio, sample - d)) return sample - d;
            if (sample + d <= audio.Length && IsCrossing(audio, sample + d)) return sample + d;
        }
        return sample;
    }

    /// <summary>
    /// The next rising zero crossing after (direction +1) or before (-1) <paramref name="sample"/>, for the fine-tune
    /// arrows. Gives up after one second and returns the position one second away (clamped to the audio).
    /// </summary>
    public static int StepToZeroCrossing(PcmAudio audio, int sample, int direction)
    {
        int step = Math.Sign(direction);
        if (step == 0) return sample;
        int limit = audio.SampleRate;
        for (int d = 1; d <= limit; d++)
        {
            int i = sample + d * step;
            if (i <= 0) return 0;
            if (i >= audio.Length) return audio.Length;
            if (IsCrossing(audio, i)) return i;
        }
        return Math.Clamp(sample + limit * step, 0, audio.Length);
    }

    /// <summary>True if the mixed signal crosses zero going up between sample i-1 and i (or i is the start or end).</summary>
    public static bool IsCrossing(PcmAudio audio, int i)
    {
        if (i <= 0 || i >= audio.Length) return true;
        return Mixed(audio, i - 1) < 0 && Mixed(audio, i) >= 0;
    }

    private static int Mixed(PcmAudio audio, int i)
    {
        int sum = 0;
        foreach (var ch in audio.Channels) sum += ch[i];
        return sum;
    }

    /// <summary>
    /// Where "Test the loop" starts playing: a few seconds before the loop end, so you hear the music lead into the
    /// jump back to the loop start.
    /// </summary>
    public static int TestStart(LoopPoints loop, int sampleRate, double secondsBefore = 3) =>
        Math.Max(0, loop.End - (int)(secondsBefore * sampleRate));

    /// <summary>The loop a newly opened file starts with: its own loop (BRSTMs), else the whole song.</summary>
    public static LoopPoints DefaultLoop(ImportedAudio imported) =>
        imported.Loop ?? new LoopPoints(0, imported.Audio.Length);
}

/// <summary>Times as people read and type them: "1:23.456" (minutes:seconds.milliseconds).</summary>
public static class TimeText
{
    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        int minutes = (int)t.TotalMinutes;
        return $"{minutes}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    /// <summary>Accepts "1:23.456", "1:23", "83.5" or "83" (seconds), with "." or "," for decimals.</summary>
    public static bool TryParse(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim().Replace(',', '.');
        string[] parts = s.Split(':');
        if (parts.Length > 2) return false;
        double minutes = 0;
        if (parts.Length == 2 && !(int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int m) && (minutes = m) >= 0)) return false;
        if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds)) return false;
        if (parts.Length == 2 && seconds >= 60) return false;
        time = TimeSpan.FromSeconds(minutes * 60 + seconds);
        return true;
    }
}
