using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.App.Views;
using PPlusTracklistEditor.Core.Audio;
using PPlusTracklistEditor.Core.Library;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>
/// The right-hand panel: one stage of one build, being edited. Wraps a <see cref="StageEditSession"/> (nothing is
/// written until Save) plus an <see cref="EditHistory"/> for Undo/Redo.
/// </summary>
public sealed partial class StageEditorViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public StageEditorViewModel(MainViewModel main, StageEditSession session)
    {
        _main = main;
        Session = session;
        History = new EditHistory(session);
        Preview = new SongPreviewViewModel(main.Player);
        // For the live "your songs" total: the other stages' songs and Project+'s songs don't change while this one is edited.
        _projectPlusSongs = SongSpace.ProjectPlusSongs(session.Build.Library);
        foreach (string name in session.Build.TracklistNames().Where(n => !string.Equals(n, session.StageName, StringComparison.OrdinalIgnoreCase)))
            foreach (var e in Core.Tlst.TlstFile.Load(session.Build.TracklistPath(name)).Entries)
                if (e.Path is not null) _otherStagesSongs.Add(SongStore.Normalize(e.Path));
        Rebuild(select: 0); // first song selected, so its settings are visible right away
    }

    public StageEditSession Session { get; }
    public EditHistory History { get; }
    public PreviewPlayer Player => _main.Player;
    /// <summary>The player bar under the song list: the selected song's timeline, Play/Pause, skip around.</summary>
    public SongPreviewViewModel Preview { get; }
    public Window? Owner => Application.Current?.MainWindow;

    public string StageTitle => Session.DisplayName;
    public string BuildName => Session.Build.Name;
    public bool IsDirty => Session.IsDirty;
    public bool CanResetToDefault => Session.CanResetToDefault;

    private readonly HashSet<string> _projectPlusSongs;
    private readonly HashSet<string> _otherStagesSongs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Your songs in this build: 91 songs, 388 MB", including this stage's unsaved changes.</summary>
    public string BuildSpaceText
    {
        get
        {
            var songs = _otherStagesSongs.Concat(Session.Tracklist.Entries.Where(e => e.Path is not null).Select(e => SongStore.Normalize(e.Path!)));
            return $"Your songs in this build: {SongSpace.Of(Session.Build.Library, songs, _projectPlusSongs).Text}";
        }
    }

    public ObservableCollection<SongRowViewModel> Songs { get; } = [];
    public ObservableCollection<Problem> Problems { get; } = [];
    public bool HasErrors => Problems.Any(p => p.Severity == ProblemSeverity.Error);
    public bool HasNoProblems => Problems.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(PlayCommand))]
    private SongRowViewModel? _selectedSong;

    public bool HasSelection => SelectedSong is not null;

    partial void OnSelectedSongChanged(SongRowViewModel? value)
    {
        History.Break();
        string? why = value is null || value.FilePath is not null ? null
            : value.IsDiscSong ? "This is one of Brawl's own songs, played from the game disc, so it can't be played here."
            : "This song's file is missing, so it can't be played.";
        _ = Preview.ShowAsync(value?.FilePath, value?.DisplayTitle, why, value?.Volume ?? 80);
    }

    // ---- state refresh ----

    /// <summary>Rebuilds the rows from the session (after add/remove/move/undo), keeping the selection by position.</summary>
    private void Rebuild(int? select = null)
    {
        int keep = select ?? SelectedSong?.Index ?? -1;
        Songs.Clear();
        var entries = Session.Tracklist.Entries;
        for (int i = 0; i < entries.Count; i++) Songs.Add(new SongRowViewModel(this, entries[i], i));
        SelectedSong = keep >= 0 && Songs.Count > 0 ? Songs[Math.Min(keep, Songs.Count - 1)] : null;
        Revalidate();
    }

    private void Revalidate()
    {
        Problems.Clear();
        foreach (var p in Session.Validate()) Problems.Add(p);
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasNoProblems));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(BuildSpaceText));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        _main.OnEditorStateChanged(this);
    }

    internal void AfterRowEdit()
    {
        Revalidate();
        foreach (var row in Songs) row.RefreshAll(); // "how often" chances depend on every song
    }

    internal void OnVolumeChanged(SongRowViewModel row)
    {
        if (row == SelectedSong) Preview.SetVolume(row.Volume); // a playing song gets louder or quieter right away
    }

    // ---- editing commands ----

    [RelayCommand]
    private void AddFromLibrary()
    {
        var window = new LibraryWindow(new LibraryViewModel(_main.Library, _main.Player, pickForStage: Session.DisplayName) { CurrentBuildName = Session.Build.Name }) { Owner = Owner };
        if (window.ShowDialog() != true) return;
        AddLibrarySongs(window.PickedSongs);
    }

    /// <summary>
    /// Adds songs that are already in the library to this stage (unsaved until Save). Songs the stage already has are
    /// skipped and reported. Used by "Add from your library" and by "Add to a stage" in the Song Library tab.
    /// </summary>
    public void AddLibrarySongs(IReadOnlyList<string> songPaths)
    {
        var have = Session.Tracklist.Entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = songPaths.Where(p => !have.Contains(p)).ToList();
        if (fresh.Count < songPaths.Count)
            Dialogs.Info(Owner, fresh.Count == 0
                ? $"{(songPaths.Count == 1 ? "That song is" : "Those songs are")} already on {StageTitle}."
                : $"{songPaths.Count - fresh.Count} of the songs {(songPaths.Count - fresh.Count == 1 ? "was" : "were")} already on {StageTitle} and {(songPaths.Count - fresh.Count == 1 ? "wasn't" : "weren't")} added again.");
        if (fresh.Count == 0) return;
        History.BeforeChange();
        try { foreach (var path in fresh) Session.AddExistingSong(path); }
        catch (MusicLibraryException ex) { Dialogs.Error(Owner, ex.Message); }
        Rebuild(Session.Tracklist.Entries.Count - 1);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (SelectedSong is not { } row) return;
        History.BeforeChange();
        Session.Remove(row.Entry);
        Rebuild(row.Index);
    }

    private bool CanMoveUp => SelectedSong is { Index: > 0 };
    private bool CanMoveDown => SelectedSong is { } s && s.Index < Songs.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(+1);

    private void Move(int delta)
    {
        if (SelectedSong is not { } row) return;
        History.BeforeChange();
        Session.Move(row.Index, row.Index + delta);
        Rebuild(row.Index + delta);
    }

    [RelayCommand]
    private void ResetToDefault()
    {
        if (!Dialogs.Confirm(Owner, $"Replace this stage's songs with the ones it has in Project+?\n\nYou can still undo this, or discard it instead of saving.")) return;
        History.BeforeChange();
        Session.ResetToDefault();
        Rebuild(0);
    }

    private bool CanUndo => History.CanUndo;
    private bool CanRedo => History.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() { if (History.Undo()) Rebuild(); }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() { if (History.Redo()) Rebuild(); }

    private bool CanSave => IsDirty;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => TrySave();

    /// <summary>Saves. Returns false (and explains why) if the stage has errors.</summary>
    public bool TrySave()
    {
        if (HasErrors)
        {
            Dialogs.Error(Owner, "This stage can't be saved yet:\n\n" +
                string.Join("\n", Problems.Where(p => p.Severity == ProblemSeverity.Error).Select(p => "• " + p.Message)));
            return false;
        }
        Session.Save();
        History.Clear();
        Rebuild();
        _main.OnStageSaved(this);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Discard()
    {
        if (!Dialogs.Confirm(Owner, "Throw away your unsaved changes to this stage?")) return;
        _main.ReopenStage();
    }

    // ---- playback ----

    private bool CanPlay => SelectedSong is { FilePath: not null };

    /// <summary>Space bar / Play button: toggles the selected song.</summary>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play() => Preview.TogglePlay(); // same as the player bar's Play/Pause

    /// <summary>Page Up / Page Down.</summary>
    public void SelectRelative(int delta)
    {
        if (Songs.Count == 0) return;
        int i = SelectedSong is null ? 0 : Math.Clamp(SelectedSong.Index + delta, 0, Songs.Count - 1);
        SelectedSong = Songs[i];
    }
}
