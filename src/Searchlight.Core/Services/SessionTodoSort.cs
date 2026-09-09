using System.Globalization;
using Searchlight.Models;

namespace Searchlight.Services;

internal static class SessionTodoSort
{
    public static IReadOnlyList<SessionTodo> Apply(
        IReadOnlyList<SessionTodo> rows, SessionTodoSortColumn column, bool descending)
    {
        if (column is SessionTodoSortColumn.Created or SessionTodoSortColumn.Updated)
        {
            var keys = rows.Select(row => (Row: row, Date: ParseDate(
                column == SessionTodoSortColumn.Created ? row.CreatedAt : row.UpdatedAt)));
            var ordered = keys.OrderBy(key => key.Date is null);
            return (descending ? ordered.ThenByDescending(key => key.Date) : ordered.ThenBy(key => key.Date))
                .Select(key => key.Row).ToArray();
        }

        string Text(SessionTodo row) => column switch
        {
            SessionTodoSortColumn.Id => row.Id,
            SessionTodoSortColumn.Title => row.Title,
            SessionTodoSortColumn.Description => row.Description,
            SessionTodoSortColumn.Status => row.Status,
            _ => throw new ArgumentOutOfRangeException(nameof(column)),
        };
        // Stable ordering retains source order for equal keys, including missing values.
        var textKeys = rows.Select(row => (Row: row, Text: Text(row)));
        var textOrdered = textKeys.OrderBy(key => string.IsNullOrWhiteSpace(key.Text));
        return (descending
                ? textOrdered.ThenByDescending(key => key.Text, StringComparer.OrdinalIgnoreCase)
                : textOrdered.ThenBy(key => key.Text, StringComparer.OrdinalIgnoreCase))
            .Select(key => key.Row).ToArray();
    }

    private static DateTimeOffset? ParseDate(string value)
    {
        // ASSUMPTION: Copilot's bare SQLite timestamps use UTC (CURRENT_TIMESTAMP).
        // Explicit offsets are honored; displayed text is never reformatted.
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;
    }
}
