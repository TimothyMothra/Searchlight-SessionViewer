namespace Searchlight.ViewModels;

/// <summary>Uses the effective viewport in element-local coordinates, without loading data.</summary>
public static class MetadataViewport
{
    public static bool Intersects(double elementWidth, double elementHeight,
        double viewportX, double viewportY, double viewportWidth, double viewportHeight) =>
        elementWidth > 0 && elementHeight > 0 && viewportWidth > 0 && viewportHeight > 0
        && viewportX < elementWidth && viewportX + viewportWidth > 0
        && viewportY < elementHeight && viewportY + viewportHeight > 0;
}
