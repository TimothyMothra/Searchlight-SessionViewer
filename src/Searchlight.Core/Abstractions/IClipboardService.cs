namespace Searchlight.Abstractions;

/// <summary>
/// Copies text to the system clipboard. Abstracted so the platform-neutral core can
/// reference it without taking a dependency on the Windows-only clipboard API. The
/// Windows front-end implements this over <c>Windows.ApplicationModel.DataTransfer</c>;
/// a mock implementation is used for the demo/mock data source and tests.
/// </summary>
public interface IClipboardService
{
    /// <summary>
    /// Places <paramref name="text"/> on the system clipboard. Returns <c>true</c>
    /// when the copy succeeded.
    /// </summary>
    bool SetText(string text);
}
