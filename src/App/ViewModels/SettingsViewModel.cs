using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.Core.Library;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>
/// The Settings window: light or dark (applies right away), and the Project+ folder "Put on my Project+" always uses.
/// Both are saved in the library (library.json), so they survive restarts and move with the library.
/// </summary>
public sealed partial class SettingsViewModel(AppLibrary library) : ObservableObject
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

    // ---- appearance ----

    public bool FollowWindows { get => ThemeService.Choice == ThemeChoice.FollowWindows; set { if (value) SetTheme(ThemeChoice.FollowWindows); } }
    public bool Light { get => ThemeService.Choice == ThemeChoice.Light; set { if (value) SetTheme(ThemeChoice.Light); } }
    public bool Dark { get => ThemeService.Choice == ThemeChoice.Dark; set { if (value) SetTheme(ThemeChoice.Dark); } }

    private void SetTheme(ThemeChoice choice)
    {
        if (ThemeService.Choice == choice) return;
        ThemeService.Apply(choice);
        library.SetTheme(choice.ToString());
        OnPropertyChanged(nameof(FollowWindows));
        OnPropertyChanged(nameof(Light));
        OnPropertyChanged(nameof(Dark));
    }

    // ---- sync folder ----

    public string? SyncFolder => library.Info.SyncFolder;
    public bool HasSyncFolder => SyncFolder is not null;
    public string SyncFolderText => SyncFolder ?? "Not set (each build uses the folder it was last put on)";
    public string SyncFolderStatus => SyncFolder is null ? ""
        : Directory.Exists(SyncFolder) ? "Found." : "Not there right now. If it's on an SD card, plug the card in.";

    [RelayCommand]
    private void ChangeSyncFolder()
    {
        string? folder = Dialogs.PickFolder(Owner, "Choose your Project+ folder (on your SD card or your computer)", SyncFolder ?? library.Info.ProjectPlusFolder);
        if (folder is null) return;
        try { ProjectPlusFolder.Resolve(folder); }
        catch (MusicLibraryException ex) { Dialogs.Error(Owner, ex.Message); return; }
        library.SetSyncFolder(folder);
        RefreshSyncFolder();
    }

    [RelayCommand]
    private void ForgetSyncFolder()
    {
        library.SetSyncFolder(null);
        RefreshSyncFolder();
    }

    private void RefreshSyncFolder()
    {
        OnPropertyChanged(nameof(SyncFolder));
        OnPropertyChanged(nameof(HasSyncFolder));
        OnPropertyChanged(nameof(SyncFolderText));
        OnPropertyChanged(nameof(SyncFolderStatus));
    }

    /// <summary>The saved theme as a choice (anything unknown means follow Windows).</summary>
    public static ThemeChoice ParseTheme(string? saved) =>
        Enum.TryParse<ThemeChoice>(saved, ignoreCase: true, out var choice) ? choice : ThemeChoice.FollowWindows;
}
