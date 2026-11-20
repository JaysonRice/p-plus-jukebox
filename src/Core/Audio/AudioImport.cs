using NAudio.Wave;

namespace PPlusTracklistEditor.Core.Audio;

/// <summary>Audio loaded from a file, plus its existing loop if the file had one (only BRSTMs do).</summary>
public sealed record ImportedAudio(PcmAudio Audio, LoopPoints? Loop, string SourcePath);

/// <summary>A file the app can't turn into audio. The message is meant to be shown to the user as-is.</summary>
public sealed class AudioImportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Opens audio files for making songs:
/// - .brstm: decoded with its loop, so an existing song can be re-looped.
/// - .wav: read directly (8/16/24/32-bit PCM and 32-bit float).
/// - everything else (.mp3, .m4a, .aac, .wma, .flac...): Windows Media Foundation, the decoder built into Windows.
///   That's why the app is Windows-only. Which formats work depends on the PC's codecs; MP3/AAC/WMA (and FLAC on Windows 10+) are built in.
/// </summary>
public static class AudioImport
{
    public static readonly string[] SupportedExtensions = [".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".ogg", ".brstm"];

    public static ImportedAudio Load(string path)
    {
        if (!File.Exists(path)) throw new AudioImportException($"The file \"{Path.GetFileName(path)}\" doesn't exist anymore.");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".brstm")
            {
                var (audio, loop) = BrstmFile.Decode(path);
                return new ImportedAudio(audio, loop, path);
            }
            if (ext == ".wav" && TryReadWav(path) is { } wav) return new ImportedAudio(wav, null, path);
            return new ImportedAudio(ReadWithMediaFoundation(path), null, path);
        }
        catch (AudioImportException) { throw; }
        catch (Exception e)
        {
            throw new AudioImportException(
                $"\"{Path.GetFileName(path)}\" couldn't be opened as audio. It may be damaged, or a type this PC can't play. Try converting it to WAV or MP3 first.", e);
        }
    }

    /// <summary>PCM/float WAVs. Returns null for WAV codecs WaveFileReader can't turn into samples (e.g. ADPCM WAVs), so Media Foundation gets a try.</summary>
    private static PcmAudio? TryReadWav(string path)
    {
        WaveFileReader reader;
        try { reader = new WaveFileReader(path); }
        catch (FormatException) { return null; } // unusual WAV layout: let Media Foundation try
        using var _ = reader;
        var enc = reader.WaveFormat.Encoding;
        if (enc is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)) return null;
        ISampleProvider samples;
        try { samples = reader.ToSampleProvider(); }
        catch (ArgumentException) { return null; }
        var channels = AudioOps.ReadAll(samples, reader.WaveFormat.Channels, reader.SampleCount);
        return Checked(new PcmAudio(channels, reader.WaveFormat.SampleRate), path);
    }

    private static PcmAudio ReadWithMediaFoundation(string path)
    {
        using var reader = new MediaFoundationReader(path);
        var samples = reader.ToSampleProvider();
        long frames = reader.Length / Math.Max(1, reader.WaveFormat.BlockAlign);
        var channels = AudioOps.ReadAll(samples, reader.WaveFormat.Channels, frames);
        return Checked(new PcmAudio(channels, reader.WaveFormat.SampleRate), path);
    }

    private static PcmAudio Checked(PcmAudio audio, string path)
    {
        if (audio.Length == 0) throw new AudioImportException($"\"{Path.GetFileName(path)}\" has no audio in it.");
        return audio;
    }
}
