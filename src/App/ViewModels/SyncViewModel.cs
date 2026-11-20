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
        string? start = build.Info.LastSyncFolder ?? build.Library.Info.ProjectPlusFolder;
        if (start is { } folder && Directory.Exists(folder)) _ = ChooseFolderAsync(folder);
        else Summary = "Choose your Project+ folder. It can be on your SD card or anywhere on your computer (for example a Dolphin copy).";
    }

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
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    public async Task ChooseFolderAsync(string picked)
    {
        Folder = picked;
        Plan = null;
        IsDone = false;
        IsBusy = true;
        Summary = "Checking what needs to change…";
        try
        {
            Plan = await Task.Run(() => BuildSync.Plan(Build, ProjectPlusFolder.Resolve(picked)));
            Summary = Plan.Summary();
        }
        catch (MusicLibraryException ex) { Summary = ex.Message; }
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
            Summary = $"Done! \"{Build.Name}\" is now on {plan.Target.SoundDir}.\n\nIf it's an SD card, eject it in Windows before taking it out, then put it in your Wii.";
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
