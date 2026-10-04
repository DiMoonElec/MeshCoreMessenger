namespace MeshCoreMessenger.Desktop.ViewModels;

internal static class ContactRouteFormatter
{
    public static string FormatSummary(byte? descriptor, byte[]? path)
    {
        if (descriptor is null) return "неизвестен";
        if (descriptor == byte.MaxValue) return "широковещательный";
        var count = descriptor.Value & 0x3F;
        var size = (descriptor.Value >> 6) + 1;
        if (size > 3 || count * size > (path?.Length ?? 0)) return "неизвестен";
        if (count == 0) return "direct";
        var unit = count % 100 is >= 11 and <= 14 ? "хопов" : (count % 10) switch
        {
            1 => "хоп", 2 or 3 or 4 => "хопа", _ => "хопов",
        };
        return $"{count} {unit}";
    }

    public static string Format(byte? descriptor, byte[]? path)
    {
        if (descriptor is null) return "данные не получены";
        if (descriptor == byte.MaxValue) return "неизвестен · flood";
        var count = descriptor.Value & 0x3F;
        var size = (descriptor.Value >> 6) + 1;
        if (size > 3 || count * size > (path?.Length ?? 0)) return "некорректные данные";
        if (count == 0) return "напрямую";
        return $"хопов: {count}, {size}-байтовые хеши · {Convert.ToHexString(path!.AsSpan(0, count * size)).ToLowerInvariant()}";
    }
}
