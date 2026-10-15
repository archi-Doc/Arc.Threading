// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1202 // Elements should be ordered by access

namespace Arc.Threading;

/// <summary>
/// Represents the root execution group that owns and coordinates top-level execution groups.
/// </summary>
/// <remarks>
/// <see cref="BaseGroup"/> and <see cref="IndependentGroup"/> are excluded from default recursive termination,
/// so <see cref="ExecutionCore.RequestTermination(TerminationOptions)"/> on the root with default options cancels only the root and the non-independent work attached to it outside these groups.
/// Waiting on the root first cancels and waits for all base services, including independent descendants,
/// then waits for the remaining tree according to the requested options.
/// Disposing the root terminates the whole tree without waiting.
/// </remarks>
public class ExecutionRoot : ExecutionGroup
{
    /// <summary>
    /// Gets the lock that synchronizes mutable state owned by this execution tree.
    /// </summary>
    internal Lock SyncObject { get; } = new();

    // internal readonly Dictionary<long, ExecutionCore> IdToCore = new(); // SyncObject

    /// <summary>
    /// Gets the base services group, whose entire subtree is canceled and awaited when waiting on this root.
    /// </summary>
    public ExecutionGroup BaseGroup { get; }

    /// <summary>
    /// Gets the group excluded from root termination and waiting unless independent descendants are explicitly included.
    /// </summary>
    /// <remarks>Use <see cref="GetOrAddUnitGroup(string)"/> to get or create named groups under it.</remarks>
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

    /// <summary>
    /// Requests the termination of <see cref="BaseGroup"/>, and asynchronously waits for the termination of the execution tree.
    /// </summary>
    /// <param name="timeout">The total time to wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
    /// <param name="options">Options controlling which descendants to wait for after base services terminate.</param>
    /// <param name="cancellationToken">An additional cancellation token to cancel the wait operation.</param>
    /// <returns><see langword="true"/> if termination was observed; <see langword="false"/> if the timeout elapsed or <paramref name="cancellationToken"/> was canceled first.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative other than <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    /// <remarks>
    /// Only base services are canceled by this method. Work attached directly to the root is waited for but not canceled;
    /// call <see cref="ExecutionCore.RequestTermination(TerminationOptions)"/> first.
    /// Including independent descendants waits for <see cref="IndependentGroup"/> without requesting its cancellation.<br/>
    /// The other <c>WaitForTerminationAsync</c> overloads also use this behavior.
    /// </remarks>
    public override async Task<bool> WaitForTerminationAsync(TimeSpan timeout, TerminationOptions options = default, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var start = Stopwatch.GetTimestamp();

        // Always request: BaseGroup may already be canceled by a request that skipped its independent descendants.
        this.BaseGroup.RequestTermination(TerminationOptions.IncludeIndependent);

        if (!await this.BaseGroup.WaitForTerminationAsync(timeout, TerminationOptions.IncludeIndependent, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (timeout != Timeout.InfiniteTimeSpan)
        {
            timeout -= Stopwatch.GetElapsedTime(start);
            if (timeout < TimeSpan.Zero)
            {
                timeout = TimeSpan.Zero;
            }
        }

        return await base.WaitForTerminationAsync(timeout, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets or creates an independent execution group with the specified unit name under the <see cref="IndependentGroup"/>.
    /// </summary>
    /// <param name="unitName">The name of the unit group to retrieve or create.</param>
    /// <returns>
    /// An existing independent child group whose name matches <paramref name="unitName"/> using
    /// <see cref="StringComparison.Ordinal"/>, or a newly created independent child group.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="unitName"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A non-independent group with the same name exists under <see cref="IndependentGroup"/>.</exception>
    /// <exception cref="ObjectDisposedException"><see cref="IndependentGroup"/> has been disposed.</exception>
    /// <remarks>The returned group may already be terminated. Dispose it to allow a new unit group with the same name.</remarks>
    public ExecutionGroup GetOrAddUnitGroup(string unitName)
        => this.IndependentGroup.GetOrAddGroup(true, unitName);

    /// <summary>
    /// Releases the resources used by this root.<br/>
    /// Termination is requested for the whole tree, including <see cref="BaseGroup"/>, <see cref="IndependentGroup"/>, and their independent descendants.
    /// Running threads and tasks are not joined; use <see cref="WaitForTerminationAsync(TimeSpan, TerminationOptions, CancellationToken)"/> first for a graceful shutdown.
    /// </summary>
    /// <param name="disposing"><see langword="true"/> to release both managed and unmanaged resources; <see langword="false"/> to release only unmanaged resources.</param>
    /// <remarks>
    /// <see cref="BaseGroup"/> and <see cref="IndependentGroup"/> are canceled and detached but not disposed, and keep their subtrees.
    /// Executions created under them afterwards are terminated immediately.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !this.IsDisposed)
        {// BaseGroup and IndependentGroup are independent, so default termination would only detach them and leave their work running.
            this.RequestTermination(TerminationOptions.IncludeIndependent);
        }

        base.Dispose(disposing);
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
