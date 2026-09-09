using Microsoft.Data.Sqlite;
using Searchlight.Models;

namespace Searchlight.Services;

/// <summary>
/// Reads a single session's <c>session.db</c> (tables <c>todos</c> and
/// <c>session_state</c>). The Todos tab uses an explicit result so missing or
/// incompatible data is not mistaken for an empty table.
/// </summary>
public sealed class SessionDbReader
{
    private static readonly string[] TodoFields = ["id", "title", "description", "status", "created_at", "updated_at"];

    /// <summary>Reads only recognized todo columns; never reads unused session_state values.</summary>
    public SessionTodosResult ReadTodos(string folderPath, CancellationToken token = default)
    {
        string dbPath = CopilotPaths.SessionDb(folderPath);
        try
        {
            token.ThrowIfCancellationRequested();
            // File.Exists hides access errors; only genuinely absent files are "missing".
            File.GetAttributes(dbPath);
            using var connection = OpenReadOnly(dbPath);
            connection.Open();
            return ReadTodos(connection, token);
        }
        catch (FileNotFoundException)
        {
            return new() { Status = SessionTodosStatus.MissingDatabase, Message = "This session has no session.db yet." };
        }
        catch (DirectoryNotFoundException)
        {
            return new() { Status = SessionTodosStatus.MissingDatabase, Message = "This session has no session.db yet." };
        }
        catch (SqliteException ex)
        {
            return Unavailable(ex, ex.SqliteErrorCode is 5 or 6
                ? "The session database is busy. Try Refresh again."
                : $"Could not read agent tasks: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Unavailable(ex, $"Could not read agent tasks: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unavailable(ex, $"Could not access agent tasks: {ex.Message}");
        }
    }

    private static SessionTodosResult Unavailable(Exception error, string message)
    {
        Diagnostics.CoreLog.Write($"Session todos unavailable: {error}");
        return new() { Status = SessionTodosStatus.Unavailable, Message = message };
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

            IReadOnlyList<SessionTodo> todos = ReadTodos(connection, default).Todos;
            IReadOnlyDictionary<string, string> state = ReadState(connection);
            return new SessionDbSnapshot { Todos = todos, State = state };
        }
        catch (SqliteException)
        {
            return SessionDbSnapshot.Empty;
        }
    }

    private static SessionTodosResult ReadTodos(SqliteConnection connection, CancellationToken token)
    {
        // ASSUMPTION: schema and rows must belong to one committed snapshot while
        // Copilot is writing. A deferred transaction starts with a read, not a write lock.
        using var transaction = connection.BeginTransaction(deferred: true);
        bool withoutRowId;
        using (var table = connection.CreateCommand())
        {
            table.Transaction = transaction;
            table.CommandText = """
                SELECT wr FROM pragma_table_list
                WHERE schema = 'main' AND name = 'todos' COLLATE NOCASE AND type = 'table';
                """;
            object? value = table.ExecuteScalar();
            if (value is null)
            {
                return new()
                {
                    Status = SessionTodosStatus.MissingTable,
                    Message = "Copilot has not created a task table (todos) in this session database.",
                };
            }
            withoutRowId = Convert.ToInt64(value) != 0;
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primaryKey = new SortedDictionary<int, string>();
        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = """PRAGMA main.table_xinfo("todos");""";
            using var reader = schema.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                string name = reader.GetString(1);
                columns.Add(name);
                int keyOrder = reader.GetInt32(5);
                if (keyOrder > 0) primaryKey.Add(keyOrder, name);
            }
        }

        string[] missing = TodoFields.Where(field => !columns.Contains(field)).ToArray();
        if (!new[] { "title", "description", "status" }.Any(columns.Contains))
        {
            return new()
            {
                Status = SessionTodosStatus.UnsupportedSchema,
                MissingFields = missing,
                Message = "The agent task table (todos) has no recognized title, description, or status columns.",
            };
        }

        string projection = string.Join(", ", TodoFields.Select(field =>
            columns.Contains(field) ? $"CAST({QuoteIdentifier(field)} AS TEXT)" : "NULL"));
        // ASSUMPTION: ordinary tables use rowid order. WITHOUT ROWID tables have
        // no insertion order, so use their declared key. Avoid shadowed rowid aliases.
        string? rowId = withoutRowId ? null
            : new[] { "rowid", "_rowid_", "oid" }.FirstOrDefault(alias => !columns.Contains(alias));
        string order = rowId is not null ? rowId : string.Join(", ", primaryKey.Values.Select(QuoteIdentifier));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {projection} FROM main.todos"
            + (order.Length > 0 ? $" ORDER BY {order};" : ";");
        var todos = new List<SessionTodo>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                todos.Add(new SessionTodo
                {
                    Id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    Description = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Status = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    CreatedAt = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    UpdatedAt = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                });
            }
        }
        return new() { Todos = todos, MissingFields = missing };
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

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
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1,
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
