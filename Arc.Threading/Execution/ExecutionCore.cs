// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

public class ExecutionCore : CancellationTokenSource, IDisposable
{
    #region FieldAndProperty

    private readonly ExecutionSignalHandler? executionSignalHandler;
    private TaskCompletionSource? completionSource;
    private ExecutionCore? parent; // Root.SyncObject
    private List<ExecutionCore>? childrenList; // Root.SyncObject
    private ExecutionCore[]? childrenArray; // Root.SyncObject

    public ExecutionRoot Root { get; }

    /// <summary>
    /// Gets the owning <see cref="Arc.Threading.ExecutionStack"/> instance.
    /// </summary>
    public ExecutionStack? Stack { get; internal set; } // Root.SyncObject

    public ExecutionCore? Parent
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

    public ExecutionCore[] GetChildren()
    {
        if (this.childrenArray is { } array)
        {
            return array;
        }

        using (this.Root.SyncObject.EnterScope())
        {
            return this.GetChildrenArrayInternal();
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

    /// <summary>
    /// Gets the <see cref="System.Threading.CancellationToken"/> associated with this execution.
    /// </summary>
    public CancellationToken CancellationToken => this.Token;

    public bool IsTerminated => this.IsCancellationRequested;

    /// <summary>
    /// Gets a value indicating whether this execution can continue running.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> after <see cref="CancellationToken"/> has been canceled.
    /// </remarks>
    public bool CanContinue => !this.IsCancellationRequested;

    /// <summary>
    /// Gets a task that completes when this execution is explicitly marked as completed.
    /// </summary>
    public Task Completion => this.GetCompletionSource().Task;

    #endregion

    public static ExecutionCore? TryCreate(ExecutionCore parent, long id, ExecutionStack? stack = default, ExecutionSignalHandler? executionSignalHandler = default)
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
    }

    public ExecutionCore(ExecutionCore parent, ExecutionSignalHandler? executionSignalHandler = default)
        : this(parent, null, executionSignalHandler)
    {
    }

    internal ExecutionCore(ExecutionCore parent, ExecutionStack? stack, ExecutionSignalHandler? executionSignalHandler)
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
        this.Id = 0;
        this.Root.IdToCore[0] = this;
    }

    private ExecutionCore(ExecutionCore parent, long id, ExecutionSignalHandler? executionSignalHandler)
    {// Create an ExecutionCore with the specified Id.
        this.Root = parent.Root;
        this.Id = id;
        this.executionSignalHandler = executionSignalHandler;

        parent.AddChildInternal(this);
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

    public new void Cancel()
    {
        List<ExecutionCore>? list = default;
        while (true)
        {
            using (this.Root.SyncObject.EnterScope())
            {
                ProcessCancellationInternal(ref list, this, false);
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

    public void TryCancel()
        => this.TryCancel(false);

    /// <summary>
    /// Removes this execution from its owning <see cref="Stack"/>.
    /// </summary>using (this.Root.SyncObject.EnterScope())
    public new void Dispose()
    {
        this.TryCancel(true);
        base.Dispose();
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"Execution {this.Id:x4}";
    }

    public void AddChild(ExecutionCore child)
    {
        if (this.Root != child.Root)
        {
            throw new InvalidOperationException("The parent and child objects must be created from the same Root.");
        }

        using (this.Root.SyncObject.EnterScope())
        {
            if (child.Parent == this)
            {
                return;
            }

            child.Parent?.RemoveChildInternal(child);
            this.AddChildInternal(child);
        }
    }

    private static void ProcessCancellationInternal(ref List<ExecutionCore>? list, ExecutionCore core, bool remove)
    {
        var children = core.GetChildrenArrayInternal();
        foreach (var x in children)
        {
            ProcessCancellationInternal(ref list, x, remove);
        }

        if (!core.IsCancellationRequested)
        {
            list ??= new();
            list.Add(core);
        }

        if (remove)
        {
            core.Root.IdToCore.Remove(core.Id);

            core.Id = long.MinValue;
            core.parent = default;
            core.childrenList = default;
            core.ClearChildrenArrayInternal();
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearChildrenArrayInternal()
    {
        this.childrenArray = default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ExecutionCore[] GetChildrenArrayInternal()
    {
        if (this.childrenArray is null)
        {
            this.childrenArray = this.childrenList is null ? [] : this.childrenList.ToArray();
        }

        return this.childrenArray;
    }

    private void AddChildInternal(ExecutionCore child)
    {
        Debug.Assert(child.Parent is null);

        this.childrenList ??= new();
        this.childrenList.Add(child);
        this.ClearChildrenArrayInternal();
        child.parent = this;
    }

    private bool RemoveChildInternal(ExecutionCore child)
    {
        if (this.childrenList is null)
        {
            return false;
        }

        if (!this.childrenList.Remove(child))
        {
            return false;
        }

        this.ClearChildrenArrayInternal();
        child.parent = null;
        return true;
    }

    private void TryCancel(bool remove)
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

                ProcessCancellationInternal(ref list, this, remove);
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
