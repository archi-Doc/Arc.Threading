// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Represents a state of a thread work.
/// </summary>
public enum ThreadWorkState
{
    Created,

    /// <summary>
    /// Work is on standby.
    /// </summary>
    Standby,

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
    public ThreadWorkState State { get; internal set; }
}

/// <summary>
/// Represents a worker.
/// </summary>
/// <typeparam name="T">The type of a work.</typeparam>
public class ThreadWorker<T> : ThreadCore
    where T : ThreadWork
{
    public static void Process(object? parameter)
    {
        var worker = (ThreadWorker<T>)parameter!;

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
            {
                worker.method(work);
                work.State = ThreadWorkState.Complete;
            }

            worker.processedEvent.Set();
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadWorker{T}"/> class.
    /// </summary>
    /// <param name="parent">The parent.</param>
    /// <param name="method">The method that receives and processes a work.</param>
    /// <param name="startImmediately">Starts the worker immediately.<br/>
    /// <see langword="false"/>: Manually call <see cref="ThreadCore.Start" /> to start the worker.</param>
    public ThreadWorker(ThreadCoreBase parent, Action<T> method, bool startImmediately = true)
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
            throw new InvalidOperationException();
        }

        work.State = ThreadWorkState.Standby;
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
    public bool Wait(T work, int millisecondsToWait, bool abortIfTimeout = true) => this.Wait(work, TimeSpan.FromMilliseconds(millisecondsToWait), abortIfTimeout);

    /// <summary>
    /// Wait for the specified time until the work is completed.
    /// </summary>
    /// <param name="work">A work to wait for.</param>
    /// <param name="timeToWait">The TimeSpan to wait.</param>
    /// <param name="abortIfTimeout">Abort the work if the specified time is elapsed [the default is true].</param>
    /// <returns><see langword="true"/> if the work is complete, <see langword="false"/> if the work is not complete.</returns>
    public bool Wait(T work, TimeSpan timeToWait, bool abortIfTimeout = true)
    {
        timeToWait = timeToWait < TimeSpan.Zero ? TimeSpan.Zero : timeToWait;
        var end = Stopwatch.GetTimestamp() + (long)(timeToWait.TotalSeconds * (double)Stopwatch.Frequency);

        while (!this.IsTerminated)
        {
            if (work.State != ThreadWorkState.Standby)
            {
                return true;
            }

            if (Stopwatch.GetTimestamp() >= end)
            {
                if (abortIfTimeout)
                {// Abort
                    work.State = ThreadWorkState.Aborted;
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

    private Action<T> method;
    private ConcurrentQueue<T> workQueue = new();
    private ManualResetEventSlim addedEvent = new(false);
    private ManualResetEventSlim processedEvent = new(false);
}
