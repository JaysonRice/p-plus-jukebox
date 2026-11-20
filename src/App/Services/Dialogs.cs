using System.Windows;
using Microsoft.Win32;
using PPlusTracklistEditor.App.Views;

namespace PPlusTracklistEditor.App.Services;

/// <summary>Message boxes and file/folder pickers in one place, all with the app's title.</summary>
public static class Dialogs
{
    public const string AppTitle = "P+ Tracklist Editor";

    public static void Info(Window? owner, string message) => Show(owner, message, MessageBoxButton.OK, MessageBoxImage.Information);
    public static void Error(Window? owner, string message) => Show(owner, message, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static bool Confirm(Window? owner, string message) =>
        Show(owner, message, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    /// <summary>Yes = save, No = discard, Cancel = stay.</summary>
    public static MessageBoxResult AskSave(Window? owner, string what) =>
        Show(owner, $"Save your changes to {what}?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

    private static MessageBoxResult Show(Window? owner, string message, MessageBoxButton buttons, MessageBoxImage image) =>
        owner is null
            ? MessageBox.Show(message, AppTitle, buttons, image)
            : MessageBox.Show(owner, message, AppTitle, buttons, image);

    public static string? PickFolder(Window? owner, string title, string? initial = null)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (initial is not null && Directory.Exists(initial)) dlg.InitialDirectory = initial;
        return dlg.ShowDialog(owner) == true ? dlg.FolderName : null;
    }

    public static string[] PickFiles(Window? owner, string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, Multiselect = true };
        return dlg.ShowDialog(owner) == true ? dlg.FileNames : [];
    }

    public static string? PromptText(Window? owner, string title, string prompt, string initial = "")
    {
        var dlg = new TextPromptWindow(title, prompt, initial) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }

    public static void ShowInExplorer(string path)
    {
        var psi = File.Exists(path)
            ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            : new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"");
        System.Diagnostics.Process.Start(psi);
    }
}
