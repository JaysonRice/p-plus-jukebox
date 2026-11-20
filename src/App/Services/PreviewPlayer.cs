using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PPlusTracklistEditor.Core.Audio;

namespace PPlusTracklistEditor.App.Services;

/// <summary>
/// Plays one song at a time for previews. BRSTMs are decoded on a background thread (a few hundred ms for a
/// typical song), and the last few decoded songs are kept so replaying is instant. Playback loops like the game.
/// </summary>
public sealed partial class PreviewPlayer : ObservableObject, IDisposable
{
    private const int CacheSize = 4;
    private readonly LinkedList<(string File, PcmAudio Audio, LoopPoints? Loop)> _cache = new();
    private WaveOutEvent? _output;
    private AudioOps.PcmSampleProvider? _source;
    private VolumeSampleProvider? _volume;
    private int _request;

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _nowPlaying;
    [ObservableProperty] private string? _currentFile;

    public TimeSpan Position => _source is null ? TimeSpan.Zero : TimeSpan.FromSeconds((double)_source.Position / _source.WaveFormat.SampleRate);
    public TimeSpan Duration { get; private set; }

    /// <summary>Game volume (0-127ish, 80 = normal) mapped to preview gain, so louder entries sound louder here too.</summary>
    public static float GainFor(int gameVolume) => Math.Clamp(gameVolume / 100f, 0f, 1.25f);

    public async Task PlayAsync(string brstmFile, string title, int gameVolume)
    {
        Stop();
        int request = ++_request;
        IsLoading = true;
        NowPlaying = title;
        CurrentFile = brstmFile;
        try
        {
            var (audio, loop) = await GetAsync(brstmFile);
            if (request != _request) return; // something else was played or stopped meanwhile
            _source = new AudioOps.PcmSampleProvider(audio, loop);
            _volume = new VolumeSampleProvider(_source) { Volume = GainFor(gameVolume) };
            Duration = audio.Duration;
            _output = new WaveOutEvent { DesiredLatency = 150 };
            _output.Init(_volume);
            _output.Play();
            IsPlaying = true;
        }
        catch (Exception ex)
        {
            if (request == _request) { NowPlaying = null; CurrentFile = null; }
            throw new AudioImportException($"\"{title}\" couldn't be played: {ex.Message}", ex);
        }
        finally
        {
            if (request == _request) IsLoading = false;
        }
    }

    public void SetVolume(int gameVolume)
    {
        if (_volume is not null) _volume.Volume = GainFor(gameVolume);
    }

    public void Stop()
    {
        _request++;
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _source = null;
        _volume = null;
        IsPlaying = false;
        IsLoading = false;
        NowPlaying = null;
        CurrentFile = null;
    }

    private async Task<(PcmAudio, LoopPoints?)> GetAsync(string file)
    {
        var hit = _cache.FirstOrDefault(c => string.Equals(c.File, file, StringComparison.OrdinalIgnoreCase));
        if (hit.Audio is not null) return (hit.Audio, hit.Loop);
        var (audio, loop) = await Task.Run(() => BrstmFile.Decode(file));
        _cache.AddFirst((file, audio, loop));
        while (_cache.Count > CacheSize) _cache.RemoveLast();
        return (audio, loop);
    }

    public void Dispose() => Stop();
}
