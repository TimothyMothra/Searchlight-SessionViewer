using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Searchlight.Models;

/// <summary>
/// User-adjustable application settings, persisted as JSON by
/// <c>SettingsService</c>. An <see cref="ObservableObject"/> so the UI can bind a
/// <c>ToggleSwitch</c> two-way and the service can auto-save on change.
/// </summary>
public sealed partial class AppSettings : ObservableObject
{
    /// <summary>
    /// When true (default), every Resume opens as a new tab in the user's
    /// most-recently-used Windows Terminal window (<c>-w last</c>). When false,
    /// each Resume opens its own separate terminal window — the opt-out of the
    /// tabbed grouping.
    /// </summary>
    [ObservableProperty]
    private bool _useSharedTerminalWindow = true;

    /// <summary>
    /// When true, the app runs elevated (as Administrator). Opt-in only (default
    /// false). Needed when the user's main Windows Terminal runs as Admin: a
    /// non-elevated <c>wt -w</c> call cannot attach a tab to an elevated Terminal
    /// window (Windows blocks cross-integrity-level window reuse), so the app must
    /// match that elevation for shared-window resume to work. Toggling this on
    /// relaunches the app elevated (one UAC prompt); toggling off applies on the
    /// next launch.
    /// </summary>
    [ObservableProperty]
    private bool _runElevated;

    /// <summary>
    /// When true, every Resume appends <c>--yolo</c> to the <c>copilot --resume</c>
    /// command so the resumed session runs with all tool approvals auto-granted.
    /// Opt-in only (default false) because it bypasses per-action confirmation.
    /// </summary>
    [ObservableProperty]
    private bool _appendYolo;

    /// <summary>
    /// Session UUIDs the user has pinned to the top of the list, newest-pinned
    /// order preserved. Persisted so pins survive restarts. IMPORTANT: mutate by
    /// REASSIGNING the property (e.g. <c>PinnedSessionIds = [.. list]</c>) — an
    /// in-place <see cref="List{T}"/> edit does not raise <c>PropertyChanged</c> and
    /// so would not trigger the settings auto-save.
    /// </summary>
    [ObservableProperty]
    private List<string> _pinnedSessionIds = [];

    /// <summary>
    /// Per-session manual display-name overrides, keyed by session UUID. A session
    /// with an entry here shows the custom name instead of its auto-generated
    /// workspace name (or UUID). Persisted so renames survive restarts. IMPORTANT:
    /// mutate by REASSIGNING the property (e.g. <c>CustomSessionNames = new(dict)</c>) —
    /// an in-place <see cref="Dictionary{TKey, TValue}"/> edit does not raise
    /// <c>PropertyChanged</c> and so would not trigger the settings auto-save.
    /// </summary>
    [ObservableProperty]
    private Dictionary<string, string> _customSessionNames = [];

    /// <summary>
    /// Whether the optional Notes pane (right of the details pane) is shown.
    /// Persisted so the pane's open/closed state survives restarts. Default false
    /// (hidden) so the window opens compact.
    /// </summary>
    [ObservableProperty]
    private bool _notesPaneVisible;
}
