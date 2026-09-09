using Searchlight.Models;
using Searchlight.Services;
using Xunit;

namespace Searchlight.Core.Tests;

public sealed class SessionTodoSortTests
{
    [Theory]
    [InlineData(SessionTodoSortColumn.Id)]
    [InlineData(SessionTodoSortColumn.Title)]
    [InlineData(SessionTodoSortColumn.Description)]
    [InlineData(SessionTodoSortColumn.Status)]
    public void TextColumns_SortBothWaysCaseInsensitively_WithMissingLast(SessionTodoSortColumn column)
    {
        SessionTodo Row(string value) => new() { Id = value, Title = value, Description = value, Status = value };
        SessionTodo[] rows = [Row("b"), Row(""), Row("A"), Row("a")];
        Assert.Equal(["A", "a", "b", ""], SessionTodoSort.Apply(rows, column, false).Select(row => row.Id));
        Assert.Equal(["b", "A", "a", ""], SessionTodoSort.Apply(rows, column, true).Select(row => row.Id));
    }

    [Theory]
    [InlineData(SessionTodoSortColumn.Created)]
    [InlineData(SessionTodoSortColumn.Updated)]
    public void Dates_CompareInstantsAndKeepInvalidValuesLast(SessionTodoSortColumn column)
    {
        SessionTodo Row(string id, string value) => new() { Id = id, CreatedAt = value, UpdatedAt = value };
        SessionTodo[] rows =
        [
            Row("old", "2026-09-08 09:00:00"),
            Row("missing", ""),
            Row("new", "2026-09-08T03:00:00-07:00"),
            Row("invalid", "not a date"),
            Row("tie", "2026-09-08T10:00:00Z"),
        ];
        Assert.Equal(["new", "tie", "old", "missing", "invalid"],
            SessionTodoSort.Apply(rows, column, true).Select(row => row.Id));
        Assert.Equal(["old", "new", "tie", "missing", "invalid"],
            SessionTodoSort.Apply(rows, column, false).Select(row => row.Id));
    }
}
