// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Provides a non-reentrant exclusive lock for synchronous and asynchronous callers.
/// </summary>
/// <remarks>
/// Keep instances private: internal synchronization locks the instance itself.<br/>
/// Ownership is not tracked by thread or task, so the lock can be held across an await and released on another thread.
/// Entering again while holding the lock deadlocks.
/// </remarks>
public class SemaphoreLock : ILockable, IAsyncLockable
{// object:16, 1+2+4+8+8+8 -> 47
    private const int DefaultSpinCountBeforeWait = 35 * 4;

    private object SyncObject => this; // lock (this) is a bad practice but...

    private bool entered = false;
    private ushort countOfWaitersPulsedToWake; // int -> ushort
    private int waitCount;
    private TaskNode? head;
    private TaskNode? tail;
    private Task<LockScope>? acquiredScopeTask; // Shared result of uncontended EnterScopeAsync() calls.

    /// <summary>
    /// Initializes a new instance of the <see cref="SemaphoreLock"/> class.
    /// </summary>
    public SemaphoreLock()
    {
    }

    /// <summary>
    /// Acquires an exclusive lock and creates a <see cref="LockScope"/> for a using statement.
    /// </summary>
    /// <returns>A scope that releases the lock on disposal.</returns>
    /// <exception cref="ThreadInterruptedException">The thread is interrupted while waiting.</exception>
    public LockScope EnterScope()
        => new LockScope(this);

    /// <summary>
    /// Asynchronously acquires an exclusive lock and creates a <see cref="LockScope"/> for a using statement.
    /// </summary>
    /// <returns>A task that returns a scope that releases the lock on disposal. Uncontended calls return a cached completed task.</returns>
    public Task<LockScope> EnterScopeAsync()
    {
        var task = this.EnterAsync();
        if (task.IsCompletedSuccessfully && task.Result)
        {// Every uncontended acquisition yields the same scope value, so one completed task can be shared.
            return Volatile.Read(ref this.acquiredScopeTask) ?? this.CreateAcquiredScopeTask();
        }

        return this.EnterScopeAsyncCore(task);
    }

    /// <summary>
    /// Gets a value indicating whether the lock is currently held by any caller.
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
    /// <returns>Always <see langword="true"/>; the method returns only after the lock is acquired.</returns>
    /// <exception cref="ThreadInterruptedException">The thread is interrupted while waiting. The lock is not acquired.</exception>
    public bool Enter()
    {
        var lockTaken = false;
        TaskNode? asyncWaiter = null;

        try
        {
            if (Volatile.Read(ref this.entered) && Volatile.Read(ref this.head) is null)
            {// Spinning cannot succeed while async waiters are queued, because Exit() hands the lock directly to them.
                var spinCount = DefaultSpinCountBeforeWait; // SpinWait.SpinCountforSpinBeforeWait * 4
                SpinWait spinner = default;
                while (spinner.Count < spinCount)
                {
                    spinner.SpinOnce(sleep1Threshold: -1);
                    if (!Volatile.Read(ref this.entered) || Volatile.Read(ref this.head) is not null)
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
            var cleanupLockTaken = false;
            var interruptedAgain = false; // Merged into the original interruption, which is rethrown.
            try
            {
                EnterUninterruptibly(this.SyncObject, ref cleanupLockTaken, ref interruptedAgain);
                if (!this.RemoveAsyncWaiter(asyncWaiter))
                {
                    // A concurrent release may already have transferred ownership to this waiter.
                    this.ReleaseNextWaiter();
                }
            }
            finally
            {
                if (cleanupLockTaken)
                {
                    Monitor.Exit(this.SyncObject);
                }
            }

            throw; // Rethrows the original interruption.
        }
    }

    /// <summary>
    /// Asynchronously waits to enter the <see cref="SemaphoreLock"/>.
    /// </summary>
    /// <returns>A task that returns <see langword="true"/> once the lock is acquired. It never returns <see langword="false"/>.</returns>
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
    /// <remarks>
    /// Cancellation completes the task with <see langword="false"/> instead of throwing.
    /// If <paramref name="cancellationToken"/> is already canceled, the method returns <see langword="false"/> without trying to acquire the lock.<br/>
    /// Continuations never run inside <see cref="CancellationTokenSource.Cancel()"/> or <see cref="Exit"/>.
    /// </remarks>
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
    /// <remarks>
    /// Timeout and cancellation complete the task with <see langword="false"/> instead of throwing.
    /// If <paramref name="cancellationToken"/> is already canceled, the method returns <see langword="false"/> without trying to acquire the lock.<br/>
    /// Continuations never run inside <see cref="CancellationTokenSource.Cancel()"/> or <see cref="Exit"/>.
    /// </remarks>
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

                if (timeout == Timeout.InfiniteTimeSpan)
                {
                    if (!cancellationToken.CanBeCanceled)
                    {
                        return this.AddAsyncWaiter().Task;
                    }

                    // Register on the token directly: no timer is needed, so Task.WaitAsync and an async state machine would only add allocations.
                    var cancelable = new CancelableTaskNode(this);
                    this.AppendAsyncWaiter(cancelable);

                    // An already canceled token runs the callback inline under the reentrant monitor and completes the task with false.
                    cancelable.Registration = cancellationToken.UnsafeRegister(static state => ((CancelableTaskNode)state!).Cancel(), cancelable);
                    return cancelable.Task;
                }

                return this.WaitUntilCountOrTimeoutAsync(this.AddAsyncWaiter(), timeout, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Releases the exclusive lock. The caller must hold the lock.
    /// </summary>
    /// <remarks>Ownership is not checked: the lock is released regardless of which thread or task acquired it.</remarks>
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

    /// <summary>
    /// Acquires the monitor even if the thread is interrupted while waiting, so cleanup cannot be skipped.
    /// </summary>
    /// <param name="syncObject">The monitor object.</param>
    /// <param name="lockTaken">Set to <see langword="true"/> when the monitor is acquired.</param>
    /// <param name="interrupted">Set to <see langword="true"/> if an interruption was suppressed.</param>
    private static void EnterUninterruptibly(object syncObject, ref bool lockTaken, ref bool interrupted)
    {
        while (!lockTaken)
        {
            try
            {
                Monitor.Enter(syncObject, ref lockTaken);
            }
            catch (ThreadInterruptedException)
            {// Monitor.Enter is interruptible while contended; retry.
                interrupted = true;
            }
        }
    }

    private Task<LockScope> CreateAcquiredScopeTask()
    {
        var task = Task.FromResult(new LockScope(this, true));
        Volatile.Write(ref this.acquiredScopeTask, task);
        return task;
    }

    private async Task<LockScope> EnterScopeAsyncCore(Task<bool> task)
        => new(this, await task.ConfigureAwait(false));

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
            if (waiterTask is CancelableTaskNode cancelable)
            {// Unregister instead of Dispose: Dispose waits for a running callback, which may be blocked on this monitor.
                cancelable.Registration.Unregister();
            }

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
        this.AppendAsyncWaiter(node);
        return node;
    }

    private void AppendAsyncWaiter(TaskNode node)
    {
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
    }

    private async Task<bool> WaitUntilCountOrTimeoutAsync(TaskNode asyncWaiter, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await ((Task)asyncWaiter.Task.WaitAsync(timeout, cancellationToken)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (cancellationToken.IsCancellationRequested)
        {// This may be running inside CancellationTokenSource.Cancel(): leave that thread before taking the monitor or running the caller's continuation.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

        if (asyncWaiter.Task.IsCompleted)
        {
            return true;
        }

        lock (this.SyncObject)
        {// A waiter that is no longer queued has already been granted the lock by ReleaseNextWaiter().
            return !this.RemoveAsyncWaiter(asyncWaiter);
        }
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

    private class TaskNode : TaskCompletionSource<bool>
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

    private sealed class CancelableTaskNode : TaskNode
    {
#pragma warning disable SA1401 // Fields should be private
        internal CancellationTokenRegistration Registration;
#pragma warning restore SA1401 // Fields should be private

        private readonly SemaphoreLock owner;

        internal CancelableTaskNode(SemaphoreLock owner)
        {
            this.owner = owner;
        }

        internal void Cancel()
        {// Runs on the canceling thread: an interrupt there must neither drop the cancellation nor escape from CancellationTokenSource.Cancel().
            var lockTaken = false;
            var interrupted = false;
            bool removed;
            try
            {
                EnterUninterruptibly(this.owner.SyncObject, ref lockTaken, ref interrupted);
                removed = this.owner.RemoveAsyncWaiter(this); // False if already granted.
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(this.owner.SyncObject);
                }

                if (interrupted)
                {// Deliver the interrupt at the next blocking call of this thread.
                    Thread.CurrentThread.Interrupt();
                }
            }

            if (removed)
            {
                this.TrySetResult(false);
            }
        }
    }
}
