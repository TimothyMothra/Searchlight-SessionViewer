using System.Globalization;
using Avalonia.Data.Converters;

namespace Searchlight.Avalonia;

/// <summary>Value converters used by the main window.</summary>
public static class Converters
{
    /// <summary>
    /// Formats a <see cref="DateTimeOffset"/> as a compact relative age
    /// ("just now", "12 min ago", "3 hours ago", then a local date).
    /// </summary>
    public static readonly IValueConverter RelativeTime =
        new FuncValueConverter<DateTimeOffset, string>(FormatRelative);

    /// <summary>Shows an em-dash for null/blank strings (matches the WinUI host).</summary>
    public static readonly IValueConverter StringOrDash =
        new FuncValueConverter<string?, string>(v => string.IsNullOrWhiteSpace(v) ? "—" : v!);

    private static string FormatRelative(DateTimeOffset value)
    {
        TimeSpan age = DateTimeOffset.Now - value;

        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (age < TimeSpan.FromHours(48))
        {
            int hours = (int)age.TotalHours;
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        return value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }
}
