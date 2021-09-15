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

namespace Arc.Threading;

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
/// Represents a work to be passed to <see cref="ThreadWorker{T}"/>.
/// </summary>
public class ThreadWork
{
#pragma warning disable SA1401 // Fields should be private
#pragma warning disable SA1307 // Accessible fields should begin with upper-case letter
    internal int state;
#pragma warning restore SA1307 // Accessible fields should begin with upper-case letter
#pragma warning restore SA1401 // Fields should be private

    public ThreadWorkState State => IntToState(this.state);

    internal static ThreadWorkState IntToState(int state) => Unsafe.As<int, ThreadWorkState>(ref state);

    internal static int StateToInt(ThreadWorkState state) => Unsafe.As<ThreadWorkState, int>(ref state);
}

/// <summary>
/// Represents a worker.
/// </summary>
/// <typeparam name="T">The type of a work.</typeparam>
public class ThreadWorker<T> : ThreadCore
    where T : ThreadWork
{
    /// <summary>
    /// Defines the type of delegate used to process a work.
    /// </summary>
    /// <param name="worker">Worker instance.</param>
    /// <param name="work">Work instance.</param>
    /// <returns><see langword="true"/>: Complete, <see langword="false"/>: Abort.</returns>
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
                        work.state = ThreadWork.StateToInt(ThreadWorkState.Complete);
                    }
                    else
                    {// Aborted
                        work.state = ThreadWork.StateToInt(ThreadWorkState.Aborted);
                    }

                    worker.processedEvent.Set();
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
        : base(parent, Process, false)
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

        work.state = ThreadWork.StateToInt(ThreadWorkState.Standby);
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
        var stateStandby = ThreadWork.StateToInt(ThreadWorkState.Standby);
        var stateAborted = ThreadWork.StateToInt(ThreadWorkState.Aborted);

        while (!this.IsTerminated)
        {
            var state = work.State;
            if (state != ThreadWorkState.Standby && state != ThreadWorkState.Working)
            {
                return true;
            }

            if (Stopwatch.GetTimestamp() >= end)
            {
                if (abortIfTimeout)
                {// Timeout. State is Standby or Working or Complete or Aborted.
                    int intState = Interlocked.CompareExchange(ref work.state, stateAborted, stateStandby);
                    if (intState == stateStandby)
                    {// Standby -> Aborted
                        return false;
                    }
                    else if (intState == ThreadWork.StateToInt(ThreadWorkState.Complete))
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

    private WorkDelegate method;
    private ConcurrentQueue<T> workQueue = new();
    private ManualResetEventSlim addedEvent = new(false);
    private ManualResetEventSlim processedEvent = new(false);
}
