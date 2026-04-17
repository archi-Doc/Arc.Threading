// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Provides a thread-safe, stack-like collection of execution <see cref="Scope"/> objects.
/// </summary>
public class ExecutionStack
{
    /// <summary>
    /// Represents a removable scope entry within an <see cref="ExecutionStack"/>.
    /// </summary>
    /// <remarks>
    /// Disposing a <see cref="Scope"/> removes it from its owning <see cref="ExecutionStack"/>; it does not
    /// automatically cancel the scope.
    /// </remarks>
    public class Scope : IDisposable
    {
        /// <summary>
        /// Gets the owning <see cref="Arc.Threading.ExecutionStack"/> instance.
        /// </summary>
        public ExecutionStack ExecutionStack { get; }

        /// <summary>
        /// Gets the identifier of this scope within the owning <see cref="ExecutionStack"/>.
        /// </summary>
        public int Id { get; }

        /// <summary>
        /// Gets the <see cref="System.Threading.CancellationTokenSource"/> associated with this scope.
        /// </summary>
        public CancellationTokenSource CancellationTokenSource { get; }

        /// <summary>
        /// Gets the <see cref="System.Threading.CancellationToken"/> associated with this scope.
        /// </summary>
        public CancellationToken CancellationToken { get; }

        /// <summary>
        /// Gets a value indicating whether this scope is the root scope (<c>Id == 0</c>).
        /// </summary>
        public bool IsRoot => this.Id == 0;

        /// <summary>
        /// Initializes a new instance of the <see cref="Scope"/> class.
        /// </summary>
        /// <param name="executionStack">The owning <see            cref="Arc.Threading.ExecutionStack"/>.</param>
        /// <param name="id">The scope identifier to assign.</param>
        public Scope(ExecutionStack executionStack, int id)
        {
            this.ExecutionStack = executionStack;
            this.Id = id;
            this.CancellationTokenSource = new();
            this.CancellationToken = this.CancellationTokenSource.Token;
        }

        /// <summary>
        /// Removes this scope from its owning <see cref="ExecutionStack"/>.
        /// </summary>
        public void Dispose()
        {
            this.ExecutionStack.Remove(this);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            var isCanceled = this.CancellationTokenSource.IsCancellationRequested ? " Canceled" : string.Empty;
            return $"Execution Scope {this.Id}{isCanceled}";
        }
    }

    /// <summary>
    /// Gets the root scope (created at construction time).
    /// </summary>
    /// <remarks>
    /// The root scope uses <c>Id = 0</c> and is not canceled by <see cref="CancelTop"/>.
    /// </remarks>
    public Scope Root { get; }

    private readonly Lock syncObject = new();
    private readonly List<Scope> list = new();
    private int incrementalId;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionStack"/> class and creates <see cref="Root"/>.
    /// </summary>
    public ExecutionStack()
    {
        this.Root = new(this, this.incrementalId++);
        this.list.Add(this.Root);
    }

    /// <summary>
    /// Creates and pushes a new <see cref="Scope"/> onto the stack.
    /// </summary>
    /// <returns>The newly created scope.</returns>
    public Scope Add()
    {
        Scope newScope;
        using (this.syncObject.EnterScope())
        {
            newScope = new Scope(this, this.incrementalId++);
            this.list.Add(newScope);
        }

        return newScope;
    }

    /// <summary>
    /// Finds the first scope with the specified identifier.
    /// </summary>
    /// <param name="id">The scope identifier.</param>
    /// <returns>The matching scope; otherwise, <see langword="null"/>.</returns>
    public Scope? Find(int id)
    {
        using (this.syncObject.EnterScope())
        {
            return this.list.Find(x => x.Id == id);
        }
    }

    /// <summary>
    /// Gets the current top scope without removing it.
    /// </summary>
    /// <returns>The top scope; or <see langword="null"/> if the stack is empty.</returns>
    public Scope? Peek()
    {
        using (this.syncObject.EnterScope())
        {
            if (this.list.Count == 0)
            {
                return null;
            }
            else
            {
                return this.list[^1];
            }
        }
    }

    /// <summary>
    /// Cancels the current top scope, if it exists and is not the <see cref="Root"/> scope.
    /// </summary>
    public void CancelTop()
    {
        var scope = this.Peek();
        if (scope is not null &&
            !scope.IsRoot)
        {
            scope.CancellationTokenSource.Cancel();
        }
    }

    /// <summary>
    /// Removes the specified scope from the stack.
    /// </summary>
    /// <param name="item">The scope instance to remove.</param>
    /// <returns><see langword="true"/> if the scope was removed; otherwise, <see langword="false"/>.</returns>
    public bool Remove(Scope item)
    {
        using (this.syncObject.EnterScope())
        {
            return this.list.Remove(item);
        }
    }

    /// <summary>
    /// Removes the first scope with the specified identifier from the stack.
    /// </summary>
    /// <param name="id">The scope identifier.</param>
    /// <returns><see langword="true"/> if a scope was removed; otherwise, <see langword="false"/>.</returns>
    public bool Remove(int id)
    {
        using (this.syncObject.EnterScope())
        {
            var index = this.list.FindIndex(x => x.Id == id);
            if (index >= 0)
            {// Found
                this.list.RemoveAt(index);
                return true;
            }
            else
            {// Not found
                return false;
            }
        }
    }
}
