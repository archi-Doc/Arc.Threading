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
/// Represents a state of a thread work.<br/>
/// Created -> Standby -> Abort / Working -> Completed.
/// </summary>
public enum ThreadWorkState : int
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
/// Represents a work to be processed by <see cref="ThreadWorker{T}"/>.
/// </summary>
public class ThreadWork
{
    /// <summary>
    /// Wait for the specified time until the work is completed.
    /// </summary>
    /// <param name="millisecondsToWait">The number of milliseconds to wait, or -1 to wait indefinitely.</param>
    /// <param name="abortIfTimeout">Abort the work if the specified time is elapsed.</param>
    /// <returns><see langword="true"/>: The work is complete<br/><see langword="false"/>: Not complete.</returns>
    public bool Wait(int millisecondsToWait, bool abortIfTimeout = true) => this.Wait(TimeSpan.FromMilliseconds(millisecondsToWait), abortIfTimeout);

    /// <summary>
    /// Wait for the specified time until the work is completed.
    /// </summary>
    /// <param name="timeToWait">The TimeSpan to wait, or negative value (e.g TimeSpan.MinValue) to wait indefinitely.</param>
    /// <param name="abortIfTimeout">Abort the work if the specified time is elapsed.</param>
    /// <returns><see langword="true"/>: The work is complete<br/><see langword="false"/>: Not complete.</returns>
    public bool Wait(TimeSpan timeToWait, bool abortIfTimeout = true)
    {
        if (this.threadWorkerBase == null)
        {
            throw new InvalidOperationException("ThreadWorker is not assigned.");
        }

        var end = Stopwatch.GetTimestamp() + (long)(timeToWait.TotalSeconds * (double)Stopwatch.Frequency);
        if (timeToWait < TimeSpan.Zero)
        {// Wait indefinitely.
            end = long.MaxValue;
        }

        while (!this.threadWorkerBase.IsTerminated)
        {
            var state = this.State;
            if (state != ThreadWorkState.Standby && state != ThreadWorkState.Working)
            {
                return true;
            }

            if (Stopwatch.GetTimestamp() >= end)
            {// Timeout
                int intState; // State is Standby or Working or Complete or Aborted.
                if (abortIfTimeout)
                {// Abort
                    intState = Interlocked.CompareExchange(ref this.state, ThreadWork.StateToInt(ThreadWorkState.Aborted), ThreadWork.StateToInt(ThreadWorkState.Standby));
                }
                else
                {
                    intState = this.state;
                }

                if(intState == ThreadWork.StateToInt(ThreadWorkState.Complete))
                {// Complete
                    return true;
                }
                else
                {// Standby or Working or Aborted
                    return false;
                }
            }

            try
            {
                this.completeEvent?.Wait(ThreadCore.DefaultInterval, this.threadWorkerBase.CancellationToken);
            }
            catch
            {
                break;
            }
        }

        return false;
    }

    internal ThreadWorkerBase? threadWorkerBase;
    internal int state;
    internal ManualResetEventSlim? completeEvent = new(false);

    public ThreadWorkState State => IntToState(this.state);

    internal static ThreadWorkState IntToState(int state) => Unsafe.As<int, ThreadWorkState>(ref state);

    internal static int StateToInt(ThreadWorkState state) => Unsafe.As<ThreadWorkState, int>(ref state);
}

/// <summary>
/// Represents a worker class.
/// </summary>
/// <typeparam name="T">The type of a work.</typeparam>
public class ThreadWorker<T> : ThreadWorkerBase
    where T : ThreadWork
{
    /// <summary>
    /// Defines the type of delegate to process a work.
    /// </summary>
    /// <param name="worker">Worker instance.</param>
    /// <param name="work">Work instance.</param>
    /// <returns><see langword="true"/>: Complete, <see langword="false"/>: Abort(Error).</returns>
    public delegate bool WorkDelegate(ThreadWorker<T> worker, T work);

    public static void Process(object? parameter)
    {
        var worker = (ThreadWorker<T>)parameter!;
        var stateStandby = ThreadWork.StateToInt(ThreadWorkState.Standby);
        var stateWorking = ThreadWork.StateToInt(ThreadWorkState.Working);

        while (!worker.IsTerminated)
        {
            try
            {
                if (worker.addedEvent?.Wait(ThreadCore.DefaultInterval, worker.CancellationToken) == true)
                {
                    worker.addedEvent?.Reset();
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
                    if (worker.method(worker, work))
                    {// Copmplete
                        work.state = ThreadWork.StateToInt(ThreadWorkState.Complete);
                    }
                    else
                    {// Aborted
                        work.state = ThreadWork.StateToInt(ThreadWorkState.Aborted);
                    }

                    if (work.completeEvent is { } e)
                    {
                        e.Set();
                        e.Dispose();
                        work.completeEvent = null;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadWorker{T}"/> class.
    /// </summary>
    /// <param name="parent">The parent.</param>
    /// <param name="method">The method that receives and processes a work.</param>
    /// <param name="startImmediately">Starts the worker immediately.<br/>
    /// <see langword="false"/>: Manually call <see cref="ThreadCore.Start" /> to start the worker.</param>
    public ThreadWorker(ThreadCoreBase parent, WorkDelegate method, bool startImmediately = true)
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
    /// <param name="work">A work.<br/>work.State is set to <see cref="ThreadWorkState.Standby"/>.</param>
    public void Add(T work)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(null);
        }

        if (work.State != ThreadWorkState.Created)
        {
            throw new InvalidOperationException("Only newly created work can be added to a worker.");
        }

        work.threadWorkerBase = this;
        work.state = ThreadWork.StateToInt(ThreadWorkState.Standby);
        this.workQueue.Enqueue(work);
        this.addedEvent?.Set();
    }

    /// <summary>
    /// Waits for the completion of all works.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1 to wait indefinitely.</param>
    /// <returns><see langword="true"/>: All works are complete.<br/><see langword="false"/>: Timeout or cancelled.</returns>
    public bool WaitForCompletion(int millisecondsTimeout)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(null);
        }

        var end = Stopwatch.GetTimestamp() + (long)(millisecondsTimeout * (double)Stopwatch.Frequency / 1000);
        while (!this.IsTerminated)
        {
            if (this.workQueue.Count == 0)
            {// Complete
                return true;
            }
            else if (millisecondsTimeout >= 0 && Stopwatch.GetTimestamp() >= end)
            {// Timeout
                return false;
            }
            else
            {// Wait
                var cancelled = this.CancellationToken.WaitHandle.WaitOne(ThreadWorker<T>.DefaultInterval);
                if (cancelled)
                {
                    return false;
                }
            }
        }

        return false;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                this.addedEvent?.Dispose();
                this.addedEvent = null;
            }

            base.Dispose(disposing);
        }
    }

    private WorkDelegate method;
    private ConcurrentQueue<T> workQueue = new();
}

/// <summary>
/// Represents a base worker class.
/// </summary>
public class ThreadWorkerBase : ThreadCore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadWorkerBase"/> class.
    /// </summary>
    /// <param name="parent">The parent.</param>
    /// <param name="method">The method that executes on a System.Threading.Thread.</param>
    internal ThreadWorkerBase(ThreadCoreBase parent, Action<object?> method)
        : base(parent, method, false)
    {
    }

    internal ManualResetEventSlim? addedEvent = new(false);
}
