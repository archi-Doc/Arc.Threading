// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1202

namespace Arc.Threading;

internal static class SemaphoreDualTask
{
    private const int IntervalInMilliseconds = 1_000;

    static SemaphoreDualTask()
    {
        Core = new TaskCore(null, Process);
    }

    private static async Task Process(object? obj)
    {
        var core = (TaskCore)obj!;
        while (await core.Delay(IntervalInMilliseconds))
        {
            var array = Dictionary.Keys.ToArray();
            Dictionary.Clear();

            if (array.Length != 0)
            {
                var currentTime = Stopwatch.GetTimestamp();
                foreach (var x in array)
                {
                    x.ExitIfExpired(currentTime);
                }
            }
        }
    }

    public static void Add(SemaphoreDual semaphore)
        => Dictionary.TryAdd(semaphore, 0);

    private static readonly TaskCore Core;
    private static readonly ConcurrentDictionary<SemaphoreDual, int> Dictionary = new();
}

/// <summary>
/// <see cref="SemaphoreDual"/> adds an expiration date to the lock feature.<br/>
/// It requires two steps of Enter() and TryLock() to actually acquire a lock.
/// </summary>
public class SemaphoreDual
{
    private object SyncObject => this; // lock (this) is a bad practice but...

    private long enteredTime = 0;
    private long lockLimit;
    private int waitCount;
    private int countOfWaitersPulsedToWake;
    private TaskNode? head;
    private TaskNode? tail;

    public SemaphoreDual(int lockLimitInMilliseconds)
    {
        if (lockLimitInMilliseconds > 0)
        {
            this.lockLimit = Stopwatch.Frequency * lockLimitInMilliseconds / 1_000;
        }
    }

    public bool IsFree => Volatile.Read(ref this.enteredTime) == 0;

    public bool IsLocked => Volatile.Read(ref this.enteredTime) != 0;

    public long Enter()
    {
        var lockTaken = false;
        long time = 0;
        Task<long>? task = null;

        try
        {
            if (this.IsLocked)
            {
                var spinCount = SemaphoreLock.DefaultSpinCountBeforeWait; // SpinWait.SpinCountforSpinBeforeWait * 4
                SpinWait spinner = default;
                while (spinner.Count < spinCount)
                {
                    spinner.SpinOnce(sleep1Threshold: -1);
                    if (this.IsFree)
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
                while (this.IsLocked)
                {
                    Monitor.Wait(this.SyncObject);
                    if (this.countOfWaitersPulsedToWake != 0)
                    {
                        this.countOfWaitersPulsedToWake--;
                    }
                }

                this.enteredTime = Stopwatch.GetTimestamp();
                time = this.enteredTime;
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

        return task == null ? time : task.GetAwaiter().GetResult();
    }

    public Task<long> EnterAsync()
    {
        lock (this.SyncObject)
        {
            if (this.IsFree)
            {
                this.enteredTime = Stopwatch.GetTimestamp();
                return Task.FromResult(this.enteredTime);
            }
            else
            {
                SemaphoreDualTask.Add(this);

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

    public Task<long> EnterAsync(int millisecondsTimeout)
        => this.EnterAsync(millisecondsTimeout, default);

    public Task<long> EnterAsync(int millisecondsTimeout, CancellationToken cancellationToken)
    {
        lock (this.SyncObject)
        {
            if (this.IsFree)
            {
                this.enteredTime = Stopwatch.GetTimestamp();
                return Task.FromResult(this.enteredTime);
            }
            else
            {
                if (millisecondsTimeout == 0)
                {// No waiting
                    return Task.FromResult(0L);
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

    public bool Exit(long time)
    {
        lock (this.SyncObject)
        {
            if (this.IsFree)
            {
                throw new SynchronizationLockException();
            }
            else if (this.enteredTime != time)
            {
                return false;
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
                waiterTask.TrySetResult(result: Stopwatch.GetTimestamp());
            }
            else
            {
                this.enteredTime = 0;
            }

            return true;
        }
    }

    internal void ExitIfExpired(long currentTime)
    {
        if (this.enteredTime == 0)
        {
            return;
        }

        lock (this.SyncObject)
        {

        }
    }

    private async Task<long> WaitUntilCountOrTimeoutAsync(TaskNode taskNode, int millisecondsTimeout, CancellationToken cancellationToken)
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
                return taskNode.Task.Result;
            }
        }

        lock (this.SyncObject)
        {
            if (this.RemoveAsyncWaiter(taskNode))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return 0L;
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

    private sealed class TaskNode : TaskCompletionSource<long>
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
