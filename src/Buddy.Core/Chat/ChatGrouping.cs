using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Buddy.Core.Chat;

/// <summary>
/// Consecutive messages of the same speaker form one group in the chat: <see cref="ChatEntry.JoinsPrevious"/> and
/// <see cref="ChatEntry.JoinsNext"/> are kept up to date as entries are added, removed or cleared.
/// </summary>
public static class ChatGrouping
{
    public static void Track(ObservableCollection<ChatEntry> entries)
    {
        entries.CollectionChanged += (_, e) =>
        {
            // Only the changed range and its neighbours move; a reset (Clear) or a move redoes everything.
            var (start, count) = e.Action switch
            {
                NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace => (e.NewStartingIndex, e.NewItems?.Count ?? 0),
                NotifyCollectionChangedAction.Remove => (e.OldStartingIndex, 0),
                _ => (0, entries.Count),
            };
            Update(entries, start - 1, start + count);
        };
        Update(entries, 0, entries.Count);
    }

    /// <summary>Two entries belong to one group: same speaker, both ordinary messages (no permission prompts).</summary>
    public static bool SameGroup(ChatEntry a, ChatEntry b) =>
        a.Speaker == b.Speaker && a.Speaker != Speaker.System && a is not PermissionEntry && b is not PermissionEntry;

    /// <summary>Recomputes from <paramref name="from"/>; past <paramref name="until"/> it stops at the first unchanged entry.</summary>
    private static void Update(IList<ChatEntry> entries, int from, int until)
    {
        for (var i = Math.Max(0, from); i < entries.Count; i++)
        {
            var entry = entries[i];
            var joinsPrevious = i > 0 && SameGroup(entries[i - 1], entry);
            var joinsNext = i + 1 < entries.Count && SameGroup(entry, entries[i + 1]);
            if (i > until && entry.JoinsPrevious == joinsPrevious && entry.JoinsNext == joinsNext)
            {
                return;
            }

            entry.JoinsPrevious = joinsPrevious;
            entry.JoinsNext = joinsNext;
        }
    }
}
