using MeshCoreMessenger.Core.Application;
using MeshCoreSharp.Transport.Serial;

namespace MeshCoreMessenger.Desktop.Platform;

public sealed class SystemSerialPortCatalog : ISerialPortCatalog
{
    private readonly Func<string[]> _getPortNames;

    public SystemSerialPortCatalog()
        : this(SerialMeshCoreTransport.GetPortNames)
    {
    }

    internal SystemSerialPortCatalog(Func<string[]> getPortNames)
    {
        _getPortNames = getPortNames ?? throw new ArgumentNullException(nameof(getPortNames));
    }

    public Task<IReadOnlyList<string>> GetPortNamesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ports = _getPortNames()
                .Where(port => !string.IsNullOrWhiteSpace(port))
                .Select(port => port.Trim())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return ports;
        }, cancellationToken);
}
