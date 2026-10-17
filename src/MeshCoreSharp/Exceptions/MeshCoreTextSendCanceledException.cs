namespace MeshCoreSharp.Exceptions;

/// <summary>Cancellation before the text SEND invocation; no text was transmitted by this operation.</summary>
public sealed class MeshCoreTextSendCanceledException(OperationCanceledException error)
    : OperationCanceledException(error.Message, error, error.CancellationToken);
