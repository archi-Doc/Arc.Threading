// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1202 // Elements should be ordered by access

namespace Arc.Threading;

/// <summary>
/// Represents the root execution group that owns and coordinates top-level execution groups.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BaseGroup"/> manages executions that provide base services for the application.<br/>
/// Executions are managed independently, and when <see cref="WaitForTermination(TimeSpan, CancellationToken, TerminationOptions)"/> is called, <see cref="ExecutionCore.RequestTermination(Arc.Threading.TerminationOptions)"/> is called on the BaseGroup.
/// </para>
/// <para>
/// <see cref="IndependentGroup"/> is intended for executions that can be managed independently,
/// but are still tracked under the same root lifecycle.
/// </para>
/// </remarks>
public class ExecutionRoot : ExecutionGroup
{
    /// <summary>
    /// Gets the lock that synchronizes mutable state owned by this execution tree.
    /// </summary>
    internal Lock SyncObject { get; } = new();

    // internal readonly Dictionary<long, ExecutionCore> IdToCore = new(); // SyncObject

    /// <summary>
    /// Gets the execution group that provides base services for the application.<br/>
    /// Executions are managed independently, and when <see cref="WaitForTermination(TimeSpan, CancellationToken, TerminationOptions)"/> is called, <see cref="ExecutionCore.RequestTermination(Arc.Threading.TerminationOptions)"/> is called on the BaseGroup.
    /// </summary>
    public ExecutionGroup BaseGroup { get; }

    /// <summary>
    /// Gets the execution group for work that is independent from the base flow.
    /// </summary>
    public ExecutionGroup IndependentGroup { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionRoot"/> class.
    /// </summary>
    /// <remarks>
    /// This constructor creates two child groups:
    /// <list type="bullet">
    /// <item><description><c>Base</c> (<see cref="BaseGroup"/>)</description></item>
    /// <item><description><c>Independent</c> (<see cref="IndependentGroup"/>)</description></item>
    /// </list>
    /// </remarks>
    public ExecutionRoot()
        : base()
    {
        this.BaseGroup = new(this, true, "Base");
        this.IndependentGroup = new(this, true, "Independent");
    }

    public override Task<bool> WaitForTermination(TimeSpan timeout, CancellationToken cancellationToken = default, TerminationOptions options = default)
    {
        if (this.BaseGroup.CanContinue)
        {
            this.BaseGroup.RequestTermination(TerminationOptions.IncludeIndependent);
        }

        return base.WaitForTermination(timeout, cancellationToken, options);
    }

    /*public ExecutionCore? Find(long id)
    {
        using (this.SyncObject.EnterScope())
        {
            this.IdToCore.TryGetValue(id, out var core);
            return core;
        }
    }

    public bool FindCancellationToken(long id, out CancellationToken cancellationToken)
    {
        if (this.Find(id) is { } core)
        {
            cancellationToken = core.CancellationToken;
            return true;
        }
        else
        {
            cancellationToken = default;
            return false;
        }
    }*/
}
