using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.Platform;

/// <summary>Process-owned lock and activation endpoint, created before SQLite or Avalonia startup.</summary>
internal sealed class ApplicationInstanceCoordinator : IDisposable
{
    internal const string DescriptorFileName = ".meshcoremessenger.instance.json";
    private const byte ProtocolVersion = 1;
    private const byte ActivateCommand = 1;
    private const byte ActivatedResponse = 1;
    private const byte UnavailableResponse = 2;
    private readonly ApplicationInstanceLock _instanceLock;
    private readonly Descriptor _descriptor;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    private int _disposed;

    internal DesktopActivationCoordinator Activation { get; }
    private string DescriptorPath => Path.Combine(_instanceLock.DataDirectory, DescriptorFileName);

    private ApplicationInstanceCoordinator(ApplicationInstanceLock instanceLock, DesktopActivationCoordinator activation)
    {
        _instanceLock = instanceLock;
        Activation = activation;
        var id = Guid.NewGuid();
        _descriptor = new(ProtocolVersion, Environment.ProcessId, $"mcm-{id:N}", id);
        var pipe = CreatePipe();
        try
        {
            PublishDescriptor();
            _server = ServeAsync(pipe);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <returns>A primary owner, or null after the existing owner has handled activation.</returns>
    internal static async Task<ApplicationInstanceCoordinator?> AcquireOrActivateAsync(
        DesktopAppPaths paths, DesktopActivationCoordinator activation,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
        Exception? lastError = null;
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                ApplicationInstanceLock? instanceLock = null;
                try
                {
                    instanceLock = ApplicationInstanceLock.Acquire(paths);
                    try { return new ApplicationInstanceCoordinator(instanceLock, activation); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SocketException)
                    { throw new ApplicationActivationException("Could not start the application activation endpoint.", exception); }
                }
                catch (ApplicationInstanceAlreadyRunningException exception) { lastError = exception; }
                catch
                {
                    instanceLock?.Dispose();
                    throw;
                }

                try
                {
                    var descriptor = ReadDescriptor(Path.Combine(paths.DataDirectory, DescriptorFileName));
                    if (descriptor is not null)
                    {
                        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        attempt.CancelAfter(TimeSpan.FromSeconds(1));
                        using var pipe = new NamedPipeClientStream(".", descriptor.PipeName, PipeDirection.InOut,
                            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await pipe.ConnectAsync(attempt.Token).ConfigureAwait(false);
                        var hello = new byte[21];
                        await pipe.ReadExactlyAsync(hello, attempt.Token).ConfigureAwait(false);
                        if (hello[0] != ProtocolVersion || new Guid(hello.AsSpan(1, 16)) != descriptor.InstanceId ||
                            BinaryPrimitives.ReadInt32LittleEndian(hello.AsSpan(17)) != descriptor.ProcessId)
                            throw new IOException("The activation endpoint identity did not match.");
                        WindowsForegroundActivation.GrantToServer(pipe, descriptor.ProcessId);
                        await pipe.WriteAsync(new byte[] { ActivateCommand }, deadline.Token).ConfigureAwait(false);
                        var response = new byte[1];
                        await pipe.ReadExactlyAsync(response, deadline.Token).ConfigureAwait(false);
                        if (response[0] == ActivatedResponse) return null;
                        if (response[0] == UnavailableResponse)
                            throw new ApplicationActivationException("The existing window is closing or unavailable.");
                        throw new IOException("Invalid activation response.");
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or SocketException)
                { lastError = exception; }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                { lastError = new TimeoutException("The activation endpoint did not respond."); }
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApplicationActivationException(
                "Another instance owns this data directory, but its window could not be activated within the timeout.", lastError);
        }
    }

    private NamedPipeServerStream CreatePipe() => new(_descriptor.PipeName, PipeDirection.InOut,
        1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ServeAsync(NamedPipeServerStream firstPipe)
    {
        NamedPipeServerStream? pipe = firstPipe;
        try
        {
            while (pipe is not null && !_stop.IsCancellationRequested)
            {
                using (pipe)
                {
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    try
                    {
                        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        handshake.CancelAfter(TimeSpan.FromSeconds(1));
                        var hello = new byte[21];
                        hello[0] = ProtocolVersion;
                        _descriptor.InstanceId.TryWriteBytes(hello.AsSpan(1, 16));
                        BinaryPrimitives.WriteInt32LittleEndian(hello.AsSpan(17), _descriptor.ProcessId);
                        await pipe.WriteAsync(hello, handshake.Token).ConfigureAwait(false);
                        var command = new byte[1];
                        await pipe.ReadExactlyAsync(command, handshake.Token).ConfigureAwait(false);
                        if (command[0] == ActivateCommand)
                        {
                            using var request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                            request.CancelAfter(TimeSpan.FromSeconds(10));
                            var activated = await Activation.RequestAsync(request.Token).ConfigureAwait(false);
                            await pipe.WriteAsync(new byte[] { activated ? ActivatedResponse : UnavailableResponse }, request.Token)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
                    { /* An abandoned/invalid client must not stop the listener. */ }
                    catch (Exception exception)
                    { Console.Error.WriteLine($"Could not activate the existing window: {exception.Message}"); }
                }
                pipe = null;
                if (!_stop.IsCancellationRequested) pipe = CreatePipe();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Application activation listener stopped: {exception.Message}");
        }
        finally { pipe?.Dispose(); }
    }

    private void PublishDescriptor()
    {
        var temporary = DescriptorPath + $".{_descriptor.InstanceId:N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_descriptor));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, DescriptorPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Descriptor? ReadDescriptor(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length is <= 0 or > 1024) throw new IOException("Invalid activation descriptor size.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        var descriptor = JsonSerializer.Deserialize<Descriptor>(bytes);
        if (descriptor is null || descriptor.Version != ProtocolVersion || descriptor.ProcessId <= 0 ||
            descriptor.InstanceId == Guid.Empty || descriptor.PipeName != $"mcm-{descriptor.InstanceId:N}")
            throw new IOException("Invalid activation descriptor.");
        return descriptor;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Activation.Dispose();
        _stop.Cancel();
        try
        {
            _server.GetAwaiter().GetResult();
            if (File.Exists(DescriptorPath) && ReadDescriptor(DescriptorPath)?.InstanceId == _descriptor.InstanceId)
                File.Delete(DescriptorPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { Console.Error.WriteLine($"Could not remove the activation descriptor: {exception.Message}"); }
        finally
        {
            _stop.Dispose();
            _instanceLock.Dispose();
        }
    }

    private sealed record Descriptor(byte Version, int ProcessId, string PipeName, Guid InstanceId);
}

internal sealed class ApplicationActivationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
