using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.App.Views;
using PPlusTracklistEditor.Core.Library;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>
/// Main window: build picker on top, stage list on the left, the selected stage's editor on the right.
/// Switching stage/build or closing with unsaved changes asks Save / Don't save / Cancel.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(AppLibrary library, bool libraryInFallbackLocation)
    {
        Library = library;
        LibraryNotice = libraryInFallbackLocation
            ? $"Your songs are kept in {library.Root} (the app's own folder can't be written to)."
            : $"Your songs are kept in {library.Root}";
        StagesView = CollectionViewSource.GetDefaultView(Stages);
        StagesView.Filter = o => o is StageCardViewModel s &&
            (string.IsNullOrWhiteSpace(SearchText) || s.DisplayName.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        ReloadBuilds(library.DefaultBuild.Id);
    }

    public AppLibrary Library { get; }
    public PreviewPlayer Player { get; } = new();
    public string LibraryNotice { get; }
    private static Window? Owner => Application.Current?.MainWindow;

    public ObservableCollection<Build> Builds { get; } = [];
    public ObservableCollection<StageCardViewModel> Stages { get; } = [];
    public ICollectionView StagesView { get; }

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => StagesView.Refresh();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(HasEditor))]
    private StageEditorViewModel? _editor;

    public bool HasEditor => Editor is not null;

    public string WindowTitle =>
        $"{(Editor?.IsDirty == true ? "• " : "")}{SelectedBuild?.Name ?? ""}{(Editor is null ? "" : " – " + Editor.StageTitle)} – {Dialogs.AppTitle}";

    // ---- build selection (guarded) ----

    private Build? _selectedBuild;
    public Build? SelectedBuild
    {
        get => _selectedBuild;
        set
        {
            if (value is null || value == _selectedBuild) return;
            if (!ConfirmLeaveStage()) { RevertLater(nameof(SelectedBuild)); return; }
            _selectedBuild = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(CanEditBuild));
            DeleteBuildCommand.NotifyCanExecuteChanged();
            LoadStages();
        }
    }

    public bool CanEditBuild => SelectedBuild is { IsDefault: false };

    private void ReloadBuilds(string selectId)
    {
        Builds.Clear();
        foreach (var b in Library.Builds()) Builds.Add(b);
        _selectedBuild = null;
        SelectedBuild = Builds.FirstOrDefault(b => b.Id == selectId) ?? Builds.FirstOrDefault();
    }

    private void LoadStages()
    {
        string? keep = _selectedStage?.Name;
        _selectedStage = null;
        Editor = null;
        Stages.Clear();
        if (SelectedBuild is null) return;
        foreach (var info in SelectedBuild.Stages()) Stages.Add(new StageCardViewModel(SelectedBuild, info));
        _selectedStage = Stages.FirstOrDefault(s => s.Name == keep);
        OnPropertyChanged(nameof(SelectedStage));
        if (_selectedStage is not null) OpenEditor();
    }

    // ---- stage selection (guarded) ----

    private StageCardViewModel? _selectedStage;
    public StageCardViewModel? SelectedStage
    {
        get => _selectedStage;
        set
        {
            if (value == _selectedStage) return;
            if (value is null) return; // the list clears its selection while filtering; keep the open stage
            if (!ConfirmLeaveStage()) { RevertLater(nameof(SelectedStage)); return; }
            _selectedStage = value;
            OnPropertyChanged();
            OpenEditor();
        }
    }

    private void OpenEditor()
    {
        if (_selectedStage is null || SelectedBuild is null) { Editor = null; return; }
        Player.Stop();
        Editor = new StageEditorViewModel(this, SelectedBuild.OpenStage(_selectedStage.Name));
    }

    /// <summary>After "Discard": reload the stage from disk.</summary>
    internal void ReopenStage()
    {
        OpenEditor();
        _selectedStage?.RefreshFromDisk();
    }

    /// <summary>The list already moved its selection; put it back after the current input event finishes.</summary>
    private void RevertLater(string property) =>
        Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged(property));

    /// <summary>True if it's OK to leave the current stage (saved, discarded, or nothing to save).</summary>
    public bool ConfirmLeaveStage()
    {
        if (Editor is not { IsDirty: true } editor) return true;
        switch (Dialogs.AskSave(Owner, $"\"{editor.StageTitle}\" in \"{editor.BuildName}\""))
        {
            case MessageBoxResult.Yes: return editor.TrySave();
            case MessageBoxResult.No:
                if (_selectedStage is not null) _selectedStage.HasUnsavedChanges = false;
                return true;
            default: return false;
        }
    }

    internal void OnEditorStateChanged(StageEditorViewModel editor)
    {
        if (editor != Editor && Editor is not null) return;
        if (_selectedStage is not null)
        {
            _selectedStage.HasUnsavedChanges = editor.IsDirty;
            _selectedStage.SongCount = editor.Songs.Count; // live, before saving
        }
        OnPropertyChanged(nameof(WindowTitle));
    }

    internal void OnStageSaved(StageEditorViewModel editor)
    {
        _selectedStage?.RefreshFromDisk();
        OnPropertyChanged(nameof(WindowTitle));
    }

    // ---- build commands ----

    [RelayCommand]
    private void NewBuild()
    {
        if (SelectedBuild is not { } from || !ConfirmLeaveStage()) return;
        string? name = Dialogs.PromptText(Owner, "New build",
            $"Name for the new build. It starts as a copy of \"{from.Name}\"; your songs are shared, not copied.", $"{from.Name} copy");
        if (string.IsNullOrWhiteSpace(name)) return;
        var build = Library.NewBuild(name.Trim(), from);
        ReloadBuilds(build.Id);
    }

    [RelayCommand]
    private void RenameBuild()
    {
        if (SelectedBuild is not { } build) return;
        string? name = Dialogs.PromptText(Owner, "Rename build", "New name for this build:", build.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == build.Name) return;
        build.Rename(name.Trim());
        ReloadBuilds(build.Id);
    }

    [RelayCommand(CanExecute = nameof(CanEditBuild))]
    private void DeleteBuild()
    {
        if (SelectedBuild is not { IsDefault: false } build) return;
        if (!Dialogs.Confirm(Owner, $"Delete the build \"{build.Name}\"?\n\nIts stage song lists are deleted. The songs themselves stay in your library.")) return;
        Editor = null; // its unsaved changes go with it
        Library.DeleteBuild(build);
        ReloadBuilds(Library.DefaultBuild.Id);
    }

    [RelayCommand]
    private void ImportMusicFolder()
    {
        if (!ConfirmLeaveStage()) return;
        string? folder = Dialogs.PickFolder(Owner, "Choose a music folder (with \"strm\" and \"tracklist\" inside)");
        if (folder is null) return;
        var source = MusicFolder.Open(folder);
        string? name = Dialogs.PromptText(Owner, "Import a music folder", "Name for the new build:", Path.GetFileName(folder.TrimEnd('\\')));
        if (string.IsNullOrWhiteSpace(name)) return;
        using (new WaitCursor())
        {
            var result = Library.ImportMusicFolder(source, name.Trim());
            ReloadBuilds(result.Build.Id);
            var notes = new List<string> { $"Imported \"{result.Build.Name}\"." };
            if (result.RenamedSongs > 0) notes.Add($"{result.RenamedSongs} songs had the same name as different songs already in your library, so they were renamed with \" (2)\".");
            if (result.FixedPaths > 0) notes.Add($"{result.FixedPaths} song paths written with \"\\\" were fixed to \"/\".");
            Dialogs.Info(Owner, string.Join("\n\n", notes));
        }
    }

    [RelayCommand]
    private void Sync()
    {
        if (SelectedBuild is not { } build || !ConfirmLeaveStage()) return;
        Player.Stop();
        new SyncWindow(new SyncViewModel(build)) { Owner = Owner }.ShowDialog();
    }

    [RelayCommand]
    private void OpenLibrary()
    {
        new LibraryWindow(new LibraryViewModel(Library, Player, pickForStage: null)) { Owner = Owner }.ShowDialog();
        Editor?.AfterRowEdit(); // a song may have been deleted; re-check
    }

    [RelayCommand]
    private void ShowLibraryInExplorer() => Dialogs.ShowInExplorer(Library.Root);

    [RelayCommand]
    private void StopPlayback() => Player.Stop();
}

/// <summary>Shows the busy cursor for a short synchronous job.</summary>
public sealed class WaitCursor : IDisposable
{
    private readonly System.Windows.Input.Cursor? _old = System.Windows.Input.Mouse.OverrideCursor;
    public WaitCursor() => System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
    public void Dispose() => System.Windows.Input.Mouse.OverrideCursor = _old;
}
