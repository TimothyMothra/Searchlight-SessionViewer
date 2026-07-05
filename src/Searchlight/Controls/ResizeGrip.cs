using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Searchlight.Controls;

/// <summary>
/// A <see cref="Grid"/> that shows the diagonal NWSE resize cursor whenever the pointer is
/// over it. WinUI 3 only lets an element set its own <see cref="Microsoft.UI.Xaml.UIElement.ProtectedCursor"/>
/// (the setter is protected), so the cursor affordance for the bottom-right resize grip must
/// live in a derived type rather than on the <c>MainView</c> code-behind.
///
/// The cursor is the primary discoverability and accessibility cue that the corner is grabbable —
/// a bare transparent hit-target with no cursor change gives the user nothing to react to. Paired
/// with an oversized (versus the visual dots) transparent hit area, this makes the grip easy to
/// grab without hunting for a pixel-perfect corner.
/// </summary>
public sealed partial class ResizeGrip : Grid
{
    public ResizeGrip()
    {
        // NWSE = the diagonal double-arrow the OS shows on a real window's bottom-right corner.
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
    }
}
