using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Sync;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>
/// "Put this build's music on my Project+": pick the folder (SD card, Project+ folder, Dolphin copy...),
/// see a plain summary of what will change, then go. Only the music folder (pf\sound) is touched.
/// </summary>
public sealed partial class SyncViewModel : ObservableObject
{
    public SyncViewModel(Build build)
    {
        Build = build;
        // The folder saved in Settings wins; otherwise where this build was last put. No folder picker if it's there.
        _knownFolder = build.Library.SyncFolderFor(build);
        _rememberFolder = build.Library.Info.SyncFolder is null || _knownFolder == build.Library.Info.SyncFolder;
        CheckKnownFolder();
    }

    private readonly string? _knownFolder;

    /// <summary>"Check again": the saved folder wasn't there (SD card not plugged in), try it again.</summary>
    [RelayCommand]
    private void CheckAgain() => CheckKnownFolder();

    [ObservableProperty] private bool _isKnownFolderMissing;

    private void CheckKnownFolder()
    {
        IsKnownFolderMissing = false;
        if (_knownFolder is { } folder && Directory.Exists(folder)) _ = ChooseFolderAsync(folder);
        else if (_knownFolder is { } missing)
        {
            Folder = missing;
            IsKnownFolderMissing = true;
            Summary = $"Your Project+ folder isn't there right now:\n{missing}\n\nIf it's on your SD card, plug the card in and press Check again. Or choose another folder with Browse.";
        }
        else Summary = "Choose your Project+ folder. It can be on your SD card or anywhere on your computer (for example a Dolphin copy).";
    }

    /// <summary>"Always use this folder": after a successful sync, save it in Settings so the button goes straight there next time.</summary>
    [ObservableProperty] private bool _rememberFolder;

    public Build Build { get; }
    public string Heading => $"Put \"{Build.Name}\" on your Project+";

    [ObservableProperty] private string? _folder;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isDone;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    private SyncPlan? _plan;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAppCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    public async Task ChooseFolderAsync(string picked)
    {
        Folder = picked;
        IsKnownFolderMissing = false;
        Plan = null;
        Changes = null;
        IsDone = false;
        IsBusy = true;
        Summary = "Checking what needs to change…";
        try
        {
            var target = ProjectPlusFolder.Resolve(picked);
            (Plan, Changes) = await Task.Run(() => (BuildSync.Plan(Build, target), CheckForUpdate(target)));
            Summary = Plan.Summary();
        }
        catch (MusicLibraryException ex) { Summary = ex.Message; }
        finally { IsBusy = false; }
    }

    // ---- Project+ updates ----

    /// <summary>What this Project+ has that the app's Project+ original doesn't (e.g. after a Project+ update), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAppCommand))]
    private ProjectPlusChanges? _changes;

    public bool HasUpdate => Changes is { HasChanges: true };
    public string UpdateText => Changes?.Summary() ?? "";

    /// <summary>True once "Update the app" ran: the main window reloads the build afterwards.</summary>
    public bool UpdatedApp { get; private set; }

    private ProjectPlusChanges? CheckForUpdate(ProjectPlusFolder target)
    {
        try { return ProjectPlusUpdate.Check(Build.Library, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    private bool CanUpdateApp => HasUpdate && !IsBusy;

    /// <summary>"Update the app from this Project+": refresh the Project+ original (builds keep their songs), then re-check the sync.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateApp))]
    private async Task UpdateApp()
    {
        if (Changes is not { } changes || Folder is not { } folder) return;
        IsBusy = true;
        try
        {
            string backup = await Task.Run(() => ProjectPlusUpdate.Apply(Build.Library, changes));
            UpdatedApp = true;
            IsBusy = false;
            await ChooseFolderAsync(folder);   // what Sync would do now
            Summary = $"The app is up to date with this Project+. Your builds kept their songs; what it replaced is backed up in:\n{backup}\n\n{Summary}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MusicLibraryException or InvalidDataException)
        {
            Summary = $"The update stopped: {ex.Message}\n\nYour builds are untouched.";
        }
        finally { IsBusy = false; }
    }

    private bool CanSync => Plan is { HasChanges: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task Sync()
    {
        if (Plan is not { } plan) return;
        IsBusy = true;
        try
        {
            var progress = new Progress<(int Done, int Total)>(p => Progress = p.Total == 0 ? 1 : (double)p.Done / p.Total);
            await Task.Run(() => BuildSync.Execute(plan, progress));
            Progress = 1;
            if (RememberFolder && Folder is { } used) Build.Library.SetSyncFolder(used);
            Summary = $"Done! \"{Build.Name}\" is now on {plan.Target.Root}.\n\nIf it's an SD card, eject it in Windows before taking it out, then put it in your Wii.";
            IsDone = true;
            Plan = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MusicLibraryException)
        {
            Summary = $"The sync stopped: {ex.Message}\n\nNothing was lost: your library is untouched. Check the card isn't locked or full, then try again.";
            Plan = null;
        }
        finally { IsBusy = false; }
    }
}
