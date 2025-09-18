// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// <see cref="SemaphoreLock"/> is a simplified version of <see cref="SemaphoreSlim"/>.<br/>
/// Used for object mutual exclusion and can also be used in code that includes await syntax.<br/>
/// An instance of <see cref="SemaphoreLock"/> should be a private member since it uses `lock (this)` statement to reduce memory usage.<br/>
/// The size of this struct is 40 bytes (37 bytes internally).
/// </summary>
public class SemaphoreLock : ILockable, IAsyncLockable
{// object:16, 1+2+2+8+8 -> 37
    internal const int DefaultSpinCountBeforeWait = 35 * 4;

    private object SyncObject => this; // lock (this) is a bad practice but...

    private bool entered = false;
    private ushort countOfWaitersPulsedToWake; // int -> ushort
    private ushort waitCount; // int -> ushort
    private TaskNode? head;
    private TaskNode? tail;

    public SemaphoreLock()
    {
    }

    public LockStruct EnterScope()
        => new LockStruct(this);

    public bool IsLocked => Volatile.Read(ref this.entered);

    /// <summary>
    /// Attempts to acquires an exclusive lock without blocking.<br/>
    /// If an exclusive lock is already held by another thread, return <see langword="false"/> without waiting.
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
    /// <returns><see langword="true"/>; Entered.</returns>
    public bool Enter()
    {
        var lockTaken = false;
        Task<bool>? task = null;

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

            if (this.head is not null)
            {// Async waiters.
                task = this.EnterAsync();
            }
            else
            {// No async waiters.
                while (Volatile.Read(ref this.entered))
                {
                    Monitor.Wait(this.SyncObject);
                    if (this.countOfWaitersPulsedToWake != 0)
                    {
                        this.countOfWaitersPulsedToWake--;
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
                Monitor.Exit(this.SyncObject);
            }
        }

        return task == null ? true : task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously waits to enter the <see cref="SemaphoreLock"/>.
    /// </summary>
    /// <returns><see langword="true"/>; Entered.</returns>
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
                var node = new TaskNode();

                if (this.head == null)
                {
                    this.head = node;
                    this.tail = node;
                }
                else
                {
                    this.tail!.Next = node;
                    node.Prev = this.tail;
                    this.tail = node;
                }

                return node.Task;
            }
        }
    }

    /// <summary>
    /// Asynchronously waits to enter the <see cref="SemaphoreLock"/> with a specified timeout and cancellation token.
    /// </summary>
    /// <param name="timeoutInMilliseconds">The duration in milliseconds to wait: -1 for infinite wait, 0 for no wait.</param>
    /// <returns>
    /// A task that returns <see langword="true"/> if the lock was acquired; otherwise, <see langword="false"/> if the timeout elapsed or the operation was canceled.
    /// </returns>
    public Task<bool> EnterAsync(int timeoutInMilliseconds)
        => this.EnterAsync(TimeSpan.FromMilliseconds(timeoutInMilliseconds), default);

    /// <summary>
    /// Asynchronously waits to enter the <see cref="SemaphoreLock"/> with a specified timeout and cancellation token.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the lock.<br/>
    /// <see cref="TimeSpan.Zero"/>: The method returns immediately.<br/>
    /// <see cref="Timeout.InfiniteTimeSpan"/>: The method waits indefinitely until the lock is acquired.
    /// </param>
    /// <returns>
    /// A task that returns <see langword="true"/> if the lock was acquired; otherwise, <see langword="false"/> if the timeout elapsed or the operation was canceled.
    /// </returns>
    public Task<bool> EnterAsync(TimeSpan timeout)
        => this.EnterAsync(timeout, default);

    /// <summary>
    /// Asynchronously waits to enter the <see cref="SemaphoreLock"/> with a specified cancellation token.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while waiting for the lock to be acquired.</param>
    /// <returns>
    /// A task that returns <see langword="true"/> if the lock was acquired; otherwise, <see langword="false"/> if the timeout elapsed or the operation was canceled.
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
    public Task<bool> EnterAsync(TimeSpan timeout, CancellationToken cancellationToken)
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
                if (timeout == TimeSpan.Zero)
                {// No waiting
                    return Task.FromResult(false);
                }

                var node = new TaskNode();

                if (this.head == null)
                {
                    this.head = node;
                    this.tail = node;
                }
                else
                {
                    this.tail!.Next = node;
                    node.Prev = this.tail;
                    this.tail = node;
                }

                return (timeout == Timeout.InfiniteTimeSpan && !cancellationToken.CanBeCanceled) ?
                    node.Task :
                    this.WaitUntilCountOrTimeoutAsync(node, timeout, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Releases the exclusive lock held by the current thread or task.
    /// </summary>
    public void Exit()
    {
        lock (this.SyncObject)
        {
            if (!Volatile.Read(ref this.entered))
            {
                throw new SynchronizationLockException();
            }

            var waitersToNotify = Math.Min((ushort)1, this.waitCount) - this.countOfWaitersPulsedToWake;
            if (waitersToNotify > 0)
            {// waitersToNotify == 1
                this.countOfWaitersPulsedToWake++;
                Monitor.Pulse(this.SyncObject);
            }

            if (this.head is not null && this.waitCount == 0)
            {
                var waiterTask = this.head;
                this.RemoveAsyncWaiter(waiterTask);
                waiterTask.TrySetResult(result: true);
            }
            else
            {
                Volatile.Write(ref this.entered, false);
            }
        }
    }

    private async Task<bool> WaitUntilCountOrTimeoutAsync(TaskNode asyncWaiter, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await ((Task)asyncWaiter.Task.WaitAsync(timeout, cancellationToken)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (cancellationToken.IsCancellationRequested)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

        if (asyncWaiter.Task.IsCompleted)
        {
            return true;
        }

        lock (this.SyncObject)
        {
            if (this.RemoveAsyncWaiter(asyncWaiter))
            {
                cancellationToken.ThrowIfCancellationRequested();
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
