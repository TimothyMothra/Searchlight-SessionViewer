namespace Searchlight.Models;

/// <summary>A single todo row from a session's <c>session.db</c>.</summary>
public sealed record SessionTodo
{
    /// <summary>Stable todo id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Full description, when the source schema includes one.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Raw lifecycle status; unfamiliar values are preserved.</summary>
    public string Status { get; init; } = string.Empty;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "(No title)" : Title;
    public string DisplayStatus => string.IsNullOrWhiteSpace(Status) ? "(No status)" : Status;
}
