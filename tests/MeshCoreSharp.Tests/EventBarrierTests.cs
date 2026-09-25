using MeshCoreSharp;
using MeshCoreSharp.Runtime;

internal static class EventBarrierTests
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Event barrier waits for prior callbacks", BasicBarrier),
        ("Event barrier waits for a delayed callback", DelayedCallback),
        ("Callbacks after an event barrier do not delay it", EventsAfterBarrier),
        ("Event barrier drains callbacks after RX stops", RxStopped),
        ("Event barrier cancellation preserves the queue", Cancellation),
        ("Client lifecycle from a callback does not deadlock", CallbackLifecycle),
        ("Dispose does not implicitly wait for callbacks", DisposeDoesNotFlush),
        ("Repeated event barriers keep FIFO positions", RepeatedFlush),
        ("Event barrier does not wait for detached async work", DetachedAsyncWork),
        ("Accepted event barrier drains after queue completion", QueueCompletionRace),
    ];

    private static async Task BasicBarrier()
    {
        await using var transport = new TestTransport();
        var client = await ConnectedClient(transport);
        var callbacks = new List<MeshCoreConnectionState>();
        client.ConnectionStateChanged += (_, args) => callbacks.Add(args.CurrentState);

        try
        {
            await client.DisconnectAsync();
            await client.FlushEventsAsync();
            Ensure(
                callbacks.SequenceEqual(
                    [MeshCoreConnectionState.Disconnecting, MeshCoreConnectionState.Disconnected]),
                "Barrier returned before prior callbacks completed in order.");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private static async Task DelayedCallback()
    {
        using var queue = new ClientEventQueue();
        using var release = new ManualResetEventSlim();
        var entered = NewCompletion();
        var returned = NewCompletion();
        queue.Post(() =>
        {
            entered.TrySetResult();
            release.Wait();
            returned.TrySetResult();
        });

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var flush = queue.FlushAsync(CancellationToken.None);
            Ensure(!flush.IsCompleted, "Barrier passed a callback that had not returned.");
            release.Set();
            await flush.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(returned.Task.IsCompletedSuccessfully, "Delayed callback did not return before the barrier.");
        }
        finally
        {
            release.Set();
        }
    }

    private static async Task EventsAfterBarrier()
    {
        using var queue = new ClientEventQueue();
        using var releasePrior = new ManualResetEventSlim();
        using var releaseLater = new ManualResetEventSlim();
        var priorEntered = NewCompletion();
        var laterEntered = NewCompletion();
        var laterReturned = NewCompletion();
        queue.Post(() =>
        {
            priorEntered.TrySetResult();
            releasePrior.Wait();
        });

        try
        {
            await priorEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var flush = queue.FlushAsync(CancellationToken.None);
            queue.Post(() =>
            {
                laterEntered.TrySetResult();
                releaseLater.Wait();
                laterReturned.TrySetResult();
            });

            releasePrior.Set();
            await laterEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await flush.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!laterReturned.Task.IsCompleted, "Barrier waited for a callback queued after its marker.");
        }
        finally
        {
            releasePrior.Set();
            releaseLater.Set();
            if (laterEntered.Task.IsCompleted)
                await laterReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task RxStopped()
    {
        await using var transport = new TestTransport();
        var client = await ConnectedClient(transport);
        using var release = new ManualResetEventSlim();
        var entered = NewCompletion();
        var returned = NewCompletion();
        client.ConnectionStateChanged += (_, args) =>
        {
            if (args.CurrentState != MeshCoreConnectionState.Disconnecting)
                return;

            entered.TrySetResult();
            release.Wait();
            returned.TrySetResult();
        };

        try
        {
            var disconnect = client.DisconnectAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await disconnect.WaitAsync(TimeSpan.FromSeconds(2));

            var flush = client.FlushEventsAsync();
            Ensure(!flush.IsCompleted, "Barrier passed a callback after RX stopped.");
            release.Set();
            await flush.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(returned.Task.IsCompletedSuccessfully, "Queued callback did not finish after RX stopped.");
        }
        finally
        {
            release.Set();
            await client.DisposeAsync();
        }
    }

    private static async Task Cancellation()
    {
        await using var transport = new TestTransport();
        var client = await ConnectedClient(transport);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = NewCompletion();
        client.ConnectionStateChanged += (_, args) =>
        {
            if (args.CurrentState != MeshCoreConnectionState.Disconnecting)
                return;

            entered.TrySetResult();
            release.Wait();
        };

        try
        {
            var disconnect = client.DisconnectAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await disconnect.WaitAsync(TimeSpan.FromSeconds(2));
            var flush = client.FlushEventsAsync(cancellation.Token);
            cancellation.Cancel();
            await Throws<OperationCanceledException>(() => flush);

            release.Set();
            await client.FlushEventsAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.Set();
            await client.DisposeAsync();
        }
    }

    private static async Task CallbackLifecycle()
    {
        await using var transport = new TestTransport();
        var client = await ConnectedClient(transport);
        var disposedFromCallback = NewCompletion();
        client.ConnectionStateChanged += (_, args) =>
        {
            if (args.CurrentState != MeshCoreConnectionState.Disconnecting)
                return;

            try
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
                disposedFromCallback.TrySetResult();
            }
            catch (Exception exception)
            {
                disposedFromCallback.TrySetException(exception);
            }
        };

        try
        {
            var disconnect = client.DisconnectAsync();
            await disconnect.WaitAsync(TimeSpan.FromSeconds(2));
            await disposedFromCallback.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private static async Task DisposeDoesNotFlush()
    {
        await using var transport = new TestTransport();
        var client = await ConnectedClient(transport);
        using var release = new ManualResetEventSlim();
        var entered = NewCompletion();
        var returned = NewCompletion();
        client.ConnectionStateChanged += (_, args) =>
        {
            if (args.CurrentState != MeshCoreConnectionState.Disconnecting)
                return;

            entered.TrySetResult();
            release.Wait();
            returned.TrySetResult();
        };

        try
        {
            var disconnect = client.DisconnectAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await disconnect.WaitAsync(TimeSpan.FromSeconds(2));
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!returned.Task.IsCompleted, "Dispose unexpectedly waited for the blocked callback.");
            await Throws<ObjectDisposedException>(() => client.FlushEventsAsync());
        }
        finally
        {
            release.Set();
            if (entered.Task.IsCompleted)
                await returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await client.DisposeAsync();
        }
    }

    private static async Task RepeatedFlush()
    {
        using var queue = new ClientEventQueue();
        var count = 0;
        for (var expected = 1; expected <= 4; expected++)
        {
            queue.Post(() => count++);
            await queue.FlushAsync(CancellationToken.None);
            Ensure(count == expected, $"Barrier {expected} did not preserve its FIFO position.");
        }
    }

    private static async Task DetachedAsyncWork()
    {
        using var queue = new ClientEventQueue();
        var release = NewCompletion();
        var detachedCompleted = NewCompletion();
        queue.Post(() => _ = CompleteLater());

        await queue.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!detachedCompleted.Task.IsCompleted, "Barrier waited for async work started by a returned callback.");
        release.TrySetResult();
        await detachedCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        async Task CompleteLater()
        {
            await release.Task.ConfigureAwait(false);
            detachedCompleted.TrySetResult();
        }
    }

    private static async Task QueueCompletionRace()
    {
        var queue = new ClientEventQueue();
        using var release = new ManualResetEventSlim();
        var entered = NewCompletion();
        queue.Post(() =>
        {
            entered.TrySetResult();
            release.Wait();
        });

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var accepted = queue.FlushAsync(CancellationToken.None);
            queue.Dispose();
            release.Set();
            await accepted.WaitAsync(TimeSpan.FromSeconds(2));
            await Throws<ObjectDisposedException>(() => queue.FlushAsync(CancellationToken.None));
        }
        finally
        {
            release.Set();
            queue.Dispose();
        }
    }

    private static async Task<MeshCoreClient> ConnectedClient(TestTransport transport)
    {
        var client = new MeshCoreClient(transport, new MeshCoreClientOptions
        {
            AutoReceiveMessages = false,
        });
        await client.ConnectAsync();
        return client;
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Throws<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
