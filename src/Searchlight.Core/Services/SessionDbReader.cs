using Microsoft.Data.Sqlite;
using Searchlight.Models;

namespace Searchlight.Services;

/// <summary>
/// Reads a single session's <c>session.db</c> (tables <c>todos</c> and
/// <c>session_state</c>) for the details pane. Opened read-only; all failures
/// (missing file, missing table, locked db) degrade to empty results.
/// </summary>
public sealed class SessionDbReader
{
    /// <summary>Reads only the displayed todos; session_state can contain large unused values.</summary>
    public IReadOnlyList<SessionTodo> ReadTodos(string folderPath)
    {
        string dbPath = CopilotPaths.SessionDb(folderPath);
        if (!File.Exists(dbPath)) return [];
        try
        {
            using var connection = OpenReadOnly(dbPath);
            connection.Open();
            return ReadTodos(connection);
        }
        catch (SqliteException ex)
        {
            Diagnostics.CoreLog.Write($"Session todos unavailable: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Loads todos and key/value session state for the session rooted at
    /// <paramref name="folderPath"/>. Returns an empty snapshot when the
    /// database is absent or unreadable.
    /// </summary>
    public SessionDbSnapshot Read(string folderPath)
    {
        string dbPath = CopilotPaths.SessionDb(folderPath);
        if (!File.Exists(dbPath))
        {
            return SessionDbSnapshot.Empty;
        }

        try
        {
            using var connection = OpenReadOnly(dbPath);
            connection.Open();

            IReadOnlyList<SessionTodo> todos = ReadTodos(connection);
            IReadOnlyDictionary<string, string> state = ReadState(connection);
            return new SessionDbSnapshot { Todos = todos, State = state };
        }
        catch (SqliteException)
        {
            return SessionDbSnapshot.Empty;
        }
    }

    private static IReadOnlyList<SessionTodo> ReadTodos(SqliteConnection connection)
    {
        var todos = new List<SessionTodo>();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, title, status FROM todos ORDER BY rowid;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                todos.Add(new SessionTodo
                {
                    Id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    Status = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                });
            }
        }
        catch (SqliteException)
        {
            // Table may not exist in older sessions; return whatever was read.
        }

        return todos;
    }

    private static IReadOnlyDictionary<string, string> ReadState(SqliteConnection connection)
    {
        var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM session_state;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                state[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }
        }
        catch (SqliteException)
        {
            // session_state may not exist; ignore.
        }

        return state;
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        };
        return new SqliteConnection(builder.ConnectionString);
    }
}

/// <summary>Todos + session state read from a session's <c>session.db</c>.</summary>
public sealed record SessionDbSnapshot
{
    /// <summary>An empty snapshot (no db / unreadable).</summary>
    public static SessionDbSnapshot Empty { get; } = new();

    /// <summary>Todo rows in insertion order.</summary>
    public IReadOnlyList<SessionTodo> Todos { get; init; } = [];

    /// <summary>Key/value session state.</summary>
    public IReadOnlyDictionary<string, string> State { get; init; } =
        new Dictionary<string, string>();
}
