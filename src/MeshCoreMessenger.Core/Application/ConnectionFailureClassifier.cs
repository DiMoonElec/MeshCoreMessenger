using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Exceptions;

namespace MeshCoreMessenger.Core.Application;

public enum ConnectionFailureDisposition
{
    Transient,
    NeedsAttention,
}

public interface IConnectionFailureClassifier
{
    ConnectionFailureDisposition Classify(Exception exception);
}

public sealed class ConnectionFailureClassifier : IConnectionFailureClassifier
{
    public ConnectionFailureDisposition Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var current = Unwrap(exception);
        return current switch
        {
            NodeIdentityMismatchException => ConnectionFailureDisposition.NeedsAttention,
            ConnectionAttemptPersistenceException => ConnectionFailureDisposition.NeedsAttention,
            ReceiveIngestException => ConnectionFailureDisposition.NeedsAttention,
            DatabaseStorageException => ConnectionFailureDisposition.NeedsAttention,
            SqliteException => ConnectionFailureDisposition.NeedsAttention,
            NotSupportedException => ConnectionFailureDisposition.NeedsAttention,
            ArgumentException => ConnectionFailureDisposition.NeedsAttention,
            MeshCoreProtocolException => ConnectionFailureDisposition.NeedsAttention,
            MeshCoreCommandException => ConnectionFailureDisposition.NeedsAttention,
            ReceiveDrainTimeoutException => ConnectionFailureDisposition.Transient,
            MeshCoreTransportException => ConnectionFailureDisposition.Transient,
            MeshCoreTimeoutException => ConnectionFailureDisposition.Transient,
            SocketException => ConnectionFailureDisposition.Transient,
            TimeoutException => ConnectionFailureDisposition.Transient,
            IOException => ConnectionFailureDisposition.Transient,
            _ => ConnectionFailureDisposition.NeedsAttention,
        };
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
        {
            exception = aggregate.InnerExceptions[0];
        }

        return exception;
    }
}
