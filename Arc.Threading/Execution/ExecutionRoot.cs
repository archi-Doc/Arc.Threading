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
/// <see cref="BaseGroup"/> and <see cref="IndependentGroup"/> are excluded from default recursive termination.
/// Waiting on the root first cancels and waits for all base services, including independent descendants,
/// then waits for the remaining tree according to the requested options.
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
    /// <returns><see langword="true"/> if termination was observed before timeout/cancellation; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative other than <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    /// <remarks>Only base services are canceled by this method. Including independent descendants waits for <see cref="IndependentGroup"/> without requesting its cancellation.</remarks>
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
    public ExecutionGroup GetOrAddUnitGroup(string unitName)
        => this.IndependentGroup.GetOrAddGroup(true, unitName);

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
