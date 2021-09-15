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
using Arc.Threading;

#pragma warning disable SA1401 // Fields should be private
#pragma warning disable SA1307 // Accessible fields should begin with upper-case letter

namespace Benchmark.Test;

/// <summary>
/// Represents a state of a thread work.<br/>
/// Created -> Standby -> Abort/Working -> Completed.
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
/// Represents a work to be passed to <see cref="ThreadWorker2{T}"/>.
/// </summary>
public class ThreadWork2
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
            throw new InvalidOperationException("ThreadWorker2 is not assigned.");
        }

        var end = Stopwatch.GetTimestamp() + (long)(timeToWait.TotalSeconds * (double)Stopwatch.Frequency);
        if (timeToWait < TimeSpan.Zero)
        {// Wait infinitely.
            end = long.MaxValue;
        }

        var stateStandby = ThreadWork2.StateToInt(ThreadWorkState.Standby);
        var stateAborted = ThreadWork2.StateToInt(ThreadWorkState.Aborted);
        while (!this.threadWorkerBase.IsTerminated)
        {
            var state = this.State;
            if (state != ThreadWorkState.Standby && state != ThreadWorkState.Working)
            {
                return true;
            }

            if (Stopwatch.GetTimestamp() >= end)
            {// Timeout
                if (abortIfTimeout)
                {// State is Standby or Working or Complete or Aborted.
                    int intState = Interlocked.CompareExchange(ref this.state, stateAborted, stateStandby);
                    if (intState == stateStandby)
                    {// Standby -> Aborted
                        return false;
                    }
                    else if (intState == ThreadWork2.StateToInt(ThreadWorkState.Complete))
                    {// Complete
                        return true;
                    }
                    else
                    {// Working or Aborted
                        return false;
                    }
                }

                return false;
            }

            try
            {
                if (this.threadWorkerBase.processedEvent.Wait(5))
                {
                    this.threadWorkerBase.processedEvent.Reset();
                }
            }
            catch
            {
                break;
            }
        }

        return false;
    }

    internal ThreadWorkerBase2? threadWorkerBase;
    internal int state;

    public ThreadWorkState State => IntToState(this.state);

    internal static ThreadWorkState IntToState(int state) => Unsafe.As<int, ThreadWorkState>(ref state);

    internal static int StateToInt(ThreadWorkState state) => Unsafe.As<ThreadWorkState, int>(ref state);
}

/// <summary>
/// Represents a worker class.
/// </summary>
/// <typeparam name="T">The type of a work.</typeparam>
public class ThreadWorker2<T> : ThreadWorkerBase2
    where T : ThreadWork2
{
    /// <summary>
    /// Defines the type of delegate used to process a work.
    /// </summary>
    /// <param name="worker">Worker instance.</param>
    /// <param name="work">Work instance.</param>
    /// <returns><see langword="true"/>: Complete, <see langword="false"/>: Abort.</returns>
    public delegate bool WorkDelegate(ThreadWorker2<T> worker, T work);

    public static void Process(object? parameter)
    {
        var worker = (ThreadWorker2<T>)parameter!;
        var stateStandby = ThreadWork2.StateToInt(ThreadWorkState.Standby);
        var stateWorking = ThreadWork2.StateToInt(ThreadWorkState.Working);

        while (!worker.IsTerminated)
        {
            try
            {
                if (worker.addedEvent.Wait(5, worker.CancellationToken))
                {
                    worker.addedEvent.Reset();
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
                        work.state = ThreadWork2.StateToInt(ThreadWorkState.Complete);
                    }
                    else
                    {// Aborted
                        work.state = ThreadWork2.StateToInt(ThreadWorkState.Aborted);
                    }

                    worker.processedEvent.Set();
                }
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadWorker2{T}"/> class.
    /// </summary>
    /// <param name="parent">The parent.</param>
    /// <param name="method">The method that receives and processes a work.</param>
    /// <param name="startImmediately">Starts the worker immediately.<br/>
    /// <see langword="false"/>: Manually call <see cref="ThreadCore.Start" /> to start the worker.</param>
    public ThreadWorker2(ThreadCoreBase parent, WorkDelegate method, bool startImmediately = true)
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
        if (work.State != ThreadWorkState.Created)
        {
            throw new InvalidOperationException("Only newly created work can be added to a worker.");
        }

        work.threadWorkerBase = this;
        work.state = ThreadWork2.StateToInt(ThreadWorkState.Standby);
        this.workQueue.Enqueue(work);
        this.addedEvent.Set();
    }

    /// <summary>
    /// Wait for the specified time until the work is completed.
    /// </summary>
    /// <param name="work">A work to wait for.</param>
    /// <param name="millisecondsToWait">The number of milliseconds to wait.</param>
    /// <param name="abortIfTimeout">Abort the work if the specified time is elapsed [the default is true].</param>
    /// <returns><see langword="true"/> if the work is complete, <see langword="false"/> if the work is not complete.</returns>
    public bool WaitForWork(T work, int millisecondsToWait, bool abortIfTimeout = true) => this.WaitForWork(work, TimeSpan.FromMilliseconds(millisecondsToWait), abortIfTimeout);

    /// <summary>
    /// Wait for the specified time until the work is completed.
    /// </summary>
    /// <param name="work">A work to wait for.</param>
    /// <param name="timeToWait">The TimeSpan to wait.</param>
    /// <param name="abortIfTimeout">Abort the work if the specified time is elapsed [the default is true].</param>
    /// <returns><see langword="true"/> if the work is complete, <see langword="false"/> if the work is not complete.</returns>
    public bool WaitForWork(T work, TimeSpan timeToWait, bool abortIfTimeout = true)
    {
        timeToWait = timeToWait < TimeSpan.Zero ? TimeSpan.Zero : timeToWait;
        var end = Stopwatch.GetTimestamp() + (long)(timeToWait.TotalSeconds * (double)Stopwatch.Frequency);
        var stateStandby = ThreadWork2.StateToInt(ThreadWorkState.Standby);
        var stateAborted = ThreadWork2.StateToInt(ThreadWorkState.Aborted);

        while (!this.IsTerminated)
        {
            var state = work.State;
            if (state != ThreadWorkState.Standby && state != ThreadWorkState.Working)
            {
                return true;
            }

            if (Stopwatch.GetTimestamp() >= end)
            {// Timeout
                if (abortIfTimeout)
                {// State is Standby or Working or Complete or Aborted.
                    int intState = Interlocked.CompareExchange(ref work.state, stateAborted, stateStandby);
                    if (intState == stateStandby)
                    {// Standby -> Aborted
                        return false;
                    }
                    else if (intState == ThreadWork2.StateToInt(ThreadWorkState.Complete))
                    {// Complete
                        return true;
                    }
                    else
                    {// Working or Aborted
                        return false;
                    }
                }

                return false;
            }

            try
            {
                if (this.processedEvent.Wait(5))
                {
                    this.processedEvent.Reset();
                }
            }
            catch
            {
                break;
            }
        }

        return false;
    }

    /// <summary>
    /// Waits for the completion of all works.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1 to wait indefinitely.</param>
    /// <returns><see langword="true"/>: All works are complete.<br/><see langword="false"/>: Timeout or cancelled.</returns>
    public bool WaitForCompletion(int millisecondsTimeout)
    {
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
                var cancelled = this.CancellationToken.WaitHandle.WaitOne(ThreadWorker2<T>.DefaultInterval);
                if (cancelled)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private WorkDelegate method;
    private ConcurrentQueue<T> workQueue = new();
}

/// <summary>
/// Represents a base worker class.
/// </summary>
public class ThreadWorkerBase2 : ThreadCore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadWorkerBase2"/> class.
    /// </summary>
    /// <param name="parent">The parent.</param>
    /// <param name="method">The method that executes on a System.Threading.Thread.</param>
    internal ThreadWorkerBase2(ThreadCoreBase parent, Action<object?> method)
        : base(parent, method, false)
    {
    }

    internal ManualResetEventSlim addedEvent = new(false);
    internal ManualResetEventSlim processedEvent = new(false);
}
