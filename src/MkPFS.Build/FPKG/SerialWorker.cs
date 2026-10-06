using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace MkPFS.Build.FPKG;

/// <summary>
/// Runs posted actions one at a time, in order, on a background thread, so sequential work (hashing what was just
/// written) overlaps the producer. The queue is bounded; a failed action is rethrown by the next
/// <see cref="Post"/> or by <see cref="Drain"/>.
/// </summary>
internal sealed class SerialWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(boundedCapacity: 256);
    private readonly Task _task;
    private ExceptionDispatchInfo? _error;

    public SerialWorker() => _task = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Queue an action; blocks while the queue is full.</summary>
    /// <param name="action">Work; must not touch buffers the producer reuses.</param>
    public void Post(Action action)
    {
        _error?.Throw();
        _queue.Add(action);
    }

    /// <summary>Wait until every posted action ran; no more may be posted.</summary>
    public void Drain()
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.CompleteAdding();
        }

        _task.GetAwaiter().GetResult();
        _error?.Throw();
    }

    public void Dispose()
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.CompleteAdding();
        }

        _task.ConfigureAwait(false).GetAwaiter().GetResult();
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (Action action in _queue.GetConsumingEnumerable())
        {
            // After a failure the queue is still emptied, so a producer blocked in Add wakes up and sees the error.
            if (_error is not null)
            {
                continue;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                _error = ExceptionDispatchInfo.Capture(ex);
            }
        }
    }
}
