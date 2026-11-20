using NAudio.Dsp;

namespace PPlusTracklistEditor.Core.Audio;

/// <summary>A suggested loop end. <see cref="Match"/> is how alike the music sounds after it and after the loop start (0..1).</summary>
public sealed record LoopSuggestion(int End, double Match);

/// <summary>
/// "Suggest a loop end": given where the loop starts, find where the song comes back around to that same music.
///
/// Idea (the same goal as Loopatron/Remixatron, which find musically similar beats; this is a simpler version aimed at
/// one question): a loop end E is seamless if the music that would have played after E is the music after the loop
/// start S, because that's what plays instead after the jump. Many downloaded songs play the loop twice (intro, loop,
/// loop again, fade), so the place where the second time through begins is the answer.
///
/// How:
/// 1. Mix to mono at ~22 kHz and cut it into overlapping frames of 4096 samples, every 512 samples (~23 ms).
/// 2. Describe each frame by its spectrum (an FFT): 12 "chroma" values (how much of each musical note C, C#, D...,
///    in any octave) for the harmony/melody, plus 24 band loudnesses for the instruments' tone. Two frames that sound
///    alike get similar descriptions (cosine similarity close to 1).
/// 3. Compare the ~15 seconds after S, frame by frame, with the 15 seconds after every later candidate E. The
///    candidates with the most similar stretch are the suggestions (best first, at least a second apart).
/// 4. Fine-tune each one to the exact sample by lining up the waveforms (cross-correlation) around it, so the wave
///    continues smoothly across the jump.
///
/// Cost: a 3-minute song has ~7,700 frames; comparing a 650-frame window at each candidate is ~200 million
/// multiply-adds, a fraction of a second. It runs on a background thread anyway.
/// </summary>
public static class LoopFinder
{
    private const int AnalysisRate = 22050;
    private const int FrameSize = 4096, FftOrder = 12, Hop = 512;
    private const int Bands = 24;
    private const double WindowSeconds = 15, MinAfterSeconds = 3, MinLoopSeconds = 8;

    /// <summary>Below this, the best match is too weak to call a repeat.</summary>
    public const double GoodMatch = 0.8;

    /// <summary>Loop end suggestions for a loop starting at <paramref name="loopStart"/>, best first. Empty if the song has no room to repeat.</summary>
    public static IReadOnlyList<LoopSuggestion> SuggestEnds(PcmAudio audio, int loopStart, int count = 5, CancellationToken cancel = default)
    {
        float[] mono = Mono(audio);
        float[] analysis = Resampled(mono, audio.SampleRate);
        double ratio = (double)audio.SampleRate / AnalysisRate; // original samples per analysis sample
        var features = Features(analysis, cancel);
        int frames = features.Length;
        if (frames == 0) return [];

        double framesPerSecond = (double)AnalysisRate / Hop;
        int s = (int)Math.Round(loopStart / ratio / Hop);
        int window = (int)(WindowSeconds * framesPerSecond);
        int minAfter = (int)(MinAfterSeconds * framesPerSecond);
        int first = s + (int)(MinLoopSeconds * framesPerSecond);
        int last = frames - minAfter;
        if (s < 0 || s >= frames - minAfter || first > last) return [];

        // score[e] = average similarity of the stretch after e to the stretch after s
        var score = new double[frames];
        Parallel.For(first, last + 1, new ParallelOptions { CancellationToken = cancel }, e =>
        {
            int n = Math.Min(window, Math.Min(frames - e, frames - s));
            double sum = 0;
            for (int k = 0; k < n; k++) sum += Dot(features[s + k], features[e + k]);
            score[e] = sum / n;
        });

        // Best candidates, at least one second apart (otherwise the top 5 would be neighbours of one spot).
        int spacing = (int)framesPerSecond;
        var picked = new List<int>();
        foreach (int e in Enumerable.Range(first, last - first + 1).OrderByDescending(e => score[e]))
        {
            if (picked.Count == count) break;
            if (picked.All(p => Math.Abs(p - e) >= spacing)) picked.Add(e);
        }

        // Equally good matches (the 2nd and 3rd time through the loop both match perfectly): the earlier one first,
        // so the first suggestion is one time through, not two.
        return picked
            .Select(e => new LoopSuggestion(Refine(mono, audio.SampleRate, loopStart, (int)Math.Round(e * Hop * ratio), (int)Math.Ceiling(Hop * ratio)), Math.Clamp(score[e], 0, 1)))
            .OrderByDescending(sug => Math.Round(sug.Match, 2))
            .ThenBy(sug => sug.End)
            .ToList();
    }

    /// <summary>
    /// Moves a rough end (within ±<paramref name="range"/> samples) to where the waveform after it best lines up with the
    /// waveform after the loop start (normalized cross-correlation over ~0.2 s).
    /// </summary>
    private static int Refine(float[] mono, int rate, int start, int roughEnd, int range)
    {
        int n = Math.Min(rate / 5, mono.Length - start);
        int bestLag = 0;
        double best = double.NegativeInfinity;
        for (int lag = -range; lag <= range; lag++)
        {
            int e = roughEnd + lag;
            if (e <= start || e + n > mono.Length) continue;
            double dot = 0, energy = 0;
            for (int i = 0; i < n; i++)
            {
                dot += mono[start + i] * mono[e + i];
                energy += mono[e + i] * mono[e + i];
            }
            double c = energy > 0 ? dot / Math.Sqrt(energy) : 0;
            if (c > best) { best = c; bestLag = lag; }
        }
        return Math.Clamp(roughEnd + bestLag, start + 1, mono.Length);
    }

    private static float[] Mono(PcmAudio audio)
    {
        var m = new float[audio.Length];
        float scale = 1f / (32768f * audio.ChannelCount);
        foreach (var ch in audio.Channels)
            for (int i = 0; i < m.Length; i++) m[i] += ch[i] * scale;
        return m;
    }

    private static float[] Resampled(float[] mono, int rate)
    {
        if (rate == AnalysisRate) return mono;
        // Linear interpolation is fine here: this is only for comparing, never heard.
        double step = (double)rate / AnalysisRate;
        var r = new float[(int)(mono.Length / step)];
        for (int i = 0; i < r.Length; i++)
        {
            double pos = i * step;
            int a = (int)pos;
            float t = (float)(pos - a);
            r[i] = a + 1 < mono.Length ? mono[a] * (1 - t) + mono[a + 1] * t : mono[a];
        }
        return r;
    }

    /// <summary>One normalized description (chroma + band loudness) per frame.</summary>
    private static float[][] Features(float[] x, CancellationToken cancel)
    {
        int frames = x.Length < FrameSize ? 0 : (x.Length - FrameSize) / Hop + 1;
        var result = new float[frames][];
        var hann = Enumerable.Range(0, FrameSize).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1)))).ToArray();

        // Which FFT bins belong to which note (chroma) and which band. Bins below ~55 Hz or above 8 kHz are skipped.
        int bins = FrameSize / 2;
        var noteOf = new int[bins];
        var bandOf = new int[bins];
        double lo = 55, hi = 8000;
        for (int b = 0; b < bins; b++)
        {
            double hz = (double)b * AnalysisRate / FrameSize;
            if (hz < lo || hz > hi) { noteOf[b] = -1; bandOf[b] = -1; continue; }
            double midi = 69 + 12 * Math.Log2(hz / 440);
            noteOf[b] = ((int)Math.Round(midi) % 12 + 12) % 12;
            bandOf[b] = Math.Min(Bands - 1, (int)(Bands * Math.Log(hz / lo) / Math.Log(hi / lo)));
        }

        Parallel.For(0, frames, new ParallelOptions { CancellationToken = cancel }, () => new Complex[FrameSize], (f, _, buf) =>
        {
            int offset = f * Hop;
            for (int i = 0; i < FrameSize; i++) { buf[i].X = x[offset + i] * hann[i]; buf[i].Y = 0; }
            FastFourierTransform.FFT(true, FftOrder, buf);
            var chroma = new double[12];
            var band = new double[Bands];
            for (int b = 1; b < bins; b++)
            {
                if (noteOf[b] < 0) continue;
                double power = buf[b].X * buf[b].X + buf[b].Y * buf[b].Y;
                chroma[noteOf[b]] += power;
                band[bandOf[b]] += power;
            }
            result[f] = Describe(chroma, band);
            return buf;
        }, _ => { });
        return result;
    }

    /// <summary>
    /// Log-compress (so loud and quiet parts compare by shape), center each part, and scale to length 1 so a dot
    /// product is the cosine similarity. Chroma and bands each make up half of the vector.
    /// </summary>
    private static float[] Describe(double[] chroma, double[] band)
    {
        var v = new float[12 + Bands];
        Part(chroma, v, 0);
        Part(band, v, 12);
        return v;

        static void Part(double[] src, float[] dst, int at)
        {
            var logs = src.Select(p => Math.Log10(1e-10 + p)).ToArray();
            double mean = logs.Average();
            double norm = Math.Sqrt(logs.Sum(l => (l - mean) * (l - mean)));
            for (int i = 0; i < src.Length; i++)
                dst[at + i] = norm > 1e-9 ? (float)((logs[i] - mean) / norm * Math.Sqrt(0.5)) : 0f;
        }
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}
