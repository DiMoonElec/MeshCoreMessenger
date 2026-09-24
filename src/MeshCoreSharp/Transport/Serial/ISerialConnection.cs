namespace MeshCoreSharp.Transport.Serial;

// Byte I/O seam for testing without opening a physical device. Implementations must
// bound Read/Write with the configured timeouts; Dispose runs only after I/O stops.
internal interface ISerialConnection : IDisposable
{
    void Open();
    int Read(byte[] buffer, int offset, int count);
    void Write(byte[] buffer, int offset, int count);
}
