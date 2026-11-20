using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.Core.Audio;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>
/// The player strip above the song library: the selected song's waveform as a timeline (loop shown, not editable),
/// Play/Pause, and click-or-drag to skip around. Plays like the game: after the loop end it jumps to the loop start.
/// </summary>
public sealed partial class SongPreviewViewModel : ObservableObject
{
    private readonly PreviewPlayer _player;
    private readonly DispatcherTimer _timer;
    private bool _playingHere;
    private int _loadRequest;
    private LoopPoints? _loop;
    private string? _file;

    public SongPreviewViewModel(PreviewPlayer player)
    {
        _player = player;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => { if (_playingHere && _player.PositionSamples >= 0) Playhead = _player.PositionSamples; };
        _player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewPlayer.IsPlaying) && !_player.IsPlaying && _playingHere) StoppedHere();
        };
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSong))] private string? _title;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TimeText), nameof(LoopText))] private WaveformData? _waveform;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TimeText))] private int _playhead;
    [ObservableProperty] private double _viewStart;
    [ObservableProperty] private double _viewLength = 1;

    public bool HasSong => Title is not null;
    public bool HasLoop => _loop is not null;
    public int LoopStart => _loop?.Start ?? 0;
    public int LoopEnd => _loop?.End ?? Waveform?.Length ?? 0;
    public bool IsPlayingHere => _playingHere;
    public string PlayPauseText => _playingHere ? "❚❚ Pause" : "▶ Play";

    public string TimeText => Waveform is { } w ? $"{TimeText_(w.Audio.TimeOf(Playhead))} / {TimeText_(w.Audio.Duration)}" : "";
    public string LoopText => Waveform is not { } w ? "" : _loop is { } l
        ? $"Loops from {TimeText_(w.Audio.TimeOf(l.Start))} to {TimeText_(w.Audio.TimeOf(l.End))}"
        : "Plays once (no loop)";

    private static string TimeText_(TimeSpan t) => Core.Audio.TimeText.Format(t);

    /// <summary>Shows a song (or nothing, for null). Stops it if this strip was playing the previous one.</summary>
    public async Task ShowAsync(string? file, string? title)
    {
        // Same song again (the list reloads when the tab is shown): keep it as it is, playing or not.
        if (file is not null && string.Equals(file, _file, StringComparison.OrdinalIgnoreCase) && (Waveform is not null || IsLoading)) return;
        _file = file;
        if (_playingHere) _player.Stop();
        int request = ++_loadRequest;
        Title = title;
        Waveform = null;
        Error = null;
        _loop = null;
        Playhead = 0;
        RefreshLoop();
        if (file is null) { IsLoading = false; return; }
        IsLoading = true;
        try
        {
            var (audio, loop) = await _player.LoadAsync(file);
            var wave = await Task.Run(() => new WaveformData(audio));
            if (request != _loadRequest) return; // another song was clicked meanwhile
            _loop = loop;
            Waveform = wave;
            ViewStart = 0;
            ViewLength = Math.Max(1, wave.Length);
            RefreshLoop();
        }
        catch (Exception ex) when (request == _loadRequest)
        {
            Error = $"This song couldn't be opened: {ex.Message}";
        }
        finally
        {
            if (request == _loadRequest) IsLoading = false;
            TogglePlayCommand.NotifyCanExecuteChanged();
        }
    }

    private void RefreshLoop()
    {
        OnPropertyChanged(nameof(HasLoop));
        OnPropertyChanged(nameof(LoopStart));
        OnPropertyChanged(nameof(LoopEnd));
        OnPropertyChanged(nameof(LoopText));
    }

    private bool CanPlay => Waveform is not null;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    public void TogglePlay()
    {
        if (Waveform is not { } w) return;
        if (_playingHere)
        {
            Playhead = Math.Max(0, _player.PositionSamples);
            _player.Stop();
            return;
        }
        int from = Playhead >= w.Length - 1 ? 0 : Playhead;
        _player.PlayAudio(w.Audio, _loop, from, Title ?? "");
        _playingHere = true;
        _timer.Start();
        OnPropertyChanged(nameof(IsPlayingHere));
        OnPropertyChanged(nameof(PlayPauseText));
    }

    /// <summary>Clicking or dragging the timeline.</summary>
    public void SeekTo(int sample)
    {
        if (Waveform is not { } w) return;
        sample = Math.Clamp(sample, 0, w.Length - 1);
        if (_playingHere) _player.Seek(sample);
        Playhead = sample;
    }

    private void StoppedHere()
    {
        _playingHere = false;
        _timer.Stop();
        OnPropertyChanged(nameof(IsPlayingHere));
        OnPropertyChanged(nameof(PlayPauseText));
    }

    public void Stop()
    {
        if (_playingHere) _player.Stop();
    }
}
