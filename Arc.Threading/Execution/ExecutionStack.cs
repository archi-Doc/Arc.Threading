// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Arc.Threading;

public class ExecutionStack
{
    #region FieldAndProperty

    private readonly List<ExecutionCore> list = new(); // Root.SyncObject

    public ExecutionRoot Root { get; }

    public int Count => this.list.Count;

    public bool IsEmpty => this.list.Count == 0;

    public ExecutionCore? TopCore
    {
        get
        {
            using (this.Root.SyncObject.EnterScope())
            {
                return this.list.Count == 0 ? null : this.list[0];
            }
        }
    }

    public ExecutionCore? BottomCore
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

    public ExecutionStack(ExecutionRoot root)
    {
        this.Root = root;
    }

    /// <summary>
    /// Creates and pushes a new <see cref="ExecutionCore"/> onto the stack.
    /// </summary>
    /// <param name="parent">Specify the parent execution.<br/>
    /// When the parent is deleted, this execution is automatically canceled and deleted as well.</param>
    /// <param name="processSignalHandler">An optional handler invoked when this execution processes an <see cref="ExecutionSignal"/>.</param>
    /// <returns>The newly created execution.</returns>
    public ExecutionCore PushNew(ExecutionGroup parent, ExecutionSignalHandler? processSignalHandler = default)
    {
        if (this.Root != parent.Root)
        {
            ExecutionHelper.ThrowDifferentRootException();
        }

        var core = new ExecutionCore(parent, this, processSignalHandler);
        return core;
    }

    public bool Push(ExecutionCore core)
    {
        if (this.Root != core.Root)
        {
            ExecutionHelper.ThrowDifferentRootException();
        }

        using (this.Root.SyncObject.EnterScope())
        {
            if (core.Stack is not null)
            {
                return core.Stack == this;
            }

            this.AddInternal(core);
        }

        return true;
    }

    /*public ExecutionCore? TryPush(ExecutionCore parent, long id, ExecutionSignalHandler? processSignalHandler = default)
    {
        if (this.Root != parent.Root)
        {
            ExecutionHelper.ThrowDifferentRootException();
        }

        var core = ExecutionCore.TryCreate(parent, id, this, processSignalHandler);
        return core;
    }*/

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
