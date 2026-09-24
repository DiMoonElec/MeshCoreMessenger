using System.IO.Ports;

namespace MeshCoreSharp.Transport.Serial;

internal sealed class SerialPortConnection : ISerialConnection
{
    private readonly SerialPort _port;

    public SerialPortConnection(SerialMeshCoreTransportOptions options)
    {
        _port = new SerialPort(options.PortName, options.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = options.DtrEnable,
            RtsEnable = options.RtsEnable,
            ReadTimeout = options.ReadTimeout,
            WriteTimeout = options.WriteTimeout,
        };
    }

    public void Open() => _port.Open();
    public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);
    public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);
    public void Dispose() => _port.Dispose();
}
