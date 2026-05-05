// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Arc.Threading;

public class ExecutionGroup : ExecutionCore
{
    #region FieldAndProperty

    private List<ExecutionCore> childrenList = new(); // Root.SyncObject
    private ExecutionCore[]? childrenArray; // Root.SyncObject

    public int Count => this.childrenList.Count;

    #endregion

    public ExecutionGroup(ExecutionGroup parent, string name, bool isIndependent)
        : base(parent, isIndependent)
    {
        this.Name = name;
    }

    private protected ExecutionGroup()
        : base()
    {
    }

    public void AddChild(ExecutionCore child)
    {
        if (this.Root != child.Root)
        {
            ExecutionHelper.ThrowDifferentParentException();
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

    public override void OnSignalReceived(ExecutionSignal signal)
    {
        var children = this.GetChildren();
        foreach (var x in children)
        {
            x.SendSignal(signal);
        }
    }

    public override void OnRemoved()
    {
        base.OnRemoved();

        this.childrenList.Clear();
        this.ClearChildrenArrayInternal();
    }

    public override string ToString()
    {
        return $"{this.Name}({this.Count}) {(ushort)this.Id:x4}";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ClearChildrenArrayInternal()
    {
        this.childrenArray = default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ExecutionCore[] GetChildrenArrayInternal()
    {
        if (this.childrenArray is null)
        {
            this.childrenArray = this.childrenList.ToArray();
        }

        return this.childrenArray;
    }

    internal void AddChildInternal(ExecutionCore child)
    {
        Debug.Assert(child.Parent is null);

        this.childrenList ??= new();
        this.childrenList.Add(child);
        this.ClearChildrenArrayInternal();
        child.parent = this;
    }

    internal bool RemoveChildInternal(ExecutionCore child)
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
}
