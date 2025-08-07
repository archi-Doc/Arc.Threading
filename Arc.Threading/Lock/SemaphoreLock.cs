// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// <see cref="SemaphoreLock"/> is a simplified version of <see cref="SemaphoreSlim"/> (no <see cref="OperationCanceledException"/>).<br/>
/// Used for object mutual exclusion and can also be used in code that includes await syntax.<br/>
/// An instance of <see cref="SemaphoreLock"/> should be a private member since it uses `lock (this)` statement to reduce memory usage.
/// </summary>
public class SemaphoreLock : ILockable, IAsyncLockable
{
    internal const int DefaultSpinCountBeforeWait = 35 * 4;

    private object SyncObject => this; // lock (this) is a bad practice but...

    private bool entered = false;
    private int waitCount;
    private int countOfWaitersPulsedToWake;
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
        var result = false;
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
                result = true;
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

        return task == null ? result : task.GetAwaiter().GetResult();
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
    /// Asynchronously waits to enter the <see cref="SemaphoreLock"/>.
    /// </summary>
    /// <param name="millisecondsTimeout">The duration in milliseconds to wait: -1 for infinite wait, 0 for no wait.</param>
    /// <returns><see langword="true"/>; Entered.<br/>
    /// <see langword="false"/>; Not entered (timeout).</returns>
    public Task<bool> EnterAsync(int millisecondsTimeout)
        => this.EnterAsync(millisecondsTimeout, default);

    /// <summary>
    /// Asynchronously waits to enter the <see cref="SemaphoreLock"/>.
    /// </summary>
    /// <param name="millisecondsTimeout">The duration in milliseconds to wait: -1 for infinite wait, 0 for no wait.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/>; Entered.<br/>
    /// <see langword="false"/>; Not entered (timeout/canceled).</returns>
    public Task<bool> EnterAsync(int millisecondsTimeout, CancellationToken cancellationToken)
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
                if (millisecondsTimeout == 0)
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

                return this.WaitUntilCountOrTimeoutAsync(node, millisecondsTimeout, cancellationToken);
            }
        }
    }

    public void Exit()
    {
        lock (this.SyncObject)
        {
            if (!Volatile.Read(ref this.entered))
            {
                throw new SynchronizationLockException();
            }

            var waitersToNotify = Math.Min(1, this.waitCount) - this.countOfWaitersPulsedToWake;
            if (waitersToNotify == 1)
            {
                this.countOfWaitersPulsedToWake += 1;
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

    private async Task<bool> WaitUntilCountOrTimeoutAsync(TaskNode taskNode, int millisecondsTimeout, CancellationToken cancellationToken)
    {
        if (millisecondsTimeout < -1)
        {
            millisecondsTimeout = -1;
        }

        using (var cts = cancellationToken.CanBeCanceled ?
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, default) :
            new CancellationTokenSource())
        {
            var waitCompleted = Task.WhenAny(taskNode.Task, Task.Delay(millisecondsTimeout, cts.Token));
            if (taskNode.Task == await waitCompleted.ConfigureAwait(false))
            {
                cts.Cancel();
                return true;
            }
        }

        lock (this.SyncObject)
        {
            if (this.RemoveAsyncWaiter(taskNode))
            {
                // cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
        }

        return await taskNode.Task.ConfigureAwait(false);
    }

    private bool RemoveAsyncWaiter(TaskNode task)
    {
        var wasInList = this.head == task || task.Prev != null;

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
