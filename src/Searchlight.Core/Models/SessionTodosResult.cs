namespace Searchlight.Models;

/// <summary>Distinguishes a real empty todo table from unavailable or incompatible data.</summary>
public enum SessionTodosStatus
{
    Success,
    MissingDatabase,
    MissingTable,
    UnsupportedSchema,
    Unavailable,
}

/// <summary>A single read-only snapshot, including any schema compatibility warning.</summary>
public sealed record SessionTodosResult
{
    public SessionTodosStatus Status { get; init; } = SessionTodosStatus.Success;
    public IReadOnlyList<SessionTodo> Todos { get; init; } = [];
    public IReadOnlyList<string> MissingFields { get; init; } = [];
    public string? Message { get; init; }
}
