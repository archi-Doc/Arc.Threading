// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/*
/// <summary>
/// <see cref="SemaphoreLock2"/> is a simplified version of <see cref="SemaphoreSlim"/>.<br/>
/// Used for object mutual exclusion and can also be used in code that includes await syntax.<br/>
/// An instance of <see cref="SemaphoreLock2"/> should be a private member since it uses `lock (this)` statement to reduce memory usage.
/// </summary>
public class SemaphoreLock2 : ILockable
{
    internal const int DefaultSpinCountBeforeWait = 35 * 4;

    private object SyncObject => this; // lock (this) is a bad practice but...

    private volatile bool entered = false;
    private int waitCount;
    private int countOfWaitersPulsedToWake;
    private TaskNode? head;
    private TaskNode? tail;

    public SemaphoreLock2()
    {
    }

    public LockStruct Lock()
        => new LockStruct(this);

    public bool IsLocked => this.entered;

    public bool TryFastEnter()
    {
        lock (this.SyncObject)
        {
            if (this.entered)
            {
                return false;
            }
            else
            {
                this.entered = true;
                return true;
            }
        }
    }

    public bool Enter()
    {
        var lockTaken = false;
        var result = false;
        ValueTask<bool> task = default;

        try
        {
            if (this.entered)
            {
                var spinCount = DefaultSpinCountBeforeWait; // SpinWait.SpinCountforSpinBeforeWait * 4
                SpinWait spinner = default;
                while (spinner.Count < spinCount)
                {
                    spinner.SpinOnce(sleep1Threshold: -1);
                    if (!this.entered)
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
                while (this.entered)
                {
                    Monitor.Wait(this.SyncObject);
                    if (this.countOfWaitersPulsedToWake != 0)
                    {
                        this.countOfWaitersPulsedToWake--;
                    }
                }

                this.entered = true;
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

        return task == default ? result : task.GetAwaiter().GetResult();
    }

    public ValueTask<bool> EnterAsync()
    {
        lock (this.SyncObject)
        {
            if (!this.entered)
            {
                this.entered = true;
                return ValueTask.FromResult(true);
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

                return new(node.Task);
            }
        }
    }

    public ValueTask<bool> EnterAsync(int millisecondsTimeout)
        => this.EnterAsync(millisecondsTimeout, default);

    public ValueTask<bool> EnterAsync(int millisecondsTimeout, CancellationToken cancellationToken)
    {
        lock (this.SyncObject)
        {
            if (!this.entered)
            {
                this.entered = true;
                return ValueTask.FromResult(true);
            }
            else
            {
                if (millisecondsTimeout == 0)
                {// No waiting
                    return ValueTask.FromResult(false);
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

                return new(this.WaitUntilCountOrTimeoutAsync(node, millisecondsTimeout, cancellationToken));
            }
        }
    }

    public void Exit()
    {
        lock (this.SyncObject)
        {
            if (!this.entered)
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
                this.entered = false;
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
                cancellationToken.ThrowIfCancellationRequested();
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
}*/
