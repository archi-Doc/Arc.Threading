// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

[DebuggerDisplay("{ToString()}")]
public class ExecutionCore : CancellationTokenSource, IDisposable
{
    private const int WaitInterval = 10;

    #region FieldAndProperty

    private readonly ExecutionSignalHandler? executionSignalHandler;
    private TaskCompletionSource? completionSource;

#pragma warning disable SA1307 // Accessible fields should begin with upper-case letter
#pragma warning disable SA1401 // Fields should be private
#pragma warning disable SA1202 // Elements should be ordered by access
    protected bool disposed;
    internal ExecutionGroup? parent; // Root.SyncObject
#pragma warning restore SA1202 // Elements should be ordered by access
#pragma warning restore SA1401 // Fields should be private
#pragma warning restore SA1307 // Accessible fields should begin with upper-case letter

    public ExecutionRoot Root { get; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets the owning <see cref="Arc.Threading.ExecutionStack"/> instance.
    /// </summary>
    public ExecutionStack? Stack { get; internal set; } // Root.SyncObject

    public ExecutionGroup? Parent
    {
        get => this.parent;
        set
        {
            if (this.IsRoot ||
                this.parent == value)
            {
                return;
            }
            else if (value is null)
            {
                using (this.Root.SyncObject.EnterScope())
                {
                    this.parent?.RemoveChildInternal(this);
                }
            }
            else
            {
                value.AddChild(this);
            }
        }
    }

    /// <summary>
    /// Gets the identifier of this execution within the owning <see cref="Stack"/>.
    /// </summary>
    public long Id { get; private set; }

    /// <summary>
    /// Gets a value indicating whether this execution is the root execution (<c>Id == 0</c>).
    /// </summary>
    public bool IsRoot => this.Id == 0;

    public bool IsIndependent { get; set; }

    public virtual bool IsActive => !this.IsCancellationRequested;

    public virtual bool IsTerminated => this.IsCancellationRequested;

    public bool IsGroup => typeof(ExecutionGroup).IsAssignableFrom(this.GetType());

    /// <summary>
    /// Gets the <see cref="System.Threading.CancellationToken"/> associated with this execution.
    /// </summary>
    public CancellationToken CancellationToken => this.Token;

    /// <summary>
    /// Gets a task that completes when this execution is explicitly marked as completed.
    /// </summary>
    public Task Completion => this.GetCompletionSource().Task;

    #endregion

    /*public static ExecutionCore? TryCreate(ExecutionCore parent, long id, ExecutionStack? stack = default, ExecutionSignalHandler? executionSignalHandler = default)
    {
        var root = parent.Root;
        using (root.SyncObject.EnterScope())
        {
            if (root.IdToCore.TryGetValue(id, out var core))
            {// Already exists
                return core;
            }

            core = new ExecutionCore(parent, id, executionSignalHandler);
            root.IdToCore.Add(id, core);
            stack?.AddInternal(core);
            return core;
        }
    }*/

    public ExecutionCore(ExecutionGroup parent, ExecutionSignalHandler? executionSignalHandler = default)
        : this(parent, null, executionSignalHandler)
    {
    }

    public ExecutionCore(ExecutionGroup parent, bool isIndependent, ExecutionSignalHandler? executionSignalHandler = default)
        : this(parent, null, executionSignalHandler)
    {
        this.IsIndependent = isIndependent;
    }

    internal ExecutionCore(ExecutionGroup parent, ExecutionStack? stack, ExecutionSignalHandler? executionSignalHandler)
    {
        this.Root = parent.Root;
        this.executionSignalHandler = executionSignalHandler;

        using (this.Root.SyncObject.EnterScope())
        {
            while (true)
            {
                var id = Random.Shared.NextInt64();
                if (this.Root.IdToCore.TryAdd(id, this))
                {
                    this.Id = id;
                    break;
                }
            }

            stack?.AddInternal(this);
            parent.AddChildInternal(this);
        }
    }

    private protected ExecutionCore()
    {// Root
        this.Root = (ExecutionRoot)this;
        this.Name = "Root";
        this.Id = 0;
        this.Root.IdToCore[0] = this;
    }

    /*private ExecutionCore(ExecutionCore parent, long id, ExecutionSignalHandler? executionSignalHandler)
    {// Create an ExecutionCore with the specified Id.
        this.Root = parent.Root;
        this.Id = id;
        this.executionSignalHandler = executionSignalHandler;

        parent.AddChildInternal(this);
    }*/

    /// <summary>
    /// Wait for the specified time (<see cref="Task.Delay(TimeSpan)"/>).
    /// </summary>
    /// <param name="millisecondsToWait">The number of milliseconds to wait.</param>
    /// <param name="cancellationToken">An additional cancellation token that can be used to cancel the delay.</param>
    /// <returns><see langword="true"/> if the time successfully elapsed, <see langword="false"/> if the thread/task is terminated.</returns>
    public Task<bool> Delay(int millisecondsToWait, CancellationToken cancellationToken = default)
        => this.Delay(TimeSpan.FromMilliseconds(millisecondsToWait), cancellationToken);

    /// <summary>
    /// Wait for the specified time (<see cref="Task.Delay(TimeSpan)"/>).
    /// </summary>
    /// <param name="delay">The TimeSpan to wait.</param>
    /// <param name="cancellationToken">An additional cancellation token that can be used to cancel the delay.</param>
    /// <returns><see langword="true"/> if the time successfully elapsed, <see langword="false"/> if the thread/task is terminated.</returns>
    public async Task<bool> Delay(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        var internalToken = this.CancellationToken;

        if (internalToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            var task = !cancellationToken.CanBeCanceled || cancellationToken == internalToken
                ? Task.Delay(delay, internalToken)
                : Task.Delay(delay, internalToken).WaitAsync(cancellationToken);
            await task.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asynchronously waits indefinitely for this execution to terminate.
    /// </summary>
    /// <param name="cancellationToken">An additional token that can cancel the wait operation.</param>
    /// <returns>A task that represents the wait operation.</returns>
    public Task WaitForTermination(CancellationToken cancellationToken = default)
        => this.WaitForTermination(Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>
    /// Asynchronously waits for the termination of the thread/task.<br/>
    /// Note that you need to call <see cref="RequestTermination(RequestTerminationOptions)"/> to terminate the object from outside the thread/task.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait before termination, or -1 to wait indefinitely.</param>
    /// <param name="cancellationToken">An additional cancellation token to cancel the wait operation.</param>
    /// <returns>A task that represents waiting for termination.</returns>
    public Task<bool> WaitForTermination(int millisecondsTimeout, CancellationToken cancellationToken = default)
        => this.WaitForTermination(TimeSpan.FromMilliseconds(millisecondsTimeout));

    /// <summary>
    /// Asynchronously waits for the termination of the thread/task.<br/>
    /// Note that you need to call <see cref="RequestTermination(RequestTerminationOptions)"/> to terminate the object from outside the thread/task.
    /// </summary>
    /// <param name="timeout">The <see cref="TimeSpan"/> to wait before termination.</param>
    /// <param name="cancellationToken">An additional cancellation token to cancel the wait operation.</param>
    /// <returns>A task that represents waiting for termination.</returns>
    public virtual async Task<bool> WaitForTermination(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        while (true)
        {
            var notTerminated = 0;
            using (this.Root.SyncObject.EnterScope())
            {
                CountObjects(this, ref notTerminated);
                if (notTerminated == 0)
                {
                    return true;
                }
            }

            try
            {
                await Task.Delay(WaitInterval, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }

            if (timeout != Timeout.InfiniteTimeSpan &&
                Stopwatch.GetElapsedTime(startTimestamp) >= timeout)
            {
                return false;
            }
        }

        static void CountObjects(ExecutionCore core, ref int notTerminated)
        {
            if (core is ExecutionGroup group)
            {
                if (group.Count > 0)
                {
                    var children = group.GetChildrenArrayInternal();
                    foreach (var x in children)
                    {
                        CountObjects(x, ref notTerminated);
                    }
                }
            }
            else
            {
                if (!core.IsTerminated)
                {
                    notTerminated++;
                }
            }
        }
    }

    public void TrySetCompleted()
        => this.GetCompletionSource().TrySetResult();

    public void SendSignal(ExecutionSignal signal)
    {
        if (this.executionSignalHandler is null)
        {
            this.OnSignalReceived(signal);
        }
        else
        {
            this.executionSignalHandler.Invoke(this, signal);
        }
    }

    public virtual void OnSignalReceived(ExecutionSignal signal)
    {
    }

    public virtual void OnRemoved()
    {
        this.Id = 0;
        this.parent = default;
    }

    public new void Cancel()
    {
        List<ExecutionCore>? list = default;
        while (true)
        {
            using (this.Root.SyncObject.EnterScope())
            {
                ProcessCancellationInternal(ref list, this, false, default);
            }

            if (list is null || list.Count == 0)
            {
                break;
            }

            foreach (var x in list)
            {
                ((CancellationTokenSource)x).Cancel();
            }

            list.Clear();
        }
    }

    public void RequestTermination(RequestTerminationOptions options = default)
        => this.RequestTermination(false, options);

    /// <summary>
    /// Removes this execution from its owning <see cref="Stack"/>.
    /// </summary>using (this.Root.SyncObject.EnterScope())
    public new void Dispose()
    {
        if (!this.disposed)
        {
            this.disposed = true;

            this.RequestTermination(true, default);
            base.Dispose();
        }
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"Core {this.Name} {(ushort)this.Id:x4}";
    }

    private static void ProcessCancellationInternal(ref List<ExecutionCore>? list, ExecutionCore core, bool remove, RequestTerminationOptions options)
    {
        if (core is ExecutionGroup group)
        {
            var children = group.GetChildrenArrayInternal();
            foreach (var x in children)
            {
                if (!x.IsIndependent ||
                    options.HasFlag(RequestTerminationOptions.IncludeIndependent))
                {
                    ProcessCancellationInternal(ref list, x, remove, options);
                }
            }
        }

        if (!core.IsCancellationRequested)
        {
            list ??= new();
            list.Add(core);
        }

        if (remove)
        {
            core.Root.IdToCore.Remove(core.Id);
            core.OnRemoved();
        }
    }

    private TaskCompletionSource GetCompletionSource()
    {
        var current = Volatile.Read(ref this.completionSource);
        if (current is not null)
        {
            return current;
        }

        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return Interlocked.CompareExchange(ref this.completionSource, created, null) ?? created;
    }

    private void RequestTermination(bool remove, RequestTerminationOptions options)
    {
        List<ExecutionCore>? list = default;
        while (true)
        {
            using (this.Root.SyncObject.EnterScope())
            {
                if (remove)
                {
                    this.parent?.RemoveChildInternal(this);
                    this.Stack?.RemoveInternal(this);
                }

                ProcessCancellationInternal(ref list, this, remove, options);
                remove = false;
            }

            if (list is null || list.Count == 0)
            {
                break;
            }

            foreach (var x in list)
            {
                try
                {
                    ((CancellationTokenSource)x).Cancel();
                }
                catch
                {
                }
            }

            list.Clear();
        }
    }
}
