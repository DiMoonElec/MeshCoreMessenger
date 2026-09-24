namespace MeshCoreSharp.Transport.Framing;

internal sealed class StreamFrameDecoder
{
    private readonly byte _marker;
    private readonly int _maximumPayloadLength;
    private readonly List<byte> _buffer = [];

    public StreamFrameDecoder(byte marker, int maximumPayloadLength)
    {
        if (maximumPayloadLength <= 0 || maximumPayloadLength > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadLength));

        _marker = marker;
        _maximumPayloadLength = maximumPayloadLength;
    }

    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
            _buffer.Add(bytes[i]);

        var frames = new List<byte[]>();

        while (true)
        {
            var markerIndex = _buffer.IndexOf(_marker);
            if (markerIndex < 0)
            {
                // No possible frame start remains in the current stream data.
                _buffer.Clear();
                break;
            }

            if (markerIndex > 0)
                _buffer.RemoveRange(0, markerIndex);

            if (_buffer.Count < 3)
                break;

            var payloadLength = _buffer[1] | (_buffer[2] << 8);
            if (payloadLength <= 0 || payloadLength > _maximumPayloadLength)
            {
                // Invalid candidate header. Discard only the marker so that a
                // later marker in the stream can be used to resynchronize.
                _buffer.RemoveAt(0);
                continue;
            }

            var completeLength = 3 + payloadLength;
            if (_buffer.Count < completeLength)
                break;

            frames.Add(_buffer.GetRange(3, payloadLength).ToArray());
            _buffer.RemoveRange(0, completeLength);
        }

        return frames;
    }

    public void Reset() => _buffer.Clear();
}
