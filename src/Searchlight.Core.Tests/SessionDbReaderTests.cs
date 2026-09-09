using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Searchlight.Models;
using Searchlight.Services;
using Xunit;

namespace Searchlight.Core.Tests;

public sealed class SessionDbReaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "searchlight-todos-" + Guid.NewGuid().ToString("N"));
    private readonly SessionDbReader _reader = new();
    private string DatabasePath => Path.Combine(_folder, "session.db");

    public SessionDbReaderTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void MissingDatabase_DoesNotCreateFiles()
    {
        Assert.Equal(SessionTodosStatus.MissingDatabase, Read().Status);
        Assert.Empty(Directory.GetFileSystemEntries(_folder));
    }

    [Fact]
    public void MissingTable_IsNotAnEmptyTodoTable()
    {
        Execute("CREATE TABLE unrelated (value TEXT);");
        var result = Read();
        Assert.Equal(SessionTodosStatus.MissingTable, result.Status);
        Assert.NotNull(result.Message);
        Execute("CREATE TABLE todos (id TEXT, title TEXT, description TEXT, status TEXT);");
        result = Read();
        Assert.Equal(SessionTodosStatus.Success, result.Status);
        Assert.Empty(result.Todos);
    }

    [Fact]
    public void ReadsDescriptionsAndRawStatuses_InRowIdOrder_WithoutWritingDatabase()
    {
        Execute("""
            CREATE TABLE todos (id TEXT PRIMARY KEY, title TEXT, description TEXT, status TEXT, created_at TEXT, updated_at TEXT);
            INSERT INTO todos VALUES ('z', 'First', 'Long description', 'in_progress', '2026-09-08 10:00:00', '2026-09-08T11:00:00-07:00');
            INSERT INTO todos VALUES ('a', 'Second', '', 'future_status', NULL, NULL);
            """);
        byte[] before = File.ReadAllBytes(DatabasePath);
        var result = Read();
        Assert.Equal(SessionTodosStatus.Success, result.Status);
        Assert.Empty(result.MissingFields);
        Assert.Equal(["z", "a"], result.Todos.Select(todo => todo.Id));
        Assert.Equal("Long description", result.Todos[0].Description);
        Assert.Equal("future_status", result.Todos[1].Status);
        Assert.Equal("2026-09-08 10:00:00", result.Todos[0].CreatedAt);
        Assert.Equal("2026-09-08T11:00:00-07:00", result.Todos[0].UpdatedAt);
        Assert.Equal(string.Empty, result.Todos[1].CreatedAt);
        Assert.Equal(string.Empty, result.Todos[1].UpdatedAt);
        Assert.Equal(before, File.ReadAllBytes(DatabasePath));
    }

    [Fact]
    public void AddedReorderedAndCaseChangedColumns_AreSupported()
    {
        Execute("""
            CREATE TABLE TODOS (UPDATED_AT TEXT, extra TEXT, STATUS TEXT, DESCRIPTION TEXT, CREATED_AT TEXT, TITLE TEXT, ID TEXT);
            INSERT INTO TODOS VALUES ('later', 'ignored', 'blocked', 'Details', 'earlier', 'Task', 'one');
            """);
        var result = Read();
        Assert.Equal(SessionTodosStatus.Success, result.Status);
        Assert.Empty(result.MissingFields);
        Assert.Equal(new SessionTodo { Id = "one", Title = "Task", Description = "Details", Status = "blocked", CreatedAt = "earlier", UpdatedAt = "later" },
            Assert.Single(result.Todos));
    }

    [Theory]
    [InlineData("title", "Task")]
    [InlineData("description", "Details")]
    [InlineData("status", "custom")]
    public void MissingColumns_PreserveRecognizedFields(string column, string value)
    {
        // Test inputs are fixed identifiers, not external SQL.
        Execute($"CREATE TABLE todos ({column} TEXT); INSERT INTO todos VALUES ('{value}');");
        var result = Read();
        Assert.Equal(SessionTodosStatus.Success, result.Status);
        Assert.Equal(5, result.MissingFields.Count);
        var todo = Assert.Single(result.Todos);
        Assert.Equal(column == "title" ? value : string.Empty, todo.Title);
        Assert.Equal(column == "description" ? value : string.Empty, todo.Description);
        Assert.Equal(column == "status" ? value : string.Empty, todo.Status);
    }

    [Fact]
    public void NullsAndNonTextValues_DoNotDiscardRows()
    {
        Execute("""
            CREATE TABLE todos (id, title, description, status);
            INSERT INTO todos VALUES (1, NULL, NULL, NULL);
            INSERT INTO todos VALUES (2, 123, 456, 789);
            """);
        var result = Read();
        Assert.Equal(SessionTodosStatus.Success, result.Status);
        Assert.Equal(2, result.Todos.Count);
        Assert.Equal("(No title)", result.Todos[0].DisplayTitle);
        Assert.Equal("(No status)", result.Todos[0].DisplayStatus);
        Assert.Equal(string.Empty, result.Todos[0].Description);
        Assert.Equal("123", result.Todos[1].Title);
        Assert.Equal("456", result.Todos[1].Description);
        Assert.Equal("789", result.Todos[1].Status);
    }

    [Fact]
    public void UnrecognizedSchema_IsExplicitlyUnsupported()
    {
        Execute("CREATE TABLE todos (id TEXT, renamed_title TEXT); INSERT INTO todos VALUES ('id', 'Task');");
        var result = Read();
        Assert.Equal(SessionTodosStatus.UnsupportedSchema, result.Status);
        Assert.Empty(result.Todos);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public void TimestampOnlySchema_IsStillUnsupported()
    {
        Execute("CREATE TABLE todos (id TEXT, created_at TEXT, updated_at TEXT);");
        Assert.Equal(SessionTodosStatus.UnsupportedSchema, Read().Status);
    }

    [Fact]
    public void MissingTimestampColumns_DoNotHideLegacyRows()
    {
        Execute("CREATE TABLE todos (id TEXT, title TEXT, description TEXT, status TEXT); INSERT INTO todos VALUES ('id', 'Task', 'Detail', 'done');");
        var result = Read();
        Assert.Equal(SessionTodosStatus.Success, result.Status);
        Assert.Equal(["created_at", "updated_at"], result.MissingFields);
        var todo = Assert.Single(result.Todos);
        Assert.Equal("Task", todo.Title);
        Assert.Empty(todo.CreatedAt);
        Assert.Empty(todo.UpdatedAt);
    }

    [Fact]
    public void SchemaIsReinspectedOnEveryRead()
    {
        Execute("CREATE TABLE todos (title TEXT); INSERT INTO todos VALUES ('Task');");
        Assert.Contains("description", Read().MissingFields);
        Execute("ALTER TABLE todos ADD COLUMN description TEXT; UPDATE todos SET description = 'New detail';");
        var result = Read();
        Assert.DoesNotContain("description", result.MissingFields);
        Assert.Equal("New detail", Assert.Single(result.Todos).Description);
    }

    [Fact]
    public void WithoutRowId_UsesDeclaredPrimaryKeyOrder()
    {
        Execute("""
            CREATE TABLE todos ("sort""key" TEXT PRIMARY KEY, title TEXT) WITHOUT ROWID;
            INSERT INTO todos VALUES ('z', 'Last'), ('a', 'First');
            """);
        var result = Read();
        Assert.Equal(SessionTodosStatus.Success, result.Status);
        Assert.Equal(["First", "Last"], result.Todos.Select(todo => todo.Title));
    }

    [Fact]
    public void ShadowedRowId_UsesUnshadowedAlias()
    {
        Execute("""
            CREATE TABLE todos (rowid INTEGER, title TEXT);
            INSERT INTO todos VALUES (9, 'First'), (1, 'Second');
            """);
        Assert.Equal(["First", "Second"], Read().Todos.Select(todo => todo.Title));
    }

    [Fact]
    public void WalReadsCommittedChanges_WithoutCheckpointingOrReadingUncommittedData()
    {
        using var writer = OpenWriter();
        Execute(writer, """
            PRAGMA journal_mode=WAL;
            PRAGMA wal_autocheckpoint=0;
            CREATE TABLE todos (title TEXT, description TEXT, status TEXT);
            INSERT INTO todos VALUES ('Task', 'Committed', 'pending');
            """);
        byte[] database = ReadSharedBytes(DatabasePath);
        byte[] wal = ReadSharedBytes(DatabasePath + "-wal");
        Execute(writer, "BEGIN; UPDATE todos SET description = 'Uncommitted';");
        Assert.Equal("Committed", Assert.Single(Read().Todos).Description);
        Assert.Equal(database, ReadSharedBytes(DatabasePath));
        Assert.Equal(wal, ReadSharedBytes(DatabasePath + "-wal"));
        Execute(writer, "COMMIT;");
        Assert.Equal("Uncommitted", Assert.Single(Read().Todos).Description);
    }

    [Fact]
    public void LockedDatabase_ReturnsRetryableErrorWithinBoundedWait()
    {
        using var writer = OpenWriter();
        Execute(writer, "CREATE TABLE todos (title TEXT); BEGIN EXCLUSIVE;");
        var elapsed = Stopwatch.StartNew();
        var result = Read();
        Assert.Equal(SessionTodosStatus.Unavailable, result.Status);
        Assert.Contains("busy", result.Message);
        Assert.Empty(result.Todos);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        Execute(writer, "ROLLBACK;");
        Assert.Equal(SessionTodosStatus.Success, Read().Status);
    }

    [Fact]
    public void CorruptDatabase_IsNotReportedAsEmpty()
    {
        File.WriteAllText(DatabasePath, new string('x', 4096));
        var result = Read();
        Assert.Equal(SessionTodosStatus.Unavailable, result.Status);
        Assert.Contains("Could not read", result.Message);
    }

    [Fact]
    public void Cancellation_IsNotConvertedToAnErrorOrEmptySnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => _reader.ReadTodos(_folder, cancellation.Token));
    }

    [Fact]
    public void LegacyCombinedReader_StillReadsState()
    {
        Execute("""
            CREATE TABLE todos (id TEXT, title TEXT, status TEXT);
            INSERT INTO todos VALUES ('id', 'Task', 'done');
            CREATE TABLE session_state (key TEXT, value TEXT);
            INSERT INTO session_state VALUES ('phase', 'finished');
            """);
        var snapshot = _reader.Read(_folder);
        Assert.Equal("Task", Assert.Single(snapshot.Todos).Title);
        Assert.Equal("finished", snapshot.State["phase"]);
    }

    private SessionTodosResult Read() => _reader.ReadTodos(_folder);

    private static byte[] ReadSharedBytes(string path)
    {
        // SQLite keeps its writer handles open throughout the WAL scenario.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var contents = new MemoryStream();
        file.CopyTo(contents);
        return contents.ToArray();
    }

    private SqliteConnection OpenWriter()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false,
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    private void Execute(string sql)
    {
        using var connection = OpenWriter();
        Execute(connection, sql);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
