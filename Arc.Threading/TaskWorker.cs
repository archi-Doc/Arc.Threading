// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1401 // Fields should be private
#pragma warning disable SA1307 // Accessible fields should begin with upper-case letter

namespace Arc.Threading;

/// <summary>
/// Represents a interface for processing <typeparamref name="TWork"/>.
/// </summary>
/// <typeparam name="TWork">The type of the work.</typeparam>
public sealed class TaskWorkInterface<TWork>
    where TWork : notnull
{
    public TaskWorkInterface(TaskWorker<TWork> taskWorker, TWork work)
    {
        this.TaskWorker = taskWorker;
        this.Work = work;
        this.state = TaskWorkHelper.StateToInt(TaskWorkState.Standby);
    }

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
        var state = this.State;
        if (state != TaskWorkState.Standby && state != TaskWorkState.Working)
        {
            return state == TaskWorkState.Complete;
        }
        else if (this.TaskWorker.IsTerminated)
        {// Terminated
            return false;
        }

        var stateComplete = TaskWorkHelper.StateToInt(TaskWorkState.Complete);
        var stateAborted = TaskWorkHelper.StateToInt(TaskWorkState.Aborted);
        try
        {
            if (this.completeEvent is { } pulseEvent)
            {
                if (timeToWait < TimeSpan.Zero)
                {
                    await pulseEvent.WaitAsync(this.TaskWorker.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await pulseEvent.WaitAsync(timeToWait, this.TaskWorker.CancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (TimeoutException)
        {// Timeout
            if (abortIfTimeout)
            {// Abort (Standby->Abort, Working->Abort)
                if (this.state != stateComplete)
                {
                    this.state = stateAborted;
                }
            }
        }
        catch
        {// Cancellation
            if (this.state != stateComplete)
            {
                this.state = stateAborted;
            }
        }

        if (this.state == stateComplete)
        {// Complete
            return true;
        }
        else
        {// Standby or Working or Aborted
            return false;
        }
    }

    /// <summary>
    /// Gets an instance of <see cref="TaskWorker{TWork}"/>.
    /// </summary>
    public TaskWorker<TWork> TaskWorker { get; }

    /// <summary>
    /// Gets an instance of <typeparamref name="TWork"/>.
    /// </summary>
    public TWork Work { get; }

    /// <summary>
    /// Gets a state of the work.
    /// </summary>
    public TaskWorkState State => TaskWorkHelper.IntToState(this.state);

    public override string ToString() => $"State: {this.State}, Work: {this.Work}";

    internal int state;
    internal AsyncSinglePulseEvent? completeEvent = new();
}

/// <summary>
/// Represents a worker class.<br/>
/// <see cref="TaskWorker{TWork}"/> uses <see cref="HashSet{TWork}"/> and <see cref="LinkedList{TWork}"/> to manage works.
/// </summary>
/// <typeparam name="TWork">The type of the work.</typeparam>
public class TaskWorker<TWork> : TaskCore
    where TWork : notnull
{
    /// <summary>
    /// Defines the type of delegate to process a work.
    /// </summary>
    /// <param name="worker">Worker instance.</param>
    /// <param name="work">Work instance.</param>
    /// <returns><see cref="AbortOrComplete.Complete"/>: Complete.<br/>
    /// <see cref="AbortOrComplete.Abort"/>: Abort or Error.</returns>
    public delegate Task<AbortOrComplete> WorkDelegate(TaskWorker<TWork> worker, TWork work);

    private static async Task Process(object? parameter)
    {
        var worker = (TaskWorker<TWork>)parameter!;
        var stateStandby = TaskWorkHelper.StateToInt(TaskWorkState.Standby);
        var stateWorking = TaskWorkHelper.StateToInt(TaskWorkState.Working);

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

            while (true)
            {
                TaskWorkInterface<TWork>? workInterface;
                lock (worker.workToInterface)
                {
                    if (worker.standbyList.First == null)
                    {// No work left.
                        break;
                    }

                    workInterface = worker.standbyList.First.Value;
                    worker.standbyList.RemoveFirst(); // Remove from linked list.
                    worker.workInProgress = workInterface;
                }

                // Standby or Aborted
                if (Interlocked.CompareExchange(ref workInterface.state, stateWorking, stateStandby) == stateStandby)
                {// Standby -> Working
                    if (!worker.IsTerminated &&
                        await worker.method(worker, workInterface.Work).ConfigureAwait(false) == AbortOrComplete.Complete)
                    {// Copmplete
                        workInterface.state = TaskWorkHelper.StateToInt(TaskWorkState.Complete);
                    }
                    else
                    {// Aborted
                        workInterface.state = TaskWorkHelper.StateToInt(TaskWorkState.Aborted);
                    }
                }

                AsyncSinglePulseEvent? completeEvent = null;
                lock (worker.workToInterface)
                {
                    worker.workToInterface.Remove(workInterface.Work); // Remove from dictionary (delayed to determine if it was the same work).
                    worker.workInProgress = null;
                    completeEvent = workInterface.completeEvent;
                    workInterface.completeEvent = null;
                }

                completeEvent?.Pulse();
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskWorker{T}"/> class.<br/>
    /// </summary>
    /// <param name="parent">The parent.</param>
    /// <param name="method">The method that receives and processes a work.</param>
    /// <param name="startImmediately">Starts the worker immediately.<br/>
    /// <see langword="false"/>: Manually call <see cref="ThreadCore.Start" /> to start the worker.</param>
    public TaskWorker(ThreadCoreBase parent, WorkDelegate method, bool startImmediately = true)
        : base(parent, Process, startImmediately)
    {
        this.method = method;
        if (startImmediately)
        {
            this.Start();
        }
    }

    /// <summary>
    /// Add a work at the start of the work queue.
    /// </summary>
    /// <param name="work">A work to be added.</param>
    /// <returns><see langword="true"/>: Success, <see langword="false"/>: The work already exists.</returns>
    public TaskWorkInterface<TWork> AddFirst(TWork work)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(null);
        }

        TaskWorkInterface<TWork>? workInterface;
        lock (this.workToInterface)
        {
            if (this.workToInterface.TryGetValue(work, out workInterface))
            {
                return workInterface;
            }

            workInterface = new(this, work);
            this.standbyList.AddFirst(workInterface);
            this.workToInterface.Add(work, workInterface);
        }

        this.addedEvent?.Pulse();
        return workInterface;
    }

    /// <summary>
    /// Add a work at the end of the work queue.
    /// </summary>
    /// <param name="work">A work to be added..</param>
    /// <returns><see langword="true"/>: Success, <see langword="false"/>: The work already exists.</returns>
    public TaskWorkInterface<TWork> AddLast(TWork work)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(null);
        }

        TaskWorkInterface<TWork>? workInterface;
        lock (this.workToInterface)
        {
            if (this.workToInterface.TryGetValue(work, out workInterface))
            {
                return workInterface;
            }

            workInterface = new(this, work);
            this.standbyList.AddLast(workInterface);
            this.workToInterface.Add(work, workInterface);
        }

        this.addedEvent?.Pulse();
        return workInterface;
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
            TaskWorkInterface<TWork>? workInterface;
            lock (this.workToInterface)
            {
                if (this.standbyList.Last != null)
                {
                    workInterface = this.standbyList.Last.Value;
                }
                else
                {
                    workInterface = this.workInProgress;
                    if (workInterface == null)
                    {
                        return true;
                    }
                }
            }

            try
            {
                var pulseEvent = workInterface.completeEvent;
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
    public int Count => this.standbyList.Count;

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

    private WorkDelegate method;
    private Dictionary<TWork, TaskWorkInterface<TWork>> workToInterface = new(); // syncObject
    private LinkedList<TaskWorkInterface<TWork>> standbyList = new();
    private LinkedList<TaskWorkInterface<TWork>> workingList = new();
    private TaskWorkInterface<TWork>? workInProgress;
}
