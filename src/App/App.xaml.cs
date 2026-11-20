using System.Windows;
using System.Windows.Threading;
using PPlusTracklistEditor.App.Services;
using PPlusTracklistEditor.App.ViewModels;
using PPlusTracklistEditor.App.Views;
using PPlusTracklistEditor.Core.Library;

namespace PPlusTracklistEditor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        string root = AppLibrary.DefaultRoot(out bool usedFallback);
        if (!AppLibrary.Exists(root))
        {
            var setup = new SetupWindow(root, usedFallback);
            if (setup.ShowDialog() != true) { Shutdown(); return; }
        }

        AppLibrary library;
        try { library = AppLibrary.Open(root); }
        catch (Exception ex)
        {
            Dialogs.Error(null, $"Your song library at \"{root}\" couldn't be opened.\n\n{ex.Message}");
            Shutdown();
            return;
        }

        var main = new MainWindow(new MainViewModel(library, usedFallback));
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Problems the user can act on come as MusicLibraryException / AudioImportException with a plain message.
        string message = e.Exception is MusicLibraryException or Core.Audio.AudioImportException
            ? e.Exception.Message
            : $"Something went wrong:\n\n{e.Exception.Message}\n\nYour saved work is safe. If this keeps happening, please report it with this message.";
        Dialogs.Error(Current?.MainWindow, message);
        e.Handled = true;
    }
}
