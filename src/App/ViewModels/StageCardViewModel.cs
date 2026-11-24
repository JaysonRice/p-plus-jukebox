using CommunityToolkit.Mvvm.ComponentModel;
using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Tlst;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>One stage in the stage list: its name, how many songs it has, and whether it differs from the Project+ original.</summary>
public sealed partial class StageCardViewModel : ObservableObject
{
    private readonly Build _build;

    public StageCardViewModel(Build build, StageInfo info)
    {
        _build = build;
        Name = info.Name;
        DisplayName = info.DisplayName;
        KnownToProjectPlus = info.KnownToProjectPlus;
        _changed = info.ChangedFromDefault;
        _songCount = CountSongs();
    }

    public string Name { get; }
    public string DisplayName { get; }
    public bool KnownToProjectPlus { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details))]
    private int _songCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details))]
    private bool _changed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details))]
    private bool _hasUnsavedChanges;

    public string Details
    {
        get
        {
            var parts = new List<string> { SongCount == 1 ? "1 song" : $"{SongCount} songs" };
            if (Changed) parts.Add("changed");
            if (!KnownToProjectPlus) parts.Add("not in your Project+");
            if (HasUnsavedChanges) parts.Add("unsaved");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>What screen readers (and UI Automation) call this item.</summary>
    public override string ToString() => $"{DisplayName}, {Details}";

    public void RefreshFromDisk()
    {
        SongCount = CountSongs();
        var info = _build.Stages().FirstOrDefault(s => s.Name == Name);
        Changed = info?.ChangedFromDefault ?? false;
    }

    private int CountSongs()
    {
        try { return TlstFile.Load(_build.TracklistPath(Name)).Entries.Count; }
        catch (Exception) { return 0; }
    }
}
