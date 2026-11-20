using CommunityToolkit.Mvvm.ComponentModel;
using PPlusTracklistEditor.Core.Library;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>One file in the "Add songs to your library" window: whether to add it, and the title and series it'll get.</summary>
public sealed partial class ImportSongItem : ObservableObject
{
    public ImportSongItem(string file, string? duplicateOf)
    {
        File = file;
        FileName = Path.GetFileName(file);
        Title = SongFileNames.TitleFromFileName(file);
        DuplicateOf = duplicateOf;
        Include = duplicateOf is null; // a song you already have is left out unless you tick it
    }

    public string File { get; }
    public string FileName { get; }
    /// <summary>The library song with exactly the same content, if any.</summary>
    public string? DuplicateOf { get; }
    public string Note => DuplicateOf is null ? "" : $"Already in your library as \"{DuplicateOf}\"";

    [ObservableProperty] private bool _include;
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _series = "";

    public override string ToString() => FileName; // what screen readers call the row
}
