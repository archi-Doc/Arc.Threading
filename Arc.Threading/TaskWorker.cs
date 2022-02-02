// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1401 // Fields should be private
#pragma warning disable SA1307 // Accessible fields should begin with upper-case letter

namespace Arc.Threading;

/// <summary>
/// Represents a state of a task work.<br/>
/// Created -> Standby -> Abort / Working -> Completed.
/// </summary>
public enum TaskWorkState : int
{
    /// <summary>
    /// Work is created.
    /// </summary>
    Created,

    /// <summary>
    /// Work is on standby.
    /// </summary>
    Standby,

    /// <summary>
    /// Work in progress.
    /// </summary>
    Working,

    /// <summary>
    /// Work is complete (worker -> user).
    /// </summary>
    Complete,

    /// <summary>
    /// Work is aborted (user -> worker).
    /// </summary>
    Aborted,
}

/// <summary>
/// Represents a work to be processed by <see cref="TaskWorker{T}"/>.
/// </summary>
public class TaskWork
{
    /// <summary>
    /// Wait until the work is completed.
    /// </summary>
    /// <returns><see langword="true"/>: The work is complete<br/><see langword="false"/>: Not complete.</returns>
    public Task<bool> WaitForCompletionAsync() => this.WaitForCompletionAsync(TimeSpan.MinValue, false);

    /// <summary>
    /// Wait for the specified time until the work is completed.
    /// </summary>
    /// <param name="millisecondsToWait">The number of milliseconds to wait, or -1 to wait indefinitely.</param>
    /// <param name="abortIfTimeout">Abort the work if the specified time is elapsed.</param>
    /// <returns><see langword="true"/>: The work is complete<br/><see langword="false"/>: Not complete.</returns>
    public Task<bool> WaitForCompletionAsync(int millisecondsToWait, bool abortIfTimeout = true) => this.WaitForCompletionAsync(TimeSpan.FromMilliseconds(millisecondsToWait), abortIfTimeout);

    /// <summary>
    /// Wait for the specified time until the work is completed.
    /// </summary>
    /// <param name="timeToWait">The TimeSpan to wait, or negative value (e.g TimeSpan.MinValue) to wait indefinitely.</param>
    /// <param name="abortIfTimeout">Abort the work if the specified time is elapsed.</param>
    /// <returns><see langword="true"/>: The work is complete<br/><see langword="false"/>: Not complete.</returns>
    public async Task<bool> WaitForCompletionAsync(TimeSpan timeToWait, bool abortIfTimeout = true)
    {
        if (this.taskWorkerBase == null)
        {
            throw new InvalidOperationException("TaskWorker is not assigned.");
        }

        var state = this.State;
        if (state != TaskWorkState.Standby && state != TaskWorkState.Working)
        {
            return state == TaskWorkState.Complete;
        }
        else if (this.taskWorkerBase.IsTerminated)
        {// Terminated
            return false;
        }

        int intState; // State is Standby or Working or Complete or Aborted.
        try
        {
            if (this.completeEvent is { } ev)
            {
                if (timeToWait < TimeSpan.Zero)
                {
                    await this.completeEvent.AsTask.WaitAsync(this.taskWorkerBase.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await this.completeEvent.AsTask.WaitAsync(timeToWait, this.taskWorkerBase.CancellationToken).ConfigureAwait(false);
                }
            }

            intState = this.state;
        }
        catch
        {// Timeout or cancelled
            if (abortIfTimeout)
            {// Abort
                intState = Interlocked.CompareExchange(ref this.state, TaskWork.StateToInt(TaskWorkState.Aborted), TaskWork.StateToInt(TaskWorkState.Standby));
            }
            else
            {
                intState = this.state;
            }
        }

        if (intState == TaskWork.StateToInt(TaskWorkState.Complete))
        {// Complete
            return true;
        }
        else
        {// Standby or Working or Aborted
            return false;
        }
    }

    internal TaskWorkerBase? taskWorkerBase;
    internal int state;
    internal AsyncManualResetEvent? completeEvent = new();

    public TaskWorkState State => IntToState(this.state);

    internal static TaskWorkState IntToState(int state) => Unsafe.As<int, TaskWorkState>(ref state);

    internal static int StateToInt(TaskWorkState state) => Unsafe.As<TaskWorkState, int>(ref state);
}

/// <summary>
/// Represents a worker class.
/// </summary>
/// <typeparam name="T">The type of a work.</typeparam>
public class TaskWorker<T> : TaskWorkerBase
    where T : TaskWork
{
    /// <summary>
    /// Defines the type of delegate to process a work.
    /// </summary>
    /// <param name="worker">Worker instance.</param>
    /// <param name="work">Work instance.</param>
    /// <returns><see cref="AbortOrComplete.Complete"/>: Complete.<br/>
    /// <see cref="AbortOrComplete.Abort"/>: Abort or Error.</returns>
    public delegate Task<AbortOrComplete> WorkDelegate(TaskWorker<T> worker, T work);

    private static async Task Process(object? parameter)
    {
        var worker = (TaskWorker<T>)parameter!;
        var stateStandby = TaskWork.StateToInt(TaskWorkState.Standby);
        var stateWorking = TaskWork.StateToInt(TaskWorkState.Working);

        while (!worker.IsTerminated)
        {
            try
            {
                if (worker.addedSemaphore is { } ss)
                {
                    await ss.WaitAsync(worker.CancellationToken).ConfigureAwait(false);
                    // await ev.AsTask.WaitAsync(worker.CancellationToken).ConfigureAwait(false);
                    // ev.Reset();
                    Console.WriteLine("addedEvent - Reset"); // tempcode
                    // await Task.Yield();
                }
                else
                {
                    await Task.Delay(ThreadCore.DefaultInterval, worker.CancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                return;
            }

            while (worker.workQueue.TryDequeue(out var work))
            {// Standby or Aborted
                if (Interlocked.CompareExchange(ref work.state, stateWorking, stateStandby) == stateStandby)
                {// Standby -> Working
                    if (await worker.method(worker, work).ConfigureAwait(false) == AbortOrComplete.Complete)
                    {// Copmplete
                        work.state = TaskWork.StateToInt(TaskWorkState.Complete);
                    }
                    else
                    {// Aborted
                        work.state = TaskWork.StateToInt(TaskWorkState.Aborted);
                    }

                    if (work.completeEvent is { } ev)
                    {
                        ev.Set();
                        work.completeEvent = null;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskWorker{T}"/> class.
    /// </summary>
    /// <param name="parent">The parent.</param>
    /// <param name="method">The method that receives and processes a work.</param>
    /// <param name="startImmediately">Starts the worker immediately.<br/>
    /// <see langword="false"/>: Manually call <see cref="ThreadCore.Start" /> to start the worker.</param>
    public TaskWorker(ThreadCoreBase parent, WorkDelegate method, bool startImmediately = true)
        : base(parent, Process)
    {
        this.method = method;
        if (startImmediately)
        {
            this.Start();
        }
    }

    /// <summary>
    /// Add a work to the worker.
    /// </summary>
    /// <param name="work">A work.<br/>work.State will be set to <see cref="TaskWorkState.Standby"/>.</param>
    public void Add(T work)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(null);
        }

        if (work.State != TaskWorkState.Created)
        {
            throw new InvalidOperationException("Only newly created work can be added to a worker.");
        }

        work.taskWorkerBase = this;
        work.state = TaskWork.StateToInt(TaskWorkState.Standby);
        this.workQueue.Enqueue(work);
        Console.WriteLine("addedEvent - Set before"); // tempcode
        this.addedSemaphore?.Release();
        Console.WriteLine("addedEvent - Set after"); // tempcode
    }

    /// <summary>
    /// Waits for the completion of all works.
    /// </summary>
    /// <returns><see langword="true"/>: All works are complete.<br/><see langword="false"/>: Timeout or cancelled.</returns>
    public Task<bool> WaitForCompletionAsync() => this.WaitForCompletionAsync(TimeSpan.MinValue);

    /// <summary>
    /// Waits for the completion of all works.
    /// </summary>
    /// <param name="millisecondsToWait">The number of milliseconds to wait, or -1 to wait indefinitely.</param>
    /// <returns><see langword="true"/>: All works are complete.<br/><see langword="false"/>: Timeout or cancelled.</returns>
    public Task<bool> WaitForCompletionAsync(int millisecondsToWait) => this.WaitForCompletionAsync(TimeSpan.FromMilliseconds(millisecondsToWait));

    /// <summary>
    /// Waits for the completion of all works.
    /// </summary>
    /// /// <param name="timeToWait">The TimeSpan to wait, or negative value (e.g TimeSpan.MinValue) to wait indefinitely.</param>
    /// <returns><see langword="true"/>: All works are complete.<br/><see langword="false"/>: Timeout or cancelled.</returns>
    public async Task<bool> WaitForCompletionAsync(TimeSpan timeToWait)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(null);
        }

        while (!this.IsTerminated)
        {
            if (!this.workQueue.TryPeek(out var work))
            {// Complete
                return true;
            }

            try
            {
                var ev = work.completeEvent;
                if (ev != null)
                {
                    if (timeToWait < TimeSpan.Zero)
                    {
                        await ev.AsTask.WaitAsync(this.CancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await ev.AsTask.WaitAsync(timeToWait, this.CancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await Task.Delay(ThreadCore.DefaultInterval).ConfigureAwait(false);
                }
            }
            catch
            {// Timeout or cancelled
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the number of works in the queue.
    /// </summary>
    public int Count => this.workQueue.Count;

    private WorkDelegate method;
    private ConcurrentQueue<T> workQueue = new();
}

/// <summary>
/// Represents a base worker class.
/// </summary>
public class TaskWorkerBase : TaskCore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TaskWorkerBase"/> class.
    /// </summary>
    /// <param name="parent">The parent.</param>
    /// <param name="processWork">The method invoked to process a work.</param>
    /*internal TaskWorkerBase(ThreadCoreBase parent, Func<object?, Task> processWork)
        : base(parent, processWork, false)*/
    internal TaskWorkerBase(ThreadCoreBase parent, Func<object?, Task> processWork)
    : base(parent, processWork, false)
    {
    }

    internal SemaphoreSlim? addedSemaphore = new(0, 1);
    // internal AsyncManualResetEvent? addedEvent = new();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                if (this.addedSemaphore != null)
                {
                    this.addedSemaphore.Dispose();
                    this.addedSemaphore = null;
                }

                // this.addedEvent = null;
            }

            base.Dispose(disposing);
        }
    }
}
