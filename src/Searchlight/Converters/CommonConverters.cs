using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Searchlight.Converters;

/// <summary>
/// Maps a <see cref="bool"/> to <see cref="Visibility"/>. Pass the string
/// parameter <c>invert</c> to flip the mapping (true → Collapsed).
/// </summary>
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool flag = value is bool b && b;
        if (IsInvert(parameter))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        bool visible = value is Visibility v && v == Visibility.Visible;
        return IsInvert(parameter) ? !visible : visible;
    }

    private static bool IsInvert(object parameter) =>
        parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Maps a string to <see cref="Visibility"/>: non-empty → Visible. Pass
/// <c>invert</c> to show only when the string is null/empty.
/// </summary>
public sealed partial class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool hasText = value is string s && !string.IsNullOrWhiteSpace(s);
        if (IsInvert(parameter))
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static bool IsInvert(object parameter) =>
        parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Renders a null/empty string as an em dash so detail rows never look broken.
/// </summary>
public sealed partial class StringOrDashConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? s : "—";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Formats a <see cref="DateTimeOffset"/> as a compact friendly string
/// (relative for the last day, otherwise local date + time).
/// The <c>absolute</c> parameter always includes local date, time, seconds, and offset.
/// </summary>
public sealed partial class FriendlyDateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DateTimeOffset dto)
        {
            return "—";
        }

        DateTimeOffset local = dto.ToLocalTime();
        // ASSUMPTION: explicit timestamps use local time like the rest of the UI;
        // include the offset so daylight-saving transitions remain unambiguous.
        if (parameter is string format && format == "absolute")
            return local.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture);

        TimeSpan age = DateTimeOffset.Now - local;

        if (age < TimeSpan.Zero)
        {
            return local.ToString("yyyy-MM-dd HH:mm");
        }

        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalMinutes < 60)
        {
            int m = (int)age.TotalMinutes;
            return $"{m} min ago";
        }

        if (age.TotalHours < 24)
        {
            int h = (int)age.TotalHours;
            return h == 1 ? "1 hour ago" : $"{h} hours ago";
        }

        if (age.TotalDays < 7)
        {
            int d = (int)age.TotalDays;
            return d == 1 ? "yesterday" : $"{d} days ago";
        }

        return local.ToString("yyyy-MM-dd HH:mm");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
