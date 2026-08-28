using System;
using System.Threading;

namespace KeitaToolbox;

internal readonly record struct AsyncOperationLease(long Generation, CancellationToken Token);

internal sealed class AsyncOperationGate : IDisposable
{
    private readonly object sync = new();
    private CancellationTokenSource cancellation = new();
    private long generation;
    private bool disposed;

    public AsyncOperationLease Begin()
    {
        lock (sync)
        {
            ThrowIfDisposed();
            ReplaceCancellation();
            generation++;
            return new AsyncOperationLease(generation, cancellation.Token);
        }
    }

    public AsyncOperationLease Capture()
    {
        lock (sync)
        {
            ThrowIfDisposed();
            return new AsyncOperationLease(generation, cancellation.Token);
        }
    }

    public bool TryApply(AsyncOperationLease lease, Action action)
    {
        lock (sync)
        {
            if (disposed ||
                lease.Generation != generation ||
                cancellation.IsCancellationRequested)
            {
                return false;
            }

            action();
            return true;
        }
    }

    public void Invalidate()
    {
        lock (sync)
        {
            if (disposed)
                return;

            ReplaceCancellation();
            generation++;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;

            disposed = true;
            generation++;
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private void ReplaceCancellation()
    {
        cancellation.Cancel();
        cancellation.Dispose();
        cancellation = new CancellationTokenSource();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
