using System.Windows;
using System.Windows.Input;
using ConversationManager.Models;
using ConversationManager.ViewModels;

namespace ConversationManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(OpenPreview);
        DataContext = _vm;

        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await _vm.ReloadAsync();
        };

        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _vm.IsSearching)
        {
            _vm.SearchText = "";
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Opens the transcript, carrying the current query across so the words the user searched for
    /// are already highlighted when the window appears.
    /// </summary>
    private void OpenPreview(Conversation conversation)
    {
        var window = new TranscriptWindow(conversation, _vm.SearchText) { Owner = this };
        window.Show();
    }
}
