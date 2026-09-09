using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Searchlight.Diagnostics;
using Searchlight.Models;
using Searchlight.Services;

namespace Searchlight.ViewModels;

/// <summary>Owns the explicit, uncached Todos-tab snapshot, independently of general details.</summary>
public sealed partial class TodosViewModel(ISessionDataSource source) : ObservableObject
{
    private readonly SemaphoreSlim _gate = new(1);
    private SessionInfo? _session;
    private bool _isActive;
    private CancellationTokenSource? _cancellation;
    private int _generation;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message), nameof(Warning))]
    private SessionTodosResult? _result;

    [ObservableProperty]
    private string _countsText = string.Empty;

    public IReadOnlyList<SessionTodo> Items { get; private set; } = [];
    public SessionTodoSortColumn SortColumn { get; private set; } = SessionTodoSortColumn.Updated;
    public bool SortDescending { get; private set; } = true;
    public string IdHeader => Header("ID", SessionTodoSortColumn.Id);
    public string TitleHeader => Header("Title", SessionTodoSortColumn.Title);
    public string DescriptionHeader => Header("Description", SessionTodoSortColumn.Description);
    public string StatusHeader => Header("Status", SessionTodoSortColumn.Status);
    public string CreatedHeader => Header("Created", SessionTodoSortColumn.Created);
    public string UpdatedHeader => Header("Updated", SessionTodoSortColumn.Updated);

    private string Header(string title, SessionTodoSortColumn column) =>
        title + (SortColumn == column ? (SortDescending ? " \u2193" : " \u2191") : string.Empty);

    partial void OnResultChanged(SessionTodosResult? value) => ApplySort();

    [RelayCommand]
    private void Sort(SessionTodoSortColumn column)
    {
        if (!Enum.IsDefined(column)) throw new ArgumentOutOfRangeException(nameof(column));
        SortDescending = column == SortColumn ? !SortDescending
            : column is SessionTodoSortColumn.Created or SessionTodoSortColumn.Updated;
        SortColumn = column;
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        foreach (string property in new[] { nameof(IdHeader), nameof(TitleHeader), nameof(DescriptionHeader),
                     nameof(StatusHeader), nameof(CreatedHeader), nameof(UpdatedHeader) })
            OnPropertyChanged(property);
        ApplySort();
    }

    private void ApplySort()
    {
        Items = SessionTodoSort.Apply(Result?.Todos ?? [], SortColumn, SortDescending);
        OnPropertyChanged(nameof(Items));
    }

    public string? Message => Result is null ? null
        : Result.Status == SessionTodosStatus.Success
            ? (Result.Todos.Count == 0 ? "Copilot has not recorded any tasks for this session." : null)
            : Result.Message;
    public string? Warning => Result is { Status: SessionTodosStatus.Success, MissingFields.Count: > 0 }
        ? $"Missing fields: {string.Join(", ", Result.MissingFields)}. Available fields are shown."
        : null;

    /// <summary>Allows headless callers to await the currently requested snapshot.</summary>
    public Task CurrentLoad { get; private set; } = Task.CompletedTask;

    public void SetSession(SessionInfo? session)
    {
        bool changed = _session?.Id != session?.Id || _session?.FolderPath != session?.FolderPath;
        _session = session;
        if (!changed) return;
        // ASSUMPTION: session identity is id + path, not the refreshed summary object.
        // A new selection is never permission to read its database.
        _isActive = false;
        CancelLoad();
        Result = null;
        CountsText = string.Empty;
        RefreshCommand.NotifyCanExecuteChanged();
    }

    public void SetActive(bool active)
    {
        if (_isActive == active) return;
        _isActive = active;
        if (active && _session is not null)
            StartLoad();
        else
            CancelLoad();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task Refresh()
    {
        StartLoad();
        return CurrentLoad;
    }

    private bool CanRefresh() => _isActive && _session is not null && !IsLoading;

    private void StartLoad()
    {
        if (!CanRefresh()) return;
        CancelLoad();
        Result = null;
        CountsText = string.Empty;
        _cancellation = new CancellationTokenSource();
        IsLoading = true;
        CurrentLoad = LoadAsync(_session!, _generation, _cancellation.Token);
    }

    private void CancelLoad()
    {
        ++_generation;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        IsLoading = false;
    }

    private async Task LoadAsync(SessionInfo session, int generation, CancellationToken token)
    {
        try
        {
            // A cancelled SQLite call may finish its bounded lock wait. Serialize
            // subsequent requests, then reject obsolete publications by generation.
            await _gate.WaitAsync(token).ConfigureAwait(true);
            (SessionTodosResult Result, string Counts) snapshot;
            try
            {
                snapshot = await Task.Run(() =>
                {
                    SessionTodosResult result = source.ReadTodos(session, token);
                    token.ThrowIfCancellationRequested();
                    return (result, DescribeCounts(result));
                }, token).ConfigureAwait(true);
            }
            finally { _gate.Release(); }

            if (generation != _generation || token.IsCancellationRequested) return;
            Result = snapshot.Result;
            CountsText = snapshot.Counts;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException ex) { PublishError(ex, generation); }
        catch (UnauthorizedAccessException ex) { PublishError(ex, generation); }
        finally
        {
            if (generation == _generation) IsLoading = false;
        }
    }

    private void PublishError(Exception error, int generation)
    {
        CoreLog.Write($"Todos load failed: {error}");
        if (generation != _generation) return;
        Result = new()
        {
            Status = SessionTodosStatus.Unavailable,
            Message = $"Could not read agent tasks: {error.Message}",
        };
    }

    private static string DescribeCounts(SessionTodosResult result)
    {
        if (result.Status != SessionTodosStatus.Success) return string.Empty;
        var counts = result.Todos
            .GroupBy(todo => string.IsNullOrWhiteSpace(todo.Status) ? string.Empty : todo.Status)
            .Select(group => $"{(group.Key.Length == 0 ? "(No status)" : group.Key)}: {group.Count()}");
        return $"{result.Todos.Count} total"
            + (result.Todos.Count == 0 ? string.Empty : " | " + string.Join(" | ", counts));
    }
}
