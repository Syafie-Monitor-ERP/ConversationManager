using System.Windows;
using ConversationManager.Services;

namespace ConversationManager.Platform;

/// <summary>
/// The few moments this app stops and asks. Everything else reports itself on the card it
/// happened on; deleting is the one action that cannot be taken back from inside the app, so it
/// is the one action that gets a modal.
/// </summary>
public static class Dialogs
{
    /// <summary>
    /// Puts the numbers in front of the user before anything is removed: how many, how much disk,
    /// and what happens to the half of a conversation that lives outside the transcript.
    /// </summary>
    public static bool ConfirmDelete(DeletePlan plan, string what, DeleteMode mode)
    {
        var answer = MessageBox.Show(
            plan.Detail(what, mode),
            plan.Question,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            // No is the default: a stray Enter or Space on a focused dialog must not delete
            // twenty conversations.
            MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }
}
