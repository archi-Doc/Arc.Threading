// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        this.task = Task.Run(() =>
        {
            this.TaskWorker.method(this.TaskWorker, this.Work).Wait();
            this.TaskWorker.FinishWork(this);
        });
    }

    /// <summary>
    /// Wait until the work is completed.
    /// </summary>
    /// <returns><see langword="true"/>: The work is complete<br/><see langword="false"/>: Not complete.</returns>
    public Task<bool> WaitForCompletionAsync() => this.WaitForCompletionAsync(TimeSpan.MinValue);

    /// <summary>
    /// Wait for the specified time until the work is completed.
    /// </summary>
    /// <param name="millisecondsToWait">The number of milliseconds to wait, or -1 to wait indefinitely.</param>
    /// <returns><see langword="true"/>: The work is complete<br/><see langword="false"/>: Not complete.</returns>
    public Task<bool> WaitForCompletionAsync(int millisecondsToWait) => this.WaitForCompletionAsync(TimeSpan.FromMilliseconds(millisecondsToWait));

    /// <summary>
    /// Wait for the specified time until the work is completed.
    /// </summary>
    /// <param name="timeToWait">The TimeSpan to wait, or negative value (e.g TimeSpan.MinValue) to wait indefinitely.</param>
    /// <returns><see langword="true"/>: The work is complete<br/><see langword="false"/>: Not complete.</returns>
    public async Task<bool> WaitForCompletionAsync(TimeSpan timeToWait)
    {
        var state = this.State;
        if (state == TaskWorkState.Complete)
        {// Complete
            return true;
        }
        else if (state == TaskWorkState.Aborted)
        {// Aborted
            return false;
        }
        else if (this.TaskWorker.IsTerminated)
        {// Terminated
            return false;
        }

        // Standby or Working
        try
        {
            if (timeToWait < TimeSpan.Zero)
            {
                await this.task.WaitAsync(this.TaskWorker.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                await this.task.WaitAsync(timeToWait, this.TaskWorker.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {// Timeout
            return false;
        }
        catch
        {// Cancellation
            return false;
        }

        if (this.task.Status == TaskStatus.RanToCompletion)
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
    /// Gets a state of the work (Standby -> Working -> Complete or Aborted).
    /// </summary>
    public TaskWorkState State
    {
        get
        {
            if (this.node == null)
            {// Complete or Aborted
                if (this.task.Status == TaskStatus.RanToCompletion)
                {
                    return TaskWorkState.Complete;
                }
                else if (this.task.Status == TaskStatus.Canceled ||
                    this.task.Status == TaskStatus.Faulted)
                {
                    return TaskWorkState.Aborted;
                }
            }
            else
            {// Standby or Working
                if (this.task.Status == TaskStatus.Running)
                {
                    return TaskWorkState.Working;
                }
            }

            return TaskWorkState.Standby;
        }
    }

    public override string ToString() => $"State: {this.State}, Work: {this.Work}";

    internal Task task;
    internal LinkedListNode<TaskWorkInterface<TWork>>? node; // null: , not null: standby list or working list
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
    /// <param name="worker">A worker instance.</param>
    /// <param name="work">A work instance.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public delegate Task WorkDelegate(TaskWorker<TWork> worker, TWork work);

    private static async Task Process(object? parameter)
    {
        var worker = (TaskWorker<TWork>)parameter!;
        var stateStandby = TaskWorkHelper.StateToInt(TaskWorkState.Standby);
        var stateWorking = TaskWorkHelper.StateToInt(TaskWorkState.Working);

        while (!worker.IsTerminated)
        {
            var updateEvent = worker.updateEvent;
            if (updateEvent == null)
            {
                break;
            }

            try
            {
                await updateEvent.WaitAsync(worker.CancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            lock (worker.workToInterface)
            {
                while (true)
                {
                    var work = worker.standbyList.FirstOrDefault();
                    if (work == null)
                    {// No work left.
                        break;
                    }
                    else if (worker.workingList.Count >= 1)
                    {// Working list is full.
                        break;
                    }

                    worker.standbyList.Remove(work.node!);
                    worker.workingList.AddLast(work);
                    work.task.Start();
                }
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
            this.workToInterface.Add(work, workInterface);
            workInterface.node = this.standbyList.AddFirst(workInterface);
        }

        this.updateEvent?.Pulse();
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
            this.workToInterface.Add(work, workInterface);
            workInterface.node = this.standbyList.AddLast(workInterface);
        }

        this.updateEvent?.Pulse();
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
            Task? task;
            lock (this.workToInterface)
            {// Get standby or working task.
                task = this.standbyList.LastOrDefault()?.task ?? this.workingList.LastOrDefault()?.task;
                if (task == null)
                {// No task (complete)
                    return true;
                }
            }

            var sw = Stopwatch.StartNew();
            try
            {
                if (timeToWait < TimeSpan.Zero)
                {
                    await task.WaitAsync(this.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await task.WaitAsync(timeToWait, this.CancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {// Timeout or cancelled
                return false;
            }

            timeToWait -= sw.Elapsed;
            if (timeToWait <= TimeSpan.Zero)
            {// Timeout
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the number of works in the standby queue.
    /// </summary>
    public int StandbyCount => this.standbyList.Count;

    /// <summary>
    /// Gets the number of works in the working queue.
    /// </summary>
    public int WorkingCount => this.workingList.Count;

    internal AsyncPulseEvent? updateEvent = new();

    internal void FinishWork(TaskWorkInterface<TWork> workInterface)
    {
        lock (this.workToInterface)
        {
            this.workToInterface.Remove(workInterface.Work);
            var node = workInterface.node;
            node?.List?.Remove(node);
            workInterface.node = null; // Complete or Aborted
        }

        this.updateEvent?.Pulse();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                this.updateEvent = null;
            }

            base.Dispose(disposing);
        }
    }

    internal WorkDelegate method;
    private Dictionary<TWork, TaskWorkInterface<TWork>> workToInterface = new(); // syncObject
    private LinkedList<TaskWorkInterface<TWork>> standbyList = new();
    private LinkedList<TaskWorkInterface<TWork>> workingList = new();
}
