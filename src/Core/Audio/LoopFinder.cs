using NAudio.Dsp;

namespace PPlusTracklistEditor.Core.Audio;

/// <summary>A suggested loop end. <see cref="Match"/> is how alike the music sounds after it and after the loop start (0..1).</summary>
public sealed record LoopSuggestion(int End, double Match);

/// <summary>A suggested whole loop: where it starts and ends, and how alike the repeat sounds (0..1).</summary>
public sealed record LoopPairSuggestion(int Start, int End, double Match)
{
    public LoopPoints Loop => new(Start, End);
}

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
    /// Suggests a whole loop (start and end) for a file that plays its loop more than once (intro, loop, loop again...).
    ///
    /// For every possible loop length L (8 s and up), it compares the song with itself L later: frame t against frame
    /// t + L. Where the song repeats, that similarity stays near 1 for as long as the repeat lasts. The real loop length
    /// is the one with the longest such stretch, since the repeat covers the whole loop. (A phrase that repeats inside
    /// the song only matches for a short while; twice the loop length matches too, but over a shorter stretch, because
    /// the file runs out sooner.) The stretch begins where the intro ends: that's the loop start. The end is one loop
    /// length later, lined up to the exact sample.
    ///
    /// Cost: about frames² / 2 comparisons of 36 numbers. To keep a long song under a second, this uses every second
    /// frame (~46 ms apart); the exact sample is found afterwards.
    /// </summary>
    public static IReadOnlyList<LoopPairSuggestion> SuggestLoops(PcmAudio audio, int count = 5, CancellationToken cancel = default)
    {
        float[] mono = Mono(audio);
        float[] analysis = Resampled(mono, audio.SampleRate);
        double ratio = (double)audio.SampleRate / AnalysisRate;
        var all = Features(analysis, cancel);
        var g = all.Where((_, i) => i % 2 == 0).ToArray();
        int frames = g.Length;
        double fps = (double)AnalysisRate / (Hop * 2);
        int minLag = (int)(MinLoopSeconds * fps), minRun = (int)(4 * fps), smooth = (int)(2 * fps);
        if (frames - minLag < minRun) return [];

        var runs = new (int Lag, int Start, int Length, double Match)[frames];
        Parallel.For(minLag, frames - minRun + 1, new ParallelOptions { CancellationToken = cancel }, lag =>
        {
            int n = frames - lag;
            var prefix = new double[n + 1];
            for (int t = 0; t < n; t++) prefix[t + 1] = prefix[t] + Dot(g[t], g[t + lag]);
            // Longest stretch where the 2-second average similarity stays above RepeatLevel.
            int bestStart = 0, bestLen = 0, runStart = -1;
            for (int t = 0; t <= n; t++)
            {
                bool high = t + smooth <= n && (prefix[t + smooth] - prefix[t]) / smooth >= RepeatLevel;
                if (high && runStart < 0) runStart = t;
                if (!high && runStart >= 0)
                {
                    int len = t - runStart + smooth; // the last high window reaches smooth frames further
                    if (len > bestLen) { bestLen = Math.Min(len, n - runStart); bestStart = runStart; }
                    runStart = -1;
                }
            }
            double match = bestLen > 0 ? (prefix[bestStart + bestLen] - prefix[bestStart]) / bestLen : 0;
            runs[lag] = (lag, bestStart, bestLen, match);
        });

        // The real loop keeps repeating until the file runs out (or fades), while a section that repeats inside the song
        // stops partway. So: repeats that reach the end first, then the longest, then the shortest loop.
        int endSlack = (int)(3 * fps);
        bool ReachesEnd((int Lag, int Start, int Length, double Match) r) => r.Start + r.Length >= frames - r.Lag - endSlack;
        var picked = new List<(int Lag, int Start, int Length, double Match)>();
        foreach (var r in runs.Where(r => r.Length >= minRun).OrderByDescending(ReachesEnd).ThenByDescending(r => r.Length).ThenBy(r => r.Lag))
        {
            if (picked.Count == count) break;
            if (picked.All(p => Math.Abs(p.Lag - r.Lag) >= fps)) picked.Add(r);
        }

        int frameSamples = (int)Math.Round(2 * Hop * ratio);
        int rate = audio.SampleRate;
        return picked.Select(r =>
        {
            // 1. Exact loop length, measured in the middle of the repeat where it's surely the same music.
            int mid = Math.Min((r.Start + r.Length / 2) * frameSamples, mono.Length - 1);
            int lag = Refine(mono, rate, mid, mid + r.Lag * frameSamples, frameSamples * 2) - mid;
            // 2. Walk back while the audio one loop later is (nearly) identical: 50 ms steps, then 5 ms steps.
            //    "Sounds similar" isn't enough here: an intro often ends like the loop does, but with small differences
            //    you'd hear right after the jump.
            int s = mid;
            foreach (int step in new[] { rate / 20, rate / 200 })
                while (s - step >= 0 && SameAudio(mono, s - step, s - step + lag, step)) s -= step;
            // 3. Start a little inside that point (50 ms, or less for a short loop); any start inside the repeat is seamless.
            int start = Math.Min(s + rate / 20, mid);
            int end = Math.Min(start + lag, audio.Length);
            return new LoopPairSuggestion(start, end, Math.Clamp(r.Match, 0, 1));
        })
        .DistinctBy(p => (p.End - p.Start) / Math.Max(1, rate / 100)) // two rough lengths can land on the same exact loop
        .ToList();
    }

    /// <summary>
    /// True if mono[a..a+n) and mono[b..b+n) are essentially the same audio: their difference has under 2% of their
    /// energy (silence counts as the same). Allows for lossy-file noise, not for different notes or instruments.
    /// </summary>
    private static bool SameAudio(float[] mono, int a, int b, int n)
    {
        if (b + n > mono.Length) return false;
        double diff = 0, energy = 0;
        for (int i = 0; i < n; i++)
        {
            double x = mono[a + i], y = mono[b + i];
            diff += (x - y) * (x - y);
            energy += x * x + y * y;
        }
        return energy < n * 1e-8 || diff / energy < 0.02;
    }
    /// <summary>How similar (2-second average) two stretches must be to count as the same music playing again.</summary>
    private const double RepeatLevel = 0.9;

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
