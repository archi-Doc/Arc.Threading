// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1202

namespace Arc.Threading;

internal static class SemaphoreDualTask
{
    internal const int IntervalInMilliseconds = 200;

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
            if (array.Length != 0)
            {
                Dictionary.Clear();

                foreach (var x in array)
                {
                    x.ExitIfExpired();
                }
            }

            currentCount++;
        }
    }

    public static void Add(SemaphoreDual semaphore)
        => Dictionary.TryAdd(semaphore, 0);

    public static uint CurrentCount => currentCount;

    private static readonly TaskCore Core;
    private static readonly ConcurrentDictionary<SemaphoreDual, int> Dictionary = new();
    private static volatile uint currentCount;
}

/// <summary>
/// <see cref="SemaphoreDual"/> adds an expiration date to the lock feature.<br/>
/// It requires two steps of Enter1() and Enter2() to actually acquire a lock.
/// </summary>
public class SemaphoreDual
{
    private object SyncObject => this; // lock (this) is a bad practice but...

    private readonly uint countLimit;
    private uint nextId = 1;
    private uint reservedId = 0;
    private uint reservedCount;
    private TaskNode? head;
    private TaskNode? tail;

    public SemaphoreDual(int lockLimitInMilliseconds)
    {
        if (lockLimitInMilliseconds >= 0)
        {
            this.countLimit = (uint)(lockLimitInMilliseconds / SemaphoreDualTask.IntervalInMilliseconds);
            if (this.countLimit == 0)
            {
                this.countLimit = 1;
            }
        }
        else
        {
            this.countLimit = uint.MaxValue;
        }
    }

    public bool CanReserve => Volatile.Read(ref this.reservedId) == 0;

    public ValueTask<uint> ReserveAsync()
        => this.ReserveAsync(-1, default);

    public ValueTask<uint> ReserveAsync(int millisecondsTimeout)
        => this.ReserveAsync(millisecondsTimeout, default);

    public ValueTask<uint> ReserveAsync(int millisecondsTimeout, CancellationToken cancellationToken)
    {
        TaskNode node;

        lock (this.SyncObject)
        {
            if (this.CanReserve)
            {// Can reserve
                this.reservedId = this.nextId++;
                this.reservedCount = SemaphoreDualTask.CurrentCount;
                return ValueTask.FromResult(this.reservedId);
            }
            else
            {
                if (millisecondsTimeout == 0)
                {// No waiting
                    return ValueTask.FromResult(0u);
                }

                node = new TaskNode();
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
            }
        }

        // Reserve task added.
        SemaphoreDualTask.Add(this);
        return this.WaitUntilCountOrTimeoutAsync(node, millisecondsTimeout, cancellationToken);
    }

    public bool Release(uint reservationId)
    {
        lock (this.SyncObject)
        {
            if (this.CanReserve || this.reservedId != reservationId)
            {
                return false;
            }

            this.ExitInternal();
            return true;
        }
    }

    public bool Enter(uint id)
    {
        var ret = false;
        if (Volatile.Read(ref this.enteredTime) != time)
        {
            return ret;
        }

        bool taken = false;
        try
        {
            Monitor.Enter(this.SyncObject, ref taken);
            if (taken && this.enteredTime == time)
            {
                ret = true;
            }
        }
        finally
        {
            if (taken && !ret)
            {
                Monitor.Exit(this.SyncObject);
            }
        }

        return ret;
    }

    public bool Exit(long time)
    {
        if (Volatile.Read(ref this.enteredTime) != time)
        {
            return false;
        }

        Monitor.Exit(this.SyncObject);
        return true;
    }

    internal void ExitIfExpired()
    {
        if (this.CanReserve)
        {
            return;
        }

        lock (this.SyncObject)
        {
            if ((this.reservedCount + this.countLimit) < SemaphoreDualTask.CurrentCount)
            {// Exit
                this.ExitInternal();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExitInternal()
    {
        if (this.head is not null)
        {
            var waiterTask = this.head;
            this.RemoveAsyncWaiter(waiterTask);
            this.enteredTime = Stopwatch.GetTimestamp();
            waiterTask.TrySetResult(result: this.enteredTime);
        }
        else
        {
            this.enteredTime = 0;
        }
    }

    private async ValueTask<long> WaitUntilCountOrTimeoutAsync(TaskNode taskNode, int millisecondsTimeout, CancellationToken cancellationToken)
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
