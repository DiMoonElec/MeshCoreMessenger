using System.Globalization;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Presentation of committed facts only; no radio queries or inferred route length.</summary>
public sealed class DeviceDetailsViewModel(ContactDetailsProjection details)
{
    public string Name { get; } = DeviceListItem.DisplayName(details.DisplayName);
    public string Type { get; } = DeviceListItem.DescribeType(details.ContactType);
    public string PublicKey { get; } = Convert.ToHexString(details.PublicKey).ToLowerInvariant();
    public string Presence { get; } = details.PresentOnNode ? "В справочнике ноды" : "Сохранено локально · отсутствует в текущем справочнике";
    public string LastAdvert { get; } = FormatDate(details.LastAdvertUtc);
    public string Updated { get; } = FormatDate(details.UpdatedUtc);
    public string Coordinates { get; } = details.Latitude is { } latitude && details.Longitude is { } longitude
        ? string.Create(CultureInfo.InvariantCulture, $"{latitude:F5}, {longitude:F5}") : "Нет сохранённых данных";
    public string CoordinatesNote { get; } = details.Latitude == 0 && details.Longitude == 0
        ? "Сохранено 0,0: текущие данные не позволяют подтвердить наличие геопозиции." : string.Empty;
    public string Flags { get; } = $"0x{details.Flags:X2} ({details.Flags})";
    public string RouteDescriptor { get; } = details.OutPathLength is { } descriptor ? $"0x{descriptor:X2}" : "Нет сохранённых данных";
    public string RawRoute { get; } = FormatBytes(details.OutPath);
    public string RawAdvert { get; } = FormatBytes(details.AdvertPayload);
    private static string FormatBytes(byte[]? data) => data is { Length: > 0 }
        ? Convert.ToHexString(data).ToLowerInvariant() : "Нет сохранённых данных";
    private static string FormatDate(DateTimeOffset? date) => date?.ToLocalTime().ToString("g") ?? "Нет сохранённых данных";
}

public sealed class DeviceListItem
{
    public DeviceListItem(ConversationDirectoryEntry entry)
    {
        NodeId = entry.NodeId;
        PublicKey = entry.Identity.ToArray();
        Name = DisplayName(entry.DisplayName);
        Type = DescribeType(entry.ContactType ?? 0);
        IsPresent = entry.PresentOnNode == true;
    }
    public DeviceListItem(ContactDetailsProjection details)
    {
        NodeId = details.NodeId;
        PublicKey = details.PublicKey.ToArray();
        Name = DisplayName(details.DisplayName);
        Type = DescribeType(details.ContactType);
        IsPresent = details.PresentOnNode;
    }
    public Guid NodeId { get; }
    internal byte[] PublicKey { get; }
    public string Key => Convert.ToHexString(PublicKey).ToLowerInvariant();
    public string Name { get; }
    public string Type { get; }
    public bool IsPresent { get; }
    public bool IsSavedOnly => !IsPresent;
    internal static string DisplayName(string? name) => string.IsNullOrWhiteSpace(name) ? "Устройство без имени" : name;
    internal static string DescribeType(int type) => type switch
    {
        0 => "Без типа", 1 => "Компаньон", 2 => "Ретранслятор", 3 => "Комната", 4 => "Датчик",
        _ => $"Устройство (тип {type})",
    };
}
