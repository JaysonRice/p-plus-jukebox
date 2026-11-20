using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.App.Views;
using PPlusTracklistEditor.Core.Audio;
using PPlusTracklistEditor.Core.Library;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>
/// The "Make a looping song" window: open an audio file, pick where the loop starts and ends (by dragging, typing,
/// or pressing "Set to now" while it plays), listen to the seam with "Test the loop", then make the BRSTM.
/// The made file goes to a temp folder; the stage editor queues it and the library copies it in on Save.
/// </summary>
public sealed partial class LoopEditorViewModel : ObservableObject, IDisposable
{
    /// <summary>The loop must be at least this long (a shorter one is almost certainly a mis-drag).</summary>
    private const double MinLoopSeconds = 0.5;
    private readonly PreviewPlayer _player;
    private readonly DispatcherTimer _timer;
    private bool _playingHere;

    public LoopEditorViewModel(string file, IReadOnlyList<string> seriesFolders, PreviewPlayer player)
    {
        SourceFile = file;
        SeriesOptions = seriesFolders;
        _player = player;
        _title = SongFileNames.TitleFromFileName(file);
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
        _player.PropertyChanged += OnPlayerChanged;
    }

    public string SourceFile { get; }
    public string FileName => Path.GetFileName(SourceFile);
    public IReadOnlyList<string> SeriesOptions { get; }

    // ---- loading ----

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _loadError;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsReady))] private WaveformData? _waveform;
    private PcmAudio? Audio => Waveform?.Audio;
    public bool IsReady => Waveform is not null && !IsWorking;

    public async Task LoadAsync()
    {
        try
        {
            var imported = await Task.Run(() => AudioImport.Load(SourceFile));
            var wave = await Task.Run(() => new WaveformData(imported.Audio));
            var loop = LoopTools.DefaultLoop(imported);
            _loopStart = loop.Start;
            _loopEnd = loop.End;
            Waveform = wave;
            Playhead = 0;
            RefreshLoop();
            RefreshSize();
            OnPropertyChanged(nameof(SourceRateText));
            OnPropertyChanged(nameof(CanKeepOriginalRate));
            OnPropertyChanged(nameof(DurationText));
            NotifyCommands();
        }
        catch (AudioImportException ex) { LoadError = ex.Message; }
        catch (Exception ex) { LoadError = $"\"{FileName}\" couldn't be opened: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    public string DurationText => Audio is { } a ? $"Length {TimeText.Format(a.Duration)}" : "";

    // ---- song details ----

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _series = "";

    // ---- the loop ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LoopHint))] private bool _loops = true;
    [ObservableProperty] private bool _snapToZero = true;
    private int _loopStart, _loopEnd;
    public int LoopStart => _loopStart;
    public int LoopEnd => _loopEnd;
    public LoopPoints? Loop => Loops && Audio is not null ? new LoopPoints(_loopStart, _loopEnd) : null;

    public string LoopHint => Loops
        ? "When the music reaches the loop end, it jumps back to the loop start and keeps going. Anything after the loop end is cut off."
        : "The song plays once, then the stage is quiet.";

    partial void OnLoopsChanged(bool value)
    {
        if (_playingHere) _player.SetLoop(Loop);
        RefreshSize();
        NotifyCommands();
    }

    /// <summary>Moves a marker (dragged, typed, nudged), keeping start before end and snapping if that's on.</summary>
    public void MoveMarker(LoopMarker marker, int sample, bool snap = true)
    {
        if (Audio is not { } audio) return;
        int min = (int)(MinLoopSeconds * audio.SampleRate);
        sample = Math.Clamp(sample, 0, audio.Length);
        if (snap && SnapToZero) sample = LoopTools.SnapToZeroCrossing(audio, sample, LoopTools.SnapRadius(audio.SampleRate));
        if (marker == LoopMarker.Start)
        {
            _loopStart = Math.Clamp(sample, 0, Math.Max(0, _loopEnd - min));
            if (_suggestionsFor != _loopStart) SuggestionText = null; // old suggestions were for the old start
        }
        else _loopEnd = Math.Clamp(sample, Math.Min(audio.Length, _loopStart + min), audio.Length);
        if (_playingHere) _player.SetLoop(Loop);
        RefreshLoop();
        RefreshSize();
    }

    private void RefreshLoop()
    {
        OnPropertyChanged(nameof(LoopStart));
        OnPropertyChanged(nameof(LoopEnd));
        OnPropertyChanged(nameof(LoopStartText));
        OnPropertyChanged(nameof(LoopEndText));
        OnPropertyChanged(nameof(LoopStartDetail));
        OnPropertyChanged(nameof(LoopEndDetail));
        OnPropertyChanged(nameof(LoopLengthText));
    }

    /// <summary>Typed times. Bad input is ignored and the box goes back to the current time.</summary>
    public string LoopStartText
    {
        get => Audio is { } a ? TimeText.Format(a.TimeOf(_loopStart)) : "";
        set => SetFromText(LoopMarker.Start, value);
    }

    public string LoopEndText
    {
        get => Audio is { } a ? TimeText.Format(a.TimeOf(_loopEnd)) : "";
        set => SetFromText(LoopMarker.End, value);
    }

    private void SetFromText(LoopMarker marker, string text)
    {
        if (Audio is { } a && TimeText.TryParse(text, out var t)) MoveMarker(marker, a.SampleAt(t));
        else RefreshLoop();
    }

    public string LoopStartDetail => $"sample {_loopStart:N0}";
    public string LoopEndDetail => $"sample {_loopEnd:N0}";
    public string LoopLengthText => Audio is { } a ? $"Loop length {TimeText.Format(a.TimeOf(_loopEnd - _loopStart))}" : "";

    [RelayCommand(CanExecute = nameof(CanEditLoop))] private void StartEarlier() => Step(LoopMarker.Start, -1);
    [RelayCommand(CanExecute = nameof(CanEditLoop))] private void StartLater() => Step(LoopMarker.Start, +1);
    [RelayCommand(CanExecute = nameof(CanEditLoop))] private void EndEarlier() => Step(LoopMarker.End, -1);
    [RelayCommand(CanExecute = nameof(CanEditLoop))] private void EndLater() => Step(LoopMarker.End, +1);

    private DateTime _lastStepAt;
    private (LoopMarker, int) _lastStepKey;
    private int _stepRepeat;

    /// <summary>
    /// The fine-tune arrows (hold to repeat). A click moves to the next zero crossing with snapping on, else by 1 ms.
    /// Holding speeds up gradually (up to ~25 ms per step), so small fixes stay precise but longer moves don't take forever.
    /// Also zooms in on the marker so the change is visible.
    /// </summary>
    private void Step(LoopMarker marker, int direction)
    {
        if (Audio is not { } a) return;
        var now = DateTime.UtcNow;
        bool held = _lastStepKey == (marker, direction) && (now - _lastStepAt).TotalMilliseconds < 250;
        _stepRepeat = held ? _stepRepeat + 1 : 0;
        _lastStepAt = now;
        _lastStepKey = (marker, direction);

        double extraMs = _stepRepeat < 6 ? 0 : Math.Min(25, (_stepRepeat - 5) * 1.0);
        int from = marker == LoopMarker.Start ? _loopStart : _loopEnd;
        int target = from + direction * (int)(extraMs * a.SampleRate / 1000);
        int to = SnapToZero ? LoopTools.StepToZeroCrossing(a, target, direction) : target + direction * Math.Max(1, a.SampleRate / 1000);
        MoveMarker(marker, to, snap: false);
        KeepInCloseView(marker == LoopMarker.Start ? _loopStart : _loopEnd);
    }

    // ---- "Suggest a loop end" ----

    private IReadOnlyList<LoopSuggestion>? _suggestions;
    private int _suggestionsFor = -1, _suggestionIndex = -1;

    [ObservableProperty] private bool _isSuggesting;
    [ObservableProperty] private string? _suggestionText;

    private bool CanSuggest => HasAudio && !IsSuggesting;

    /// <summary>
    /// Finds where the music after the loop start comes around again and moves the loop end there. Pressing it again
    /// goes to the next suggestion. Nothing is saved; it only moves the marker.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSuggest))]
    private async Task SuggestEnd()
    {
        if (Audio is not { } a) return;
        if (_suggestions is null || _suggestionsFor != _loopStart)
        {
            IsSuggesting = true;
            SuggestEndCommand.NotifyCanExecuteChanged();
            SuggestionText = "Listening for where the music repeats…";
            int start = _loopStart;
            try { _suggestions = await Task.Run(() => LoopFinder.SuggestEnds(a, start)); }
            finally
            {
                IsSuggesting = false;
                SuggestEndCommand.NotifyCanExecuteChanged();
            }
            _suggestionsFor = start;
            _suggestionIndex = -1;
        }

        if (_suggestions.Count == 0 || _suggestions[0].Match < LoopFinder.GoodMatch)
        {
            SuggestionText = "Couldn't find a place where the music after the loop start plays again. The file may already end where it should loop, or the loop start may need moving.";
            return;
        }
        _suggestionIndex = (_suggestionIndex + 1) % _suggestions.Count;
        var s = _suggestions[_suggestionIndex];
        Loops = true;
        MoveMarker(LoopMarker.End, s.End, snap: false); // already lined up to the waveform, which beats a zero crossing here
        ZoomAround(_loopEnd, 4.0 * a.SampleRate);
        SuggestionText = $"Suggestion {_suggestionIndex + 1} of {_suggestions.Count}: {s.Match:P0} match. " +
                         (_suggestions.Count > 1 ? "Press again to try the next one. " : "") + "Use \"Test the loop\" to hear it.";
    }

    /// <summary>Zooms to ~1 second around <paramref name="sample"/> unless the view is already that close and shows it.</summary>
    private void KeepInCloseView(int sample)
    {
        if (Audio is not { } a) return;
        double close = 1.0 * a.SampleRate;
        double margin = ViewLength * 0.1;
        bool visible = sample >= ViewStart + margin && sample <= ViewStart + ViewLength - margin;
        if (ViewLength > close * 2 || !visible) ZoomAround(sample, close);
    }

    /// <summary>
    /// "Set to blue line": put the marker at the playback position. While playing, a little is taken off for the time it
    /// takes to react and press; while paused it's exactly where the line is.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasAudio))] private void StartAtPlayhead() => MarkAtPlayhead(LoopMarker.Start);
    [RelayCommand(CanExecute = nameof(HasAudio))] private void EndAtPlayhead() => MarkAtPlayhead(LoopMarker.End);

    private void MarkAtPlayhead(LoopMarker marker)
    {
        if (Audio is not { } a) return;
        Loops = true; // marking a loop point means they want a loop
        int position = _playingHere ? _player.PositionSamples : Playhead;
        if (position < 0) return;
        // ~150 ms of output buffer plus a typical reaction time: people press a little after the moment they mean.
        if (_playingHere) position -= (int)(0.25 * a.SampleRate);
        MoveMarker(marker, position);
    }

    // ---- view (zoom) ----

    [ObservableProperty] private double _viewStart;
    [ObservableProperty] private double _viewLength = 1;

    [RelayCommand(CanExecute = nameof(HasAudio))]
    private void ZoomAll()
    {
        if (Audio is not { } a) return;
        ViewStart = 0;
        ViewLength = a.Length;
    }

    [RelayCommand(CanExecute = nameof(HasAudio))] private void ZoomToStart() => ZoomAround(_loopStart, 4.0 * (Audio?.SampleRate ?? 0));
    [RelayCommand(CanExecute = nameof(HasAudio))] private void ZoomToEnd() => ZoomAround(_loopEnd, 4.0 * (Audio?.SampleRate ?? 0));

    /// <summary>Shows <paramref name="samples"/> of audio centered on <paramref name="sample"/>.</summary>
    private void ZoomAround(int sample, double samples)
    {
        if (Audio is not { } a) return;
        double length = Math.Clamp(samples, 200, a.Length);
        ViewLength = length;
        ViewStart = Math.Clamp(sample - length / 2, 0, a.Length - length);
    }

    // ---- playback ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PlayheadText))] private int _playhead = -1;
    public bool IsPlayingHere => _playingHere;
    public string PlayheadText => Audio is { } a && Playhead >= 0 ? TimeText.Format(a.TimeOf(Playhead)) : "";

    private bool HasAudio => Audio is not null && !IsWorking;
    private bool CanEditLoop => HasAudio && Loops;

    [RelayCommand(CanExecute = nameof(HasAudio))] private void PlayFromBeginning() => PlayFrom(0);
    [RelayCommand(CanExecute = nameof(CanEditLoop))] private void PlayFromLoopStart() => PlayFrom(_loopStart);

    /// <summary>Plays the last few seconds before the loop end, so you hear the jump back to the loop start.</summary>
    [RelayCommand(CanExecute = nameof(CanEditLoop))]
    private void TestLoop()
    {
        if (Audio is not { } a) return;
        PlayFrom(LoopTools.TestStart(new LoopPoints(_loopStart, _loopEnd), a.SampleRate));
    }

    public string PlayPauseText => _playingHere ? "❚❚ Pause" : "▶ Play";

    /// <summary>Play button / Space bar: pause (the blue line stays where it stopped), or play on from the blue line.</summary>
    [RelayCommand(CanExecute = nameof(HasAudio))]
    public void TogglePlay()
    {
        if (_playingHere) { Playhead = Math.Max(0, _player.PositionSamples); _player.Stop(); }
        else if (HasAudio) PlayFrom(Math.Max(0, Playhead));
    }

    public void Pause()
    {
        if (_playingHere) TogglePlay();
    }

    /// <summary>Clicking the waveform: jumps there while playing, or just moves the blue line while paused.</summary>
    public void SeekTo(int sample)
    {
        if (_playingHere) _player.Seek(sample);
        else Playhead = sample;
        OnPropertyChanged(nameof(PlayheadText));
    }

    private void PlayFrom(int sample)
    {
        if (Audio is not { } a) return;
        if (sample >= a.Length - 1) sample = 0; // finished last time: start over
        // Starting past the loop end would play to the end of the song first; the game never gets there.
        if (Loop is { } l && sample >= l.End) sample = l.Start;
        _player.PlayAudio(a, Loop, sample, Title);
        _playingHere = true;
        _timer.Start();
        NotifyCommands();
        Tick();
    }

    private void Tick()
    {
        int pos = _playingHere ? _player.PositionSamples : -1;
        if (pos >= 0) Playhead = pos;
        OnPropertyChanged(nameof(PlayheadText));
    }

    private void OnPlayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Stopped, or something else started playing (the player is shared with the main window).
        if (e.PropertyName == nameof(PreviewPlayer.IsPlaying) && !_player.IsPlaying && _playingHere)
        {
            _playingHere = false;
            _timer.Stop();
            NotifyCommands();
        }
    }

    // ---- output options ----

    [ObservableProperty] private bool _keepOriginalRate;
    [ObservableProperty] private bool _normalize;
    [ObservableProperty] private string _sizeText = "";

    partial void OnKeepOriginalRateChanged(bool value) => RefreshSize();

    public bool CanKeepOriginalRate => Audio is { } a && a.SampleRate > BrstmFile.GameSampleRate;
    public string SourceRateText => Audio is { } a
        ? $"Keep the original sound quality ({a.SampleRate:N0} Hz instead of {BrstmFile.GameSampleRate:N0} Hz). Makes the file bigger; most players won't hear a difference."
        : "";

    private BrstmEncodeOptions Options => new(KeepOriginalRate ? null : BrstmFile.GameSampleRate, Normalize);

    private void RefreshSize()
    {
        if (Audio is not { } a) return;
        double mb = BrstmFile.EstimateSize(a, Loop, Options) / (1024.0 * 1024.0);
        SizeText = $"File size about {mb:0.0} MB";
    }

    // ---- making the song ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsReady))] private bool _isWorking;
    [ObservableProperty] private double _progress;

    /// <summary>The finished .brstm (in a temp folder), set when <see cref="MakeAsync"/> succeeds.</summary>
    public string? OutputFile { get; private set; }

    /// <summary>Encodes in the background. Returns an error message to show, or null on success.</summary>
    public async Task<string?> MakeAsync()
    {
        if (Audio is not { } audio) return "The song isn't loaded yet.";
        if (Title.Trim().Length == 0) return "Please give the song a title.";
        try { SongFileNames.UniqueSongPath(_ => false, Series, Title); } // same checks the library will do on Save
        catch (MusicLibraryException ex) { return ex.Message; }

        _player.Stop();
        IsWorking = true;
        NotifyCommands();
        Progress = 0;
        var loop = Loop;
        var options = Options;
        var progress = new Progress<double>(p => Progress = p);
        try
        {
            OutputFile = await Task.Run(() =>
            {
                var result = BrstmFile.Encode(audio, loop, options, progress);
                string path = NewSongFiles.NewPath();
                File.WriteAllBytes(path, result.Bytes);
                return path;
            });
            return null;
        }
        catch (Exception ex) { return $"The song couldn't be made: {ex.Message}"; }
        finally
        {
            IsWorking = false;
            NotifyCommands();
        }
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(IsPlayingHere));
        OnPropertyChanged(nameof(PlayPauseText));
        OnPropertyChanged(nameof(IsReady));
        foreach (var c in new IRelayCommand[]
                 {
                     StartEarlierCommand, StartLaterCommand, EndEarlierCommand, EndLaterCommand, StartAtPlayheadCommand,
                     EndAtPlayheadCommand, ZoomAllCommand, ZoomToStartCommand, ZoomToEndCommand, PlayFromBeginningCommand,
                     PlayFromLoopStartCommand, TestLoopCommand, TogglePlayCommand, SuggestEndCommand,
                 })
            c.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _timer.Stop();
        _player.PropertyChanged -= OnPlayerChanged;
        if (_playingHere) _player.Stop();
    }
}

/// <summary>
/// Where made songs wait until the stage is saved (the library copies them in then). Files from earlier runs
/// that are more than a day old are cleaned up at startup; a day keeps a second copy of the app that's still open safe.
/// </summary>
public static class NewSongFiles
{
    public static string Folder => Path.Combine(Path.GetTempPath(), "PPlusTracklistEditor", "NewSongs");

    public static string NewPath()
    {
        Directory.CreateDirectory(Folder);
        return Path.Combine(Folder, $"{Guid.NewGuid():N}.brstm");
    }

    public static void CleanUpOld()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var f in Directory.GetFiles(Folder, "*.brstm"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
