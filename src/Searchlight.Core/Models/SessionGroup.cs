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
}
