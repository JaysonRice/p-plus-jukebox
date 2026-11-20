using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.Core.Audio;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>
/// The player strip above the song library: a simple timeline of the selected song (loop shown, not editable),
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
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TimeText), nameof(LoopText), nameof(Length))] private PcmAudio? _audio;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TimeText))] private int _playhead;

    public bool HasSong => Title is not null;
    public bool HasLoop => _loop is not null;
    public int LoopStart => _loop?.Start ?? 0;
    public int LoopEnd => _loop?.End ?? Length;
    public int Length => Audio?.Length ?? 0;
    public bool IsPlayingHere => _playingHere;
    public string PlayPauseText => _playingHere ? "❚❚ Pause" : "▶ Play";

    public string TimeText => Audio is { } a ? $"{TimeText_(a.TimeOf(Playhead))} / {TimeText_(a.Duration)}" : "";
    public string LoopText => Audio is not { } a ? "" : _loop is { } l
        ? $"Loops from {TimeText_(a.TimeOf(l.Start))} to {TimeText_(a.TimeOf(l.End))}"
        : "Plays once (no loop)";

    private static string TimeText_(TimeSpan t) => Core.Audio.TimeText.Format(t);

    /// <summary>Shows a song (or nothing, for null). Stops it if this strip was playing the previous one.</summary>
    public async Task ShowAsync(string? file, string? title)
    {
        // Same song again (the list reloads when the tab is shown): keep it as it is, playing or not.
        if (file is not null && string.Equals(file, _file, StringComparison.OrdinalIgnoreCase) && (Audio is not null || IsLoading)) return;
        _file = file;
        if (_playingHere) _player.Stop();
        int request = ++_loadRequest;
        Title = title;
        Audio = null;
        Error = null;
        _loop = null;
        Playhead = 0;
        RefreshLoop();
        if (file is null) { IsLoading = false; return; }
        IsLoading = true;
        try
        {
            var (audio, loop) = await _player.LoadAsync(file);
            if (request != _loadRequest) return; // another song was clicked meanwhile
            _loop = loop;
            Audio = audio;
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

    private bool CanPlay => Audio is not null;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    public void TogglePlay()
    {
        if (Audio is not { } a) return;
        if (_playingHere)
        {
            Playhead = Math.Max(0, _player.PositionSamples);
            _player.Stop();
            return;
        }
        int from = Playhead >= a.Length - 1 ? 0 : Playhead;
        _player.PlayAudio(a, _loop, from, Title ?? "");
        _playingHere = true;
        _timer.Start();
        OnPropertyChanged(nameof(IsPlayingHere));
        OnPropertyChanged(nameof(PlayPauseText));
    }

    /// <summary>Clicking or dragging the timeline.</summary>
    public void SeekTo(int sample)
    {
        if (Audio is not { } a) return;
        sample = Math.Clamp(sample, 0, a.Length - 1);
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
