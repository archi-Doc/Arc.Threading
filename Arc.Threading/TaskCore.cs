// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1124 // Do not use regions

namespace Arc.Threading;

/// <summary>
/// Support class for <see cref="System.Threading.Tasks.Task"/>.
/// </summary>
public class TaskCore : ThreadCoreBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TaskCore"/> class.<br/>
    /// method: async <see cref="System.Threading.Tasks.Task"/> Method(<see cref="object"/>? parameter).
    /// </summary>
    /// <param name="parent">The parent.</param>
    /// <param name="method">The method that executes on a <see cref="System.Threading.Tasks.Task"/>.</param>
    /// <param name="startImmediately">Starts the task immediately.<br/>
    /// <see langword="false"/>: Manually call <see cref="Start"/> to start the task.</param>
    public TaskCore(ThreadCoreBase parent, Func<object?, Task> method, bool startImmediately = true)
    {
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }

        // this.Task = System.Threading.Tasks.Task.Run(async () => { await method(this); });
        // this.Task = System.Threading.Tasks.Task.Factory.StartNew(async () => { await method(this); }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        // this.Task = new Task(async () => await method(this).ConfigureAwait(false), TaskCreationOptions.LongRunning);
        // this.Task = new Task(() => method(this).Wait(this.CancellationToken), TaskCreationOptions.LongRunning);

        this.Task = new Task(() => method(this).Wait(), TaskCreationOptions.LongRunning);
        this.Prepare(parent); // this.Task (this.IsRunning) might be referenced after this method.
        if (startImmediately)
        {
            this.Start(false);
        }
    }

    /// <inheritdoc/>
    public override void Start(bool includeChildren = false)
    {
        if (Interlocked.CompareExchange(ref this.started, 1, 0) == 0)
        {
            this.Task.Start();
        }

        if (includeChildren)
        {
            this.StartChildren();
        }
    }

    /// <inheritdoc/>
    public override bool IsRunning => (this.started != 0) && !this.Task.IsCompleted; // this.Task.Status != TaskStatus.RanToCompletion && this.Task.Status != TaskStatus.Canceled && this.Task.Status != TaskStatus.Faulted;

    /// <inheritdoc/>
    public override bool IsThreadOrTask => true;

    /// <summary>
    /// Gets an instance of <see cref="System.Threading.Tasks.Task"/>.
    /// </summary>
    public Task Task { get; }

    private int started;

    #region IDisposable Support

    /// <summary>
    /// Finalizes an instance of the <see cref="TaskCore"/> class.
    /// </summary>
    ~TaskCore()
    {
        this.Dispose(false);
    }

    /// <summary>
    /// free managed/native resources.
    /// </summary>
    /// <param name="disposing">true: free managed resources.</param>
    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                // this.Task.Dispose(); // Not necessary
            }

            base.Dispose(disposing);
        }
    }
    #endregion
}

/// <summary>
/// Support class for <see cref="System.Threading.Tasks.Task{TResult}"/>.<br/>
/// </summary>
/// <typeparam name="TResult">The type of the result produced by this task.</typeparam>
public class TaskCore<TResult> : ThreadCoreBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TaskCore{TResult}"/> class.<br/>
    /// method: async <see cref="System.Threading.Tasks.Task{TResult}"/> Method(<see cref="object"/>? parameter).
    /// </summary>
    /// <param name="parent">The parent.</param>
    /// <param name="method">The method that executes on a <see cref="System.Threading.Tasks.Task{TResult}"/>.</param>
    /// <param name="startImmediately">Starts the task immediately.<br/>
    /// <see langword="false"/>: Manually call <see cref="Start"/> to start the task.</param>
    public TaskCore(ThreadCoreBase parent, Func<object?, Task<TResult>> method, bool startImmediately = true)
    {
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }

        // this.Task = System.Threading.Tasks.Task.Run(async () => await method(this));
        /*this.Task = new Task<TResult>(async () =>
        {
            return await method(this);
        });*/

        this.Task = new Task<TResult>(() => method(this).Result, TaskCreationOptions.LongRunning);
        this.Prepare(parent); // this.Task (this.IsRunning) might be referenced after this method.
        if (startImmediately)
        {
            this.Start(false);
        }
    }

    /// <inheritdoc/>
    public override void Start(bool includeChildren = false)
    {
        if (Interlocked.CompareExchange(ref this.started, 1, 0) == 0)
        {
            this.Task.Start();
        }

        if (includeChildren)
        {
            this.StartChildren();
        }
    }

    /// <inheritdoc/>
    public override bool IsRunning => (this.started != 0) && !this.Task.IsCompleted;

    /// <inheritdoc/>
    public override bool IsThreadOrTask => true;

    /// <summary>
    /// Gets an instance of <see cref="System.Threading.Tasks.Task{TResult}"/>.
    /// </summary>
    public Task<TResult> Task { get; }

    private int started;

    #region IDisposable Support

    /// <summary>
    /// Finalizes an instance of the <see cref="TaskCore{T}"/> class.
    /// </summary>
    ~TaskCore()
    {
        this.Dispose(false);
    }

    /// <summary>
    /// free managed/native resources.
    /// </summary>
    /// <param name="disposing">true: free managed resources.</param>
    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                // this.Task.Dispose(); // Not necessary
            }

            base.Dispose(disposing);
        }
    }
    #endregion
}
