using System.Collections.ObjectModel;
using ConversationManager.Services;

namespace ConversationManager.ViewModels;

/// <summary>One heading in the overview - a branch, a folder, or a stretch of time.</summary>
public sealed class ConversationGroupViewModel
{
    public ConversationGroupViewModel(
        ConversationGroup group,
        IEnumerable<ConversationCardViewModel> cards,
        DeleteRequest delete,
        bool groupsCanOverlap = false)
    {
        Name = group.Name;
        Subtitle = group.Subtitle;
        Cards = new ObservableCollection<ConversationCardViewModel>(cards);
        GroupsCanOverlap = groupsCanOverlap;

        DeleteAllCommand = new AsyncRelayCommand(() =>
            delete(Cards.Select(c => c.Conversation).ToList(), DeleteLabel));
    }

    public string Name { get; }

    public string Subtitle { get; }

    public ObservableCollection<ConversationCardViewModel> Cards { get; }

    public AsyncRelayCommand DeleteAllCommand { get; }

    public string CountText => Cards.Count == 1 ? "1 conversation" : $"{Cards.Count} conversations";

    public string DeleteAllText => $"Delete all {Cards.Count}";

    /// <summary>
    /// True only when grouped by branch, which is the one mode where a conversation can be under
    /// two headings at once.
    /// </summary>
    private bool GroupsCanOverlap { get; }

    /// <summary>
    /// Names the group for the confirmation, and owns up to the one thing that is not obvious
    /// from the screen: a session that changed branches is listed under both of them, so deleting
    /// this heading empties part of another one too.
    /// </summary>
    private string DeleteLabel
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(Subtitle) ? Name : $"{Name}  ·  {Subtitle}";
            if (!GroupsCanOverlap) return label;

            var shared = Cards.Count(c => c.Conversation.Branches.Count > 1);
            if (shared == 0) return label;

            return label + Environment.NewLine +
                   $"{shared} of these also worked on another branch, and will go from " +
                   "that heading too.";
        }
    }
}
