using System.Windows;
using System.Windows.Input;
using ConversationManager.Models;
using ConversationManager.ViewModels;

namespace ConversationManager;

public partial class TranscriptWindow : Window
{
    private readonly TranscriptViewModel _vm;

    public TranscriptWindow(Conversation conversation, string? initialFind = null)
    {
        InitializeComponent();
        _vm = new TranscriptViewModel(conversation);
        DataContext = _vm;
        Title = conversation.DisplayTitle;

        Loaded += async (_, _) => await _vm.LoadAsync(initialFind);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                FindBox.Focus();
                FindBox.SelectAll();
                e.Handled = true;
            }
        };
    }
}
