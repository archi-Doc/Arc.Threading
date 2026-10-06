// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Provides a non-reentrant exclusive lock for synchronous and asynchronous callers.
/// </summary>
/// <remarks>Keep instances private: internal synchronization locks the instance itself. Ownership is not tracked by thread or task.</remarks>
public class SemaphoreLock : ILockable, IAsyncLockable
{// object:16, 1+2+4+8+8 -> 39
    private const int DefaultSpinCountBeforeWait = 35 * 4;

    private object SyncObject => this; // lock (this) is a bad practice but...

    private bool entered = false;
    private ushort countOfWaitersPulsedToWake; // int -> ushort
    private int waitCount;
    private TaskNode? head;
    private TaskNode? tail;

    /// <summary>
    /// Initializes a new instance of the <see cref="SemaphoreLock"/> class.
    /// </summary>
    public SemaphoreLock()
    {
    }

    /// <summary>
    /// Acquires an exclusive lock and creates a <see cref="LockScope"/> for a using statement.
    /// </summary>
    /// <returns><see cref="LockScope"/>.</returns>
    public LockScope EnterScope()
        => new LockScope(this);

    /// <summary>
    /// Asynchronously acquires an exclusive lock and creates a <see cref="LockScope"/> for a using statement.
    /// </summary>
    /// <returns><see cref="LockScope"/>.</returns>
    public async Task<LockScope> EnterScopeAsync()
        => new(this, await this.EnterAsync().ConfigureAwait(false));

    /// <summary>
    /// Gets a value indicating whether the exclusive lock has been acquired.
    /// </summary>
    public bool IsLocked => Volatile.Read(ref this.entered);

    /// <summary>
    /// Attempts to acquire the exclusive lock without waiting for it to be released.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the lock was successfully acquired; otherwise, <see langword="false"/>.
    /// </returns>
    public bool TryEnter()
    {
        lock (this.SyncObject)
        {
            if (!Volatile.Read(ref this.entered))
            {
                Volatile.Write(ref this.entered, true);
                return true;
            }
            else
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Blocks the current thread until it can enter the <see cref="SemaphoreLock"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the lock is acquired.</returns>
    /// <exception cref="ThreadInterruptedException">The thread is interrupted while waiting.</exception>
    public bool Enter()
    {
        var lockTaken = false;
        TaskNode? asyncWaiter = null;

        try
        {
            if (Volatile.Read(ref this.entered))
            {
                var spinCount = DefaultSpinCountBeforeWait; // SpinWait.SpinCountforSpinBeforeWait * 4
                SpinWait spinner = default;
                while (spinner.Count < spinCount)
                {
                    spinner.SpinOnce(sleep1Threshold: -1);
                    if (!Volatile.Read(ref this.entered))
                    {
                        break;
                    }
                }
            }

            Monitor.Enter(this.SyncObject, ref lockTaken);
            this.waitCount++;

            if (this.head is not null && Volatile.Read(ref this.entered))
            {// Async waiters.
                asyncWaiter = this.AddAsyncWaiter();
            }
            else
            {// No async waiters.
                while (Volatile.Read(ref this.entered))
                {
                    try
                    {
                        Monitor.Wait(this.SyncObject);
                    }
                    finally
                    {
                        if (this.countOfWaitersPulsedToWake != 0)
                        {
                            this.countOfWaitersPulsedToWake--;
                        }
                    }
                }

                Volatile.Write(ref this.entered, true);
            }
        }
        finally
        {
            if (lockTaken)
            {
                this.waitCount--;
                if (!Volatile.Read(ref this.entered))
                {
                    // An interrupted waiter must pass an available lock to the next waiter.
                    this.ReleaseNextWaiter();
                }

                Monitor.Exit(this.SyncObject);
            }
        }

        if (asyncWaiter is null)
        {
            return true;
        }

        try
        {
            return asyncWaiter.Task.GetAwaiter().GetResult();
        }
        catch (ThreadInterruptedException)
        {
            lock (this.SyncObject)
            {
                if (!this.RemoveAsyncWaiter(asyncWaiter))
                {
                    // A concurrent release may already have transferred ownership to this waiter.
                    this.ReleaseNextWaiter();
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Asynchronously waits to enter the <see cref="SemaphoreLock"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the lock is acquired.</returns>
    public Task<bool> EnterAsync()
    {
        lock (this.SyncObject)
        {
            if (!Volatile.Read(ref this.entered))
            {
                Volatile.Write(ref this.entered, true);
                return Task.FromResult(true);
            }
            else
            {
                return this.AddAsyncWaiter().Task;
            }
        }
    }

    /// <summary>
    /// Asynchronously waits to acquire the lock within the specified timeout.
    /// </summary>
    /// <param name="millisecondsTimeout">The duration in milliseconds to wait: -1 for infinite wait, 0 for no wait.</param>
    /// <returns>
    /// A task that returns <see langword="true"/> if the lock was acquired; otherwise, <see langword="false"/> if the timeout elapsed.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is less than -1.</exception>
    public Task<bool> EnterAsync(int millisecondsTimeout)
        => this.EnterAsync(TimeSpan.FromMilliseconds(millisecondsTimeout), default);

    /// <summary>
    /// Asynchronously waits to acquire the lock within the specified timeout.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the lock.<br/>
    /// <see cref="TimeSpan.Zero"/>: The method returns immediately.<br/>
    /// <see cref="Timeout.InfiniteTimeSpan"/>: The method waits indefinitely until the lock is acquired.
    /// </param>
    /// <returns>
    /// A task that returns <see langword="true"/> if the lock was acquired; otherwise, <see langword="false"/> if the timeout elapsed.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative other than -1 ms, or exceeds 4,294,967,294 milliseconds.</exception>
    public Task<bool> EnterAsync(TimeSpan timeout)
        => this.EnterAsync(timeout, default);

    /// <summary>
    /// Asynchronously waits to enter the <see cref="SemaphoreLock"/> with a specified cancellation token.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while waiting for the lock to be acquired.</param>
    /// <returns>
    /// A task that returns <see langword="true"/> if the lock was acquired; otherwise, <see langword="false"/> if the operation was canceled.
    /// </returns>
    public Task<bool> EnterAsync(CancellationToken cancellationToken)
        => this.EnterAsync(Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>
    /// Asynchronously waits to enter the <see cref="SemaphoreLock"/> with a specified timeout and cancellation token.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the lock.<br/>
    /// <see cref="TimeSpan.Zero"/>: The method returns immediately.<br/>
    /// <see cref="Timeout.InfiniteTimeSpan"/>: The method waits indefinitely until the lock is acquired.
    /// </param>
    /// <param name="cancellationToken">A token to observe while waiting for the lock to be acquired.</param>
    /// <returns>
    /// A task that returns <see langword="true"/> if the lock was acquired; otherwise, <see langword="false"/> if the timeout elapsed or the operation was canceled.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative other than -1 ms, or exceeds 4,294,967,294 milliseconds.</exception>
    public Task<bool> EnterAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if ((timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) || timeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(false);
        }

        lock (this.SyncObject)
        {
            if (!Volatile.Read(ref this.entered))
            {
                Volatile.Write(ref this.entered, true);
                return Task.FromResult(true);
            }
            else
            {
                if (timeout == TimeSpan.Zero)
                {// No waiting
                    return Task.FromResult(false);
                }

                var node = this.AddAsyncWaiter();

                return (timeout == Timeout.InfiniteTimeSpan && !cancellationToken.CanBeCanceled) ?
                    node.Task :
                    this.WaitUntilCountOrTimeoutAsync(node, timeout, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Releases the exclusive lock. The caller must hold the lock.
    /// </summary>
    /// <exception cref="SynchronizationLockException">The lock is not held.</exception>
    public void Exit()
    {
        lock (this.SyncObject)
        {
            if (!Volatile.Read(ref this.entered))
            {
                throw new SynchronizationLockException();
            }

            this.ReleaseNextWaiter();
        }
    }

    private void ReleaseNextWaiter()
    {
        if (this.waitCount > 0 && this.countOfWaitersPulsedToWake == 0)
        {// waitersToNotify == 1
            this.countOfWaitersPulsedToWake++;
            Monitor.Pulse(this.SyncObject);
        }

        if (this.head is not null && this.waitCount == 0)
        {
            var waiterTask = this.head;
            this.RemoveAsyncWaiter(waiterTask);
            Volatile.Write(ref this.entered, true);
            waiterTask.TrySetResult(result: true);
        }
        else
        {
            Volatile.Write(ref this.entered, false);
        }
    }

    private TaskNode AddAsyncWaiter()
    {
        var node = new TaskNode();
        if (this.head is null)
        {
            this.head = node;
        }
        else
        {
            this.tail!.Next = node;
            node.Prev = this.tail;
        }

        this.tail = node;
        return node;
    }

    private async Task<bool> WaitUntilCountOrTimeoutAsync(TaskNode asyncWaiter, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await ((Task)asyncWaiter.Task.WaitAsync(timeout, cancellationToken)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (asyncWaiter.Task.IsCompleted)
        {
            return true;
        }

        lock (this.SyncObject)
        {
            if (this.RemoveAsyncWaiter(asyncWaiter))
            {
                return false;
            }
        }

        return await asyncWaiter.Task.ConfigureAwait(false);
    }

    private bool RemoveAsyncWaiter(TaskNode task)
    {
        var wasInList = this.head == task || task.Prev != null; // True if the task was in the list.

        if (task.Next is not null)
        {
            task.Next.Prev = task.Prev;
        }

        if (task.Prev is not null)
        {
            task.Prev.Next = task.Next;
        }

        if (this.head == task)
        {
            this.head = task.Next;
        }

        if (this.tail == task)
        {
            this.tail = task.Prev;
        }

        task.Next = null;
        task.Prev = null;

        return wasInList;
    }

    private sealed class TaskNode : TaskCompletionSource<bool>
    {
#pragma warning disable SA1401 // Fields should be private
        internal TaskNode? Prev;
        internal TaskNode? Next;
#pragma warning restore SA1401 // Fields should be private

        internal TaskNode()
            : base(null, TaskCreationOptions.RunContinuationsAsynchronously)
        {
        }
    }
}
