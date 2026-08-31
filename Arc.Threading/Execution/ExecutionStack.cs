// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Collections.Generic;

namespace Arc.Threading;

/// <summary>
/// Represents a collection of <see cref="ExecutionCore"/> instances managed as an execution stack.
/// </summary>
/// <remarks>
/// Access to mutable state is synchronized through <see cref="ExecutionRoot.SyncObject"/>.
/// </remarks>
public class ExecutionStack
{
    #region FieldAndProperty

    private readonly List<ExecutionCore> list = new(); // Root.SyncObject

    /// <summary>
    /// Gets the owning <see cref="ExecutionRoot"/> for this stack.
    /// </summary>
    public ExecutionRoot Root { get; }

    /// <summary>
    /// Gets the number of executions currently contained in the stack.
    /// </summary>
    public int Count => this.list.Count;

    /// <summary>
    /// Gets a value indicating whether the stack contains no executions.
    /// </summary>
    public bool IsEmpty => this.list.Count == 0;

    /// <summary>
    /// Gets the first execution in the stack.
    /// </summary>
    /// <value>
    /// The first <see cref="ExecutionCore"/> when available; otherwise, <see langword="null"/>.
    /// </value>
    public ExecutionCore? FirstCore
    {
        get
        {
            using (this.Root.SyncObject.EnterScope())
            {
                return this.list.Count == 0 ? null : this.list[0];
            }
        }
    }

    /// <summary>
    /// Gets the last execution in the stack.
    /// </summary>
    /// <value>
    /// The last <see cref="ExecutionCore"/> when available; otherwise, <see langword="null"/>.
    /// </value>
    public ExecutionCore? LastCore
    {
        get
        {
            using (this.Root.SyncObject.EnterScope())
            {
                return this.list.Count == 0 ? null : this.list[^1];
            }
        }
    }

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionStack"/> class.
    /// </summary>
    /// <param name="root">The owning execution root.</param>
    public ExecutionStack(ExecutionRoot root)
    {
        this.Root = root;
    }

    /// <summary>
    /// Creates and pushes a new <see cref="ExecutionCore"/> onto the stack.
    /// </summary>
    /// <param name="parent">
    /// The parent execution group.
    /// <br/>
    /// When the parent is deleted, this execution is automatically canceled and deleted as well.
    /// </param>
    /// <param name="processSignalHandler">
    /// An optional handler invoked when this execution processes an <see cref="ExecutionSignal"/>.
    /// </param>
    /// <returns>The newly created execution.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="parent"/> belongs to a different <see cref="ExecutionRoot"/>.
    /// </exception>
    public TaskCompletionGroup PushNew(ExecutionGroup parent, ExecutionSignalHandler? processSignalHandler = default)
    {
        if (this.Root != parent.Root)
        {
            ExecutionHelper.ThrowDifferentRootException();
        }

        return new TaskCompletionGroup(parent, this, false, processSignalHandler);
    }

    /// <summary>
    /// Pushes an existing execution onto this stack.
    /// </summary>
    /// <param name="core">The execution to push.</param>
    /// <returns>
    /// <see langword="true"/> when the execution is now associated with this stack;
    /// otherwise, <see langword="false"/> when it is already associated with another stack.
    /// </returns>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="core"/> belongs to a different <see cref="ExecutionRoot"/>.
    /// </exception>
    public bool Push(ExecutionCore core)
    {
        using (this.Root.SyncObject.EnterScope())
        {
            if (this.Root != core.Root)
            {
                ExecutionHelper.ThrowDifferentRootException();
            }

            if (core.Stack is not null)
            {
                return core.Stack == this;
            }

            this.AddInternal(core);
        }

        return true;
    }

    /// <summary>
    /// Finds the first execution with the specified identifier.
    /// </summary>
    /// <param name="id">The execution identifier.</param>
    /// <returns>The matching execution; otherwise, <see langword="null"/>.</returns>
    public ExecutionCore? Find(int id)
    {
        using (this.Root.SyncObject.EnterScope())
        {
            foreach (var x in this.list)
            {
                if (x.Id == id)
                {
                    return x;
                }
            }
        }

        return null;
    }

    internal void AddInternal(ExecutionCore core)
    {
        core.Stack = this;
        this.list.Add(core);
    }

    internal void RemoveInternal(ExecutionCore core)
    {
        core.Stack = null;
        this.list.Remove(core);
    }
}
