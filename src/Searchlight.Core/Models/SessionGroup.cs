using System.Collections.ObjectModel;

namespace Searchlight.Models;

/// <summary>
/// A titled bucket of sessions for the grouped left-pane list. The
/// <see cref="Key"/> is the header text shown above the group — a relative
/// window ("Last 2 hours" … "Last 32 hours") for recent sessions, then a
/// calendar day, week, or month for progressively older sessions. The
/// <see cref="ShortKey"/> is the abbreviated label shown on the compact tick
/// rail for jump-to-group navigation. Sessions remain newest-first within each
/// group, and groups themselves are emitted newest-first by
/// <see cref="ViewModels.MainViewModel"/>.
/// </summary>
public sealed class SessionGroup : ObservableCollection<SessionInfo>
{
    /// <summary>Creates a group with the given header and tick-rail labels.</summary>
    public SessionGroup(string key, string shortKey)
    {
        Key = key;
        ShortKey = shortKey;
    }

    /// <summary>Header text displayed above the group in the list.</summary>
    public string Key { get; }

    /// <summary>Abbreviated label shown on the compact tick rail (e.g. "8h", "Jul 1", "Wk Jun 15", "Jun 2026").</summary>
    public string ShortKey { get; }

    internal void SetItems(IReadOnlyList<SessionInfo> sessions, bool force = false)
    {
        if (!force && Count == sessions.Count
            && this.Zip(sessions).All(pair => ReferenceEquals(pair.First, pair.Second)))
            return;

        // ASSUMPTION: native session IDs identify rows across summary replacements.
        // Preserve surviving containers; a forced Replace refreshes one-time bindings
        // after mutable pin/name changes without resetting the whole group.
        HashSet<string> desiredIds = sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = Count - 1; i >= 0; i--)
            if (!desiredIds.Contains(this[i].Id)) RemoveAt(i);

        HashSet<string> currentIds = this.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < sessions.Count; i++)
        {
            SessionInfo desired = sessions[i];
            if (i >= Count || this[i].Id != desired.Id)
            {
                int existingIndex = -1;
                // New rows need no search through the retained group. Only an
                // actual reorder scans the suffix for the row to move.
                if (currentIds.Contains(desired.Id))
                    for (int j = i + 1; j < Count; j++)
                        if (this[j].Id == desired.Id)
                        {
                            existingIndex = j;
                            break;
                        }

                if (existingIndex >= 0) Move(existingIndex, i);
                else
                {
                    Insert(i, desired);
                    currentIds.Add(desired.Id);
                    continue;
                }
            }

            if (force || !ReferenceEquals(this[i], desired)) this[i] = desired;
        }

        while (Count > sessions.Count) RemoveAt(Count - 1);
    }
}
