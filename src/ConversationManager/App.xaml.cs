using System.Windows;
using System.Windows.Threading;

namespace ConversationManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A silent exit is the worst failure mode for a double-click tool; always say what broke.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ShowError(args.ExceptionObject as Exception);

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowError(e.Exception);
        e.Handled = true;
    }

    private static void ShowError(Exception? ex)
    {
        if (ex is null) return;
        MessageBox.Show(
            $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
            "Conversation Manager - unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
