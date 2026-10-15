// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Represents an <see cref="ExecutionCore"/> node that owns and coordinates a mutable collection
/// of child executions.
/// </summary>
/// <remarks>
/// <para>
/// A group is a container with no thread or task of its own. Terminating a group cancels the group and its non-independent descendants;
/// waiting for a group waits for its non-group descendants. By default, signals are forwarded to all children, including independent ones.
/// </para>
/// <para>
/// Child membership is synchronized via <c>Root.SyncObject</c> and exposed through a cached array snapshot for efficient iteration.
/// The cache is invalidated whenever children are added or removed.
/// </para>
/// </remarks>
public class ExecutionGroup : ExecutionCore
{
    #region FieldAndProperty

    private readonly List<ExecutionCore> childrenList = new(); // Root.SyncObject
    private ExecutionCore[]? childrenArray; // Root.SyncObject

    /// <summary>
    /// Gets the number of registered child executions. The value is advisory during concurrent changes.
    /// </summary>
    public int ChildCount => this.childrenList.Count;

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionGroup"/> class under the specified parent group.
    /// </summary>
    /// <param name="parent">The parent group that owns this group.</param>
    /// <param name="isIndependent">
    /// Whether recursive termination and waiting skip this group by default. Signals are still forwarded.
    /// </param>
    /// <param name="name">An optional display name. If <see langword="null"/>, an empty name is used.</param>
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="parent"/> has been disposed.</exception>
    /// <remarks>If <paramref name="parent"/> is terminated, this group is terminated before the constructor returns.</remarks>
    public ExecutionGroup(ExecutionGroup parent, bool isIndependent = false, string? name = default)
        : base(parent, isIndependent)
    {
        this.Name = name ?? string.Empty;
    }

    internal ExecutionGroup(ExecutionGroup parent, ExecutionStack stack, bool isIndependent, ExecutionSignalHandler? signalHandler = default)
        : base(parent, stack, isIndependent, signalHandler)
    {
    }

    private protected ExecutionGroup()
        : base()
    {
    }

    /// <summary>
    /// Adds a child execution to this group by assigning its <see cref="ExecutionCore.Parent"/> to the current instance.
    /// </summary>
    /// <param name="child">The child execution to add. It is moved from its current parent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="child"/> belongs to a different root, or is this group or one of its ancestors.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This group or <paramref name="child"/> has been disposed.</exception>
    /// <remarks>
    /// If this group is terminated, termination of <paramref name="child"/> is requested immediately.
    /// Adding a current child or the root has no effect.
    /// </remarks>
    public void AddChild(ExecutionCore child)
    {
        ArgumentNullException.ThrowIfNull(child);
        child.Parent = this;
    }

    /// <summary>
    /// Gets an existing child <see cref="ExecutionGroup"/> with the specified <paramref name="name"/>,<br/>
    /// or creates and returns a new one when no match exists.
    /// </summary>
    /// <param name="isIndependent">
    /// Whether recursive termination and waiting skip a newly created group by default. An existing group must have the same setting.
    /// </param>
    /// <param name="name">The group name to search for.</param>
    /// <returns>
    /// An existing child group whose name matches <paramref name="name"/> using
    /// <see cref="StringComparison.Ordinal"/>, or a newly created child group.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A group with the same name exists, but its independence setting differs from <paramref name="isIndependent"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This group has been disposed.</exception>
    /// <remarks>
    /// Matching ignores termination state, so the returned group may already be terminated, and work attached to it terminates immediately.
    /// Dispose a terminated group so that a later call creates a new group with the same name.
    /// </remarks>
    public ExecutionGroup GetOrAddGroup(bool isIndependent, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        using (this.Root.SyncObject.EnterScope())
        {
            ObjectDisposedException.ThrowIf(this.IsDisposed, this);
            foreach (var x in this.childrenList)
            {
                if (x is ExecutionGroup group &&
                    string.Equals(x.Name, name, StringComparison.Ordinal))
                {
                    if (group.IsIndependent != isIndependent)
                    {
                        throw new InvalidOperationException("An ExecutionGroup with the same name already exists with different independence settings.");
                    }

                    return group;
                }
            }

            // ExecutionGroup's constructor re-enters Root.SyncObject through AddChildInternal.
            // System.Threading.Lock is reentrant, so this is safe.
            var newGroup = new ExecutionGroup(this, isIndependent, name);
            return newGroup;
        }
    }

    /// <summary>
    /// Gets a stable array snapshot of the current children.
    /// </summary>
    /// <returns>An array containing the current child executions.</returns>
    /// <remarks>
    /// Returns a cached snapshot when available; otherwise, builds and caches a new snapshot under synchronization.
    /// The array is shared with other callers and does not reflect later membership changes. Callers must not modify it.
    /// </remarks>
    public ExecutionCore[] GetChildren()
    {
        var array = Volatile.Read(ref this.childrenArray);
        if (array is not null)
        {
            return array;
        }

        using (this.Root.SyncObject.EnterScope())
        {
            return this.GetChildrenArrayInternal();
        }
    }

    /// <summary>
    /// Finds a direct child execution by its identifier.
    /// </summary>
    /// <param name="id">The child execution identifier.</param>
    /// <returns>The matching child execution, or <see langword="null"/> if not found.</returns>
    public ExecutionCore? FindChild(int id)
    {
        using (this.Root.SyncObject.EnterScope())
        {
            foreach (var child in this.childrenList)
            {
                if (child.Id == id)
                {
                    return child;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Attempts to locate a direct child execution and retrieve its cancellation token.
    /// </summary>
    /// <param name="id">The child execution identifier.</param>
    /// <param name="cancellationToken">
    /// When this method returns <see langword="true"/>, contains the child's cancellation token;
    /// otherwise, the default token.
    /// </param>
    /// <returns><see langword="true"/> if a child with the specified id exists; otherwise, <see langword="false"/>.</returns>
    public bool TryGetChildCancellationToken(int id, out CancellationToken cancellationToken)
    {
        if (this.FindChild(id) is { } core)
        {
            cancellationToken = core.CancellationToken;
            return true;
        }
        else
        {
            cancellationToken = default;
            return false;
        }
    }

    /// <summary>
    /// Propagates an execution signal to all current children, including independent children.
    /// </summary>
    /// <param name="signal">The signal to forward.</param>
    /// <remarks>
    /// Each child receives the signal through <see cref="ExecutionCore.SendSignal(ExecutionSignal)"/>, using a snapshot of the children taken at the start.
    /// Every child receives the signal even if another child throws.
    /// A single exception is rethrown after forwarding; multiple exceptions are thrown as an <see cref="AggregateException"/>.
    /// </remarks>
    /// <exception cref="AggregateException">More than one child threw an exception.</exception>
    public override void OnSignalReceived(ExecutionSignal signal)
    {
        List<Exception>? exceptions = null;
        var children = this.GetChildren();
        foreach (var x in children)
        {
            try
            {
                x.SendSignal(signal);
            }
            catch (Exception ex)
            {
                (exceptions ??= new()).Add(ex);
            }
        }

        if (exceptions is not null)
        {
            if (exceptions.Count == 1)
            {
                ExceptionDispatchInfo.Throw(exceptions[0]);
            }

            throw new AggregateException(exceptions);
        }
    }

    /// <summary>
    /// Returns a compact debug string for this group.
    /// </summary>
    /// <returns>A display string containing the name (or "Group" if empty), the number of children, and the lower 16 bits of the identifier in hexadecimal.</returns>
    public override string ToString()
    {
        var name = string.IsNullOrEmpty(this.Name) ? "Group" : this.Name;
        return $"{name}({this.ChildCount}) {(ushort)this.Id:x4}";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ClearChildrenArrayInternal()
    {
        Volatile.Write(ref this.childrenArray, null);
    }

    internal void ClearInternal()
    {
        this.childrenList.Clear();
        this.ClearChildrenArrayInternal();
    }

    /// <summary>
    /// Gets the current children without creating a snapshot. Requires <c>Root.SyncObject</c>.
    /// </summary>
    /// <returns>A span over the child list. Do not add or remove children while iterating it.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<ExecutionCore> GetChildrenSpanInternal()
        => CollectionsMarshal.AsSpan(this.childrenList);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ExecutionCore[] GetChildrenArrayInternal()
    {
        var array = this.childrenArray;
        if (array is null)
        {
            array = this.childrenList.ToArray();
            Volatile.Write(ref this.childrenArray, array);
        }

        return array;
    }

    internal void AddChildInternal(ExecutionCore child)
    {
        Debug.Assert(child.Root == this.Root);
        Debug.Assert(child.parent is null);

        this.childrenList.Add(child);
        this.ClearChildrenArrayInternal();
        child.parent = this;
    }

    internal bool RemoveChildInternal(ExecutionCore child)
    {
        if (!this.childrenList.Remove(child))
        {
            return false;
        }

        this.ClearChildrenArrayInternal();
        child.parent = null;
        return true;
    }
}
