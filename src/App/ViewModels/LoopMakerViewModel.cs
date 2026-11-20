using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.Core.Audio;
using PPlusTracklistEditor.Core.Library;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>
/// The Loop Maker tab: pick an audio file, set its loop, save it straight into the song library. Adding it to a
/// stage is a separate step ("Add from your library…" on the Stages tab).
/// Three states: nothing open (pick a file), editing (<see cref="Editor"/>), just saved (<see cref="SavedSongPath"/>).
/// </summary>
public sealed partial class LoopMakerViewModel(AppLibrary library, PreviewPlayer player, Action showLibrary) : ObservableObject
{
    private static Window? Owner => Application.Current?.MainWindow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(IsEditing))]
    private LoopEditorViewModel? _editor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(IsSaved), nameof(SavedText))]
    private string? _savedSongPath;

    public bool IsEditing => Editor is not null;
    public bool IsSaved => Editor is null && SavedSongPath is not null;
    public bool IsEmpty => Editor is null && SavedSongPath is null;

    public string SavedText => SavedSongPath is null ? "" :
        $"\"{SavedSongPath.Split('/')[^1]}\" is in your song library" +
        (SavedSongPath.Contains('/') ? $" (series \"{SavedSongPath.Split('/')[0]}\")." : ".") +
        "\nTo use it, open a stage on the Stages tab and click \"Add from your library…\".";

    /// <summary>A song is open and hasn't been saved (leaving would lose the loop points).</summary>
    public bool HasUnsavedWork => Editor is { Waveform: not null };

    public const string FileFilter =
        "Audio files (*.mp3;*.wav;*.flac;*.m4a;*.brstm)|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.wma;*.ogg;*.brstm|All files|*.*";

    [RelayCommand]
    private async Task ChooseFile()
    {
        var files = Dialogs.PickFiles(Owner, "Choose a song to loop", FileFilter);
        if (files.Length > 0) await OpenAsync(files[0]);
    }

    /// <summary>Opens a file (picked or dropped). Asks first if another song is being worked on.</summary>
    public async Task OpenAsync(string file)
    {
        if (!ConfirmDiscard()) return;
        Close();
        SavedSongPath = null;
        var editor = new LoopEditorViewModel(file, library.Songs.SeriesFolders().ToList(), player);
        Editor = editor;
        await editor.LoadAsync();
        if (editor.LoadError is not null && ReferenceEquals(Editor, editor))
        {
            Dialogs.Info(Owner, editor.LoadError);
            Close();
        }
    }

    [RelayCommand]
    private async Task SaveToLibrary()
    {
        if (Editor is not { } editor) return;
        Views.LoopEditorView.CommitTypedTime();
        string? error = await editor.MakeAsync();
        if (error is not null) { Dialogs.Info(Owner, error); return; }
        try
        {
            string path = library.AddSong(editor.OutputFile!, editor.Title.Trim(), editor.Series.Trim());
            TryDelete(editor.OutputFile!);
            Close();
            SavedSongPath = path;
        }
        catch (Exception ex) when (ex is MusicLibraryException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(Owner, $"The song couldn't be saved to your library: {ex.Message}");
        }
    }

    [RelayCommand]
    private void StartOver()
    {
        if (!ConfirmDiscard()) return;
        Close();
        SavedSongPath = null;
    }

    [RelayCommand]
    private void ShowInLibrary() => showLibrary();

    /// <summary>Asks before throwing away an open, unsaved song. True = go ahead.</summary>
    public bool ConfirmDiscard() =>
        !HasUnsavedWork || Dialogs.Confirm(Owner, $"Stop working on \"{Editor!.Title}\"? Its loop hasn't been saved to your library.");

    /// <summary>Leaving the tab: stop playing (the work stays open).</summary>
    public void Pause() => Editor?.Pause();

    private void Close()
    {
        Editor?.Dispose();
        Editor = null;
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
