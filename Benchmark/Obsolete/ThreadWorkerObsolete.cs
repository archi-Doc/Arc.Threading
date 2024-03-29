﻿// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

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

namespace Benchmark.Obsolete;

/// <summary>
/// Represents a work to be passed to <see cref="ThreadWorkerObsolete{T}"/>.
/// </summary>
public class ThreadWorkObsolete
{
    /// <summary>
    /// Wait for the specified time until the work is completed.
    /// </summary>
    /// <param name="millisecondsToWait">The number of milliseconds to wait, or -1 to wait indefinitely.</param>
    /// <param name="abortIfTimeout">Abort the work if the specified time is elapsed.</param>
    /// <returns><see langword="true"/>: The work is complete<br/><see langword="false"/>: Not complete.</returns>
    public bool Wait(int millisecondsToWait, bool abortIfTimeout = true) => Wait(TimeSpan.FromMilliseconds(millisecondsToWait), abortIfTimeout);

    /// <summary>
    /// Wait for the specified time until the work is completed.
    /// </summary>
    /// <param name="timeToWait">The TimeSpan to wait, or negative value (e.g TimeSpan.MinValue) to wait indefinitely.</param>
    /// <param name="abortIfTimeout">Abort the work if the specified time is elapsed.</param>
    /// <returns><see langword="true"/>: The work is complete<br/><see langword="false"/>: Not complete.</returns>
    public bool Wait(TimeSpan timeToWait, bool abortIfTimeout = true)
    {
        if (threadWorkerBase == null)
        {
            throw new InvalidOperationException("ThreadWorker is not assigned.");
        }

        var end = Stopwatch.GetTimestamp() + (long)(timeToWait.TotalSeconds * Stopwatch.Frequency);
        if (timeToWait < TimeSpan.Zero)
        {// Wait infinitely.
            end = long.MaxValue;
        }

        var stateStandby = StateToInt(ThreadWorkState.Standby);
        var stateAborted = StateToInt(ThreadWorkState.Aborted);
        while (!threadWorkerBase.IsTerminated)
        {
            var state = State;
            if (state != ThreadWorkState.Standby && state != ThreadWorkState.Working)
            {
                return true;
            }

            if (Stopwatch.GetTimestamp() >= end)
            {// Timeout
                if (abortIfTimeout)
                {// State is Standby or Working or Complete or Aborted.
                    var intState = Interlocked.CompareExchange(ref this.state, stateAborted, stateStandby);
                    if (intState == stateStandby)
                    {// Standby -> Aborted
                        return false;
                    }
                    else if (intState == StateToInt(ThreadWorkState.Complete))
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
                if (threadWorkerBase.processedEvent.Wait(5))
                {
                    threadWorkerBase.processedEvent.Reset();
                }
            }
            catch
            {
                break;
            }
        }

        return false;
    }

    internal ThreadWorkerObsoleteBase? threadWorkerBase;
    internal int state;

    public ThreadWorkState State => IntToState(state);

    internal static ThreadWorkState IntToState(int state) => Unsafe.As<int, ThreadWorkState>(ref state);

    internal static int StateToInt(ThreadWorkState state) => Unsafe.As<ThreadWorkState, int>(ref state);
}

/// <summary>
/// Represents a worker class.
/// </summary>
/// <typeparam name="T">The type of a work.</typeparam>
public class ThreadWorkerObsolete<T> : ThreadWorkerObsoleteBase
    where T : ThreadWorkObsolete
{
    /// <summary>
    /// Defines the type of delegate used to process a work.
    /// </summary>
    /// <param name="worker">Worker instance.</param>
    /// <param name="work">Work instance.</param>
    /// <returns><see langword="true"/>: Complete, <see langword="false"/>: Abort.</returns>
    public delegate bool WorkDelegate(ThreadWorkerObsolete<T> worker, T work);

    public static void Process(object? parameter)
    {
        var worker = (ThreadWorkerObsolete<T>)parameter!;
        var stateStandby = ThreadWorkObsolete.StateToInt(ThreadWorkState.Standby);
        var stateWorking = ThreadWorkObsolete.StateToInt(ThreadWorkState.Working);

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
                        work.state = ThreadWorkObsolete.StateToInt(ThreadWorkState.Complete);
                    }
                    else
                    {// Aborted
                        work.state = ThreadWorkObsolete.StateToInt(ThreadWorkState.Aborted);
                    }

                    worker.processedEvent.Set();
                }
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadWorkerObsolete{T}"/> class.
    /// </summary>
    /// <param name="parent">The parent.</param>
    /// <param name="method">The method that receives and processes a work.</param>
    /// <param name="startImmediately">Starts the worker immediately.<br/>
    /// <see langword="false"/>: Manually call <see cref="ThreadCore.Start" /> to start the worker.</param>
    public ThreadWorkerObsolete(ThreadCoreBase parent, WorkDelegate method, bool startImmediately = true)
        : base(parent, Process)
    {
        this.method = method;
        if (startImmediately)
        {
            Start();
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
        work.state = ThreadWorkObsolete.StateToInt(ThreadWorkState.Standby);
        workQueue.Enqueue(work);
        addedEvent.Set();
    }

    /// <summary>
    /// Wait for the specified time until the work is completed.
    /// </summary>
    /// <param name="work">A work to wait for.</param>
    /// <param name="millisecondsToWait">The number of milliseconds to wait.</param>
    /// <param name="abortIfTimeout">Abort the work if the specified time is elapsed [the default is true].</param>
    /// <returns><see langword="true"/> if the work is complete, <see langword="false"/> if the work is not complete.</returns>
    public bool WaitForWork(T work, int millisecondsToWait, bool abortIfTimeout = true) => WaitForWork(work, TimeSpan.FromMilliseconds(millisecondsToWait), abortIfTimeout);

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
        var end = Stopwatch.GetTimestamp() + (long)(timeToWait.TotalSeconds * Stopwatch.Frequency);
        var stateStandby = ThreadWorkObsolete.StateToInt(ThreadWorkState.Standby);
        var stateAborted = ThreadWorkObsolete.StateToInt(ThreadWorkState.Aborted);

        while (!IsTerminated)
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
                    var intState = Interlocked.CompareExchange(ref work.state, stateAborted, stateStandby);
                    if (intState == stateStandby)
                    {// Standby -> Aborted
                        return false;
                    }
                    else if (intState == ThreadWorkObsolete.StateToInt(ThreadWorkState.Complete))
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
                if (processedEvent.Wait(5))
                {
                    processedEvent.Reset();
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
        while (!IsTerminated)
        {
            if (workQueue.Count == 0)
            {// Complete
                return true;
            }
            else if (millisecondsTimeout >= 0 && Stopwatch.GetTimestamp() >= end)
            {// Timeout
                return false;
            }
            else
            {// Wait
                var cancelled = CancellationToken.WaitHandle.WaitOne(DefaultInterval);
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
public class ThreadWorkerObsoleteBase : ThreadCore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadWorkerObsoleteBase"/> class.
    /// </summary>
    /// <param name="parent">The parent.</param>
    /// <param name="method">The method that executes on a System.Threading.Thread.</param>
    internal ThreadWorkerObsoleteBase(ThreadCoreBase parent, Action<object?> method)
        : base(parent, method, false)
    {
    }

    internal ManualResetEventSlim addedEvent = new(false);
    internal ManualResetEventSlim processedEvent = new(false);
}
