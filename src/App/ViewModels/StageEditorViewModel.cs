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
        Rebuild(select: 0); // first song selected, so its settings are visible right away
    }

    public StageEditSession Session { get; }
    public EditHistory History { get; }
    public PreviewPlayer Player => _main.Player;
    public Window? Owner => Application.Current?.MainWindow;

    public string StageTitle => Session.DisplayName;
    public string BuildName => Session.Build.Name;
    public bool IsDirty => Session.IsDirty;
    public bool CanResetToDefault => Session.CanResetToDefault;

    public ObservableCollection<SongRowViewModel> Songs { get; } = [];
    public ObservableCollection<Problem> Problems { get; } = [];
    public bool HasErrors => Problems.Any(p => p.Severity == ProblemSeverity.Error);
    public bool HasNoProblems => Problems.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(PlayCommand))]
    private SongRowViewModel? _selectedSong;

    public bool HasSelection => SelectedSong is not null;

    partial void OnSelectedSongChanged(SongRowViewModel? value) => History.Break();

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
        if (Player.IsPlaying && row.FilePath is not null && string.Equals(Player.CurrentFile, row.FilePath, StringComparison.OrdinalIgnoreCase))
            Player.SetVolume(row.Volume);
    }

    // ---- editing commands ----

    [RelayCommand]
    private void AddFromLibrary()
    {
        var window = new LibraryWindow(new LibraryViewModel(_main.Library, _main.Player, pickForStage: Session.DisplayName)) { Owner = Owner };
        if (window.ShowDialog() != true) return;
        var picked = window.PickedSongs;
        if (picked.Count == 0) return;
        History.BeforeChange();
        foreach (var path in picked) Session.AddExistingSong(path);
        Rebuild(Session.Tracklist.Entries.Count - 1);
    }

    [RelayCommand]
    private void AddFromFile()
    {
        var files = Dialogs.PickFiles(Owner, "Choose song files",
            "Songs (*.brstm;*.wav;*.mp3;*.m4a;*.flac)|*.brstm;*.wav;*.mp3;*.m4a;*.flac;*.wma;*.aac;*.ogg|All files|*.*");
        AddFiles(files);
    }

    /// <summary>Files dropped on the song list or picked with "From a file". BRSTMs ask for a title and series.</summary>
    public void AddFiles(IEnumerable<string> files)
    {
        int added = 0;
        var notBrstm = new List<string>();
        foreach (var file in files)
        {
            if (!Path.GetExtension(file).Equals(".brstm", StringComparison.OrdinalIgnoreCase))
            {
                notBrstm.Add(Path.GetFileName(file));
                continue;
            }
            var dlg = new AddSongWindow(file, _main.Library.Songs.SeriesFolders().ToList(), _main.Player) { Owner = Owner };
            if (dlg.ShowDialog() != true) continue;
            if (added == 0) History.BeforeChange(); // one undo step for the whole drop
            try
            {
                Session.AddSong(file, dlg.SongTitle, dlg.Series);
                added++;
            }
            catch (MusicLibraryException ex) { Dialogs.Error(Owner, ex.Message); }
        }
        if (added > 0) Rebuild(Session.Tracklist.Entries.Count - 1);
        if (notBrstm.Count > 0)
            Dialogs.Info(Owner,
                "Turning WAV/MP3 files into looping songs is coming in the next update of this app. For now, only .brstm songs can be added:\n\n" +
                string.Join("\n", notBrstm.Select(n => "• " + n)));
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
        if (!Dialogs.Confirm(Owner, $"Replace this stage's songs with the ones from the Default build?\n\nYou can still undo this, or discard it instead of saving.")) return;
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
    private async Task Play()
    {
        if (SelectedSong is not { FilePath: { } file } row) return;
        if (Player.IsPlaying && string.Equals(Player.CurrentFile, file, StringComparison.OrdinalIgnoreCase)) { Player.Stop(); return; }
        await Player.PlayAsync(file, row.DisplayTitle, row.Volume);
    }

    /// <summary>Page Up / Page Down.</summary>
    public void SelectRelative(int delta)
    {
        if (Songs.Count == 0) return;
        int i = SelectedSong is null ? 0 : Math.Clamp(SelectedSong.Index + delta, 0, Songs.Count - 1);
        SelectedSong = Songs[i];
    }
}
