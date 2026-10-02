namespace MeshCoreMessenger.Desktop.Preferences;

public sealed record ScreenArea(double X, double Y, double Width, double Height, bool IsPrimary);

public static class WindowPlacementCalculator
{
    public const double MinimumWidth = 560;
    public const double MinimumHeight = 440;

    public static WindowPlacement? Restore(
        WindowPlacement? saved,
        IReadOnlyList<ScreenArea> screens)
    {
        if (saved is null || screens.Count == 0 || !IsValid(saved))
        {
            return null;
        }

        var target = screens
            .Where(IsValid)
            .OrderByDescending(screen => IntersectionArea(saved, screen))
            .FirstOrDefault();
        if (target is null)
        {
            return null;
        }

        if (IntersectionArea(saved, target) <= 0)
        {
            target = screens.FirstOrDefault(screen => screen.IsPrimary && IsValid(screen)) ?? target;
        }

        var width = Math.Clamp(saved.Width, MinimumWidth, target.Width);
        var height = Math.Clamp(saved.Height, MinimumHeight, target.Height);
        var x = Math.Clamp(saved.X, target.X, target.X + target.Width - width);
        var y = Math.Clamp(saved.Y, target.Y, target.Y + target.Height - height);
        if (IntersectionArea(saved, target) <= 0)
        {
            x = target.X + ((target.Width - width) / 2);
            y = target.Y + ((target.Height - height) / 2);
        }

        return new WindowPlacement(x, y, width, height, saved.IsMaximized);
    }

    public static WindowPlacement? Capture(
        WindowPlacement? previousNormal,
        WindowPlacement? currentNormal,
        bool isMaximized)
    {
        var normal = currentNormal is not null && IsValid(currentNormal)
            ? currentNormal with { IsMaximized = false }
            : previousNormal;
        return normal is null || !IsValid(normal)
            ? null
            : normal with { IsMaximized = isMaximized };
    }

    private static bool IsValid(WindowPlacement placement) =>
        IsFinite(placement.X) && IsFinite(placement.Y) &&
        IsFinite(placement.Width) && IsFinite(placement.Height) &&
        placement.Width >= MinimumWidth && placement.Height >= MinimumHeight;

    private static bool IsValid(ScreenArea screen) =>
        IsFinite(screen.X) && IsFinite(screen.Y) &&
        IsFinite(screen.Width) && IsFinite(screen.Height) &&
        screen.Width >= MinimumWidth && screen.Height >= MinimumHeight;

    private static double IntersectionArea(WindowPlacement window, ScreenArea screen)
    {
        var width = Math.Max(0, Math.Min(window.X + window.Width, screen.X + screen.Width) -
            Math.Max(window.X, screen.X));
        var height = Math.Max(0, Math.Min(window.Y + window.Height, screen.Y + screen.Height) -
            Math.Max(window.Y, screen.Y));
        return width * height;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
