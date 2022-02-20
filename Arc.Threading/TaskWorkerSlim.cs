// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1401 // Fields should be private
#pragma warning disable SA1307 // Accessible fields should begin with upper-case letter

namespace Arc.Threading;

/// <summary>
/// Represents a work to be processed by <see cref="TaskWorkerSlim{T}"/>.
/// </summary>
public class TaskWorkSlim
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
            if (this.completeEvent is { } pulseEvent)
            {
                if (timeToWait < TimeSpan.Zero)
                {
                    await pulseEvent.WaitAsync(this.taskWorkerBase.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await pulseEvent.WaitAsync(timeToWait, this.taskWorkerBase.CancellationToken).ConfigureAwait(false);
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

    internal TaskWorkerSlimBase? taskWorkerBase;
    internal int state;
    internal AsyncPulseEvent? completeEvent = new();

    public TaskWorkState State => TaskWork.IntToState(this.state);
}

/// <summary>
/// Represents a worker class.
/// </summary>
/// <typeparam name="T">The type of a work.</typeparam>
public class TaskWorkerSlim<T> : TaskWorkerSlimBase
    where T : TaskWorkSlim
{
    /// <summary>
    /// Defines the type of delegate to process a work.
    /// </summary>
    /// <param name="worker">Worker instance.</param>
    /// <param name="work">Work instance.</param>
    /// <returns><see cref="AbortOrComplete.Complete"/>: Complete.<br/>
    /// <see cref="AbortOrComplete.Abort"/>: Abort or Error.</returns>
    public delegate Task<AbortOrComplete> WorkDelegate(TaskWorkerSlim<T> worker, T work);

    private static async Task Process(object? parameter)
    {
        var worker = (TaskWorkerSlim<T>)parameter!;
        var stateStandby = TaskWork.StateToInt(TaskWorkState.Standby);
        var stateWorking = TaskWork.StateToInt(TaskWorkState.Working);

        while (!worker.IsTerminated)
        {
            var pulseEvent = worker.addedEvent;
            if (pulseEvent == null)
            {
                break;
            }

            try
            {
                await pulseEvent.WaitAsync(worker.CancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            while (worker.workQueue.TryDequeue(out var work))
            {// Standby or Aborted
                if (Interlocked.CompareExchange(ref work.state, stateWorking, stateStandby) == stateStandby)
                {// Standby -> Working
                    worker.workInProgress = work;
                    if (await worker.method(worker, work).ConfigureAwait(false) == AbortOrComplete.Complete)
                    {// Copmplete
                        work.state = TaskWork.StateToInt(TaskWorkState.Complete);
                    }
                    else
                    {// Aborted
                        work.state = TaskWork.StateToInt(TaskWorkState.Aborted);
                    }

                    worker.workInProgress = null;
                    work.completeEvent?.Pulse();
                    work.completeEvent = null;
                }
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskWorkerSlim{T}"/> class.
    /// </summary>
    /// <param name="parent">The parent.</param>
    /// <param name="method">The method that receives and processes a work.</param>
    /// <param name="startImmediately">Starts the worker immediately.<br/>
    /// <see langword="false"/>: Manually call <see cref="ThreadCore.Start" /> to start the worker.</param>
    public TaskWorkerSlim(ThreadCoreBase parent, WorkDelegate method, bool startImmediately = true)
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
        this.addedEvent?.Pulse();
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
            {// No work
                work = this.workInProgress;
                if (work == null)
                {
                    return true;
                }
            }

            try
            {
                var pulseEvent = work.completeEvent;
                if (pulseEvent != null)
                {
                    if (timeToWait < TimeSpan.Zero)
                    {
                        await pulseEvent.WaitAsync(this.CancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await pulseEvent.WaitAsync(timeToWait, this.CancellationToken).ConfigureAwait(false);
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
    private volatile T? workInProgress;
}

/// <summary>
/// Represents a base worker class.
/// </summary>
public class TaskWorkerSlimBase : TaskCore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TaskWorkerSlimBase"/> class.
    /// </summary>
    /// <param name="parent">The parent.</param>
    /// <param name="processWork">The method invoked to process a work.</param>
    internal TaskWorkerSlimBase(ThreadCoreBase parent, Func<object?, Task> processWork)
    : base(parent, processWork, false)
    {
    }

    internal AsyncPulseEvent? addedEvent = new();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                this.addedEvent = null;
            }

            base.Dispose(disposing);
        }
    }
}
