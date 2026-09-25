namespace MeshCoreMessenger.Core.Application;

public interface ISerialPortCatalog
{
    Task<IReadOnlyList<string>> GetPortNamesAsync(CancellationToken cancellationToken = default);
}
