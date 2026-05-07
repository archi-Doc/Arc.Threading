// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Represents an <see cref="ExecutionCore"/> node that owns and coordinates a mutable collection
/// of child executions.
/// </summary>
/// <remarks>
/// <para>
/// Child membership is stored in a list and exposed through a cached array snapshot for efficient iteration.
/// The cache is invalidated whenever children are added or removed.
/// </para>
/// <para>
/// Synchronization is performed via <c>Root.SyncObject</c> when list access requires consistency.
/// </para>
/// </remarks>
public class ExecutionGroup : ExecutionCore
{
    #region FieldAndProperty

    private readonly List<ExecutionCore> childrenList = new(); // Root.SyncObject
    private ExecutionCore[]? childrenArray; // Root.SyncObject

    /// <summary>
    /// Gets the current number of registered child executions.
    /// </summary>
    public int Count => this.childrenList.Count;

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionGroup"/> class under the specified parent group.
    /// </summary>
    /// <param name="parent">The parent group that owns this group.</param>
    /// <param name="isIndependent">
    /// A value indicating whether this group is independent from parent cancellation/signal behavior.
    /// </param>
    /// <param name="name">An optional display name. If <see langword="null"/>, an empty name is used.</param>
    public ExecutionGroup(ExecutionGroup parent, bool isIndependent = false, string? name = default)
        : base(parent, isIndependent)
    {
        this.Name = name ?? string.Empty;
    }

    private protected ExecutionGroup()
        : base()
    {
    }

    /// <summary>
    /// Adds a child execution to this group by assigning its <c>Parent</c> to the current instance.
    /// </summary>
    /// <param name="child">The child execution to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is <see langword="null"/>.</exception>
    public void AddChild(ExecutionCore child)
    {
        if (child is null)
        {
            throw new ArgumentNullException(nameof(child));
        }

        child.Parent = this;
    }

    /// <summary>
    /// Gets an existing child <see cref="ExecutionGroup"/> with the specified <paramref name="name"/>,<br/>
    /// or creates and returns a new one when no match exists.
    /// </summary>
    /// <param name="isIndependent">
    /// A value indicating whether a newly created group should be independent from parent signal/cancellation behavior.<br/>
    /// This value is ignored when a matching group already exists.
    /// </param>
    /// <param name="name">The group name to search for.</param>
    /// <returns>
    /// An existing child group whose name matches <paramref name="name"/> using
    /// <see cref="StringComparison.InvariantCulture"/>, or a newly created child group.
    /// </returns>
    public ExecutionGroup GetOrAddGroup(bool isIndependent, string name)
    {
        using (this.Root.SyncObject.EnterScope())
        {
            foreach (var x in this.childrenList)
            {
                if (x is ExecutionGroup group &&
                    string.Equals(x.Name, name, StringComparison.Ordinal))
                {
                    return group;
                }
            }

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
            return this.childrenList.Find(x => x.Id == id);
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
    /// Propagates an execution signal to all current children.
    /// </summary>
    /// <param name="signal">The signal to forward.</param>
    public override void OnSignalReceived(ExecutionSignal signal)
    {
        var children = this.GetChildren();
        foreach (var x in children)
        {
            x.SendSignal(signal);
        }
    }

    public override string ToString()
    {
        var name = string.IsNullOrEmpty(this.Name) ? "Group" : this.Name;
        return $"{name}({this.Count}) {(ushort)this.Id:x4}";
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
