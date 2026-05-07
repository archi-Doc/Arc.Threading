// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Provides low-level conversion helpers between <see cref="CancellationToken"/> and <see cref="ExecutionCore"/>.
/// </summary>
public static class ExecutionHelper
{
    /// <summary>
    /// Attempts to extract an execution instance from a <see cref="CancellationToken"/>.
    /// </summary>
    /// <typeparam name="TExecution">
    /// The execution type to extract. Must derive from <see cref="ExecutionCore"/>.
    /// </typeparam>
    /// <param name="cancellationToken">The token to inspect.</param>
    /// <returns>
    /// An instance of <typeparamref name="TExecution"/> when the underlying source is compatible; otherwise, <see langword="null"/>.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TExecution? Extract<TExecution>(this CancellationToken cancellationToken)
        where TExecution : ExecutionCore
    {// In my opinion, CancellationToken should have been named something like TaskContext, with added features for managing parent-child dependencies and for canceling or terminating processing.
        try
        {
            var cts = Unsafe.As<CancellationToken, CancellationTokenSource>(ref cancellationToken);
            return cts as TExecution;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to extract an <see cref="ExecutionCore"/> from a <see cref="CancellationToken"/>.
    /// </summary>
    /// <param name="cancellationToken">The token to inspect.</param>
    /// <returns>
    /// The extracted <see cref="ExecutionCore"/> when available; otherwise, <see langword="null"/>.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ExecutionCore? ExtractCore(this CancellationToken cancellationToken)
    {
        return Extract<ExecutionCore>(cancellationToken);
    }

    /// <summary>
    /// Packs an <see cref="ExecutionCore"/> instance into a <see cref="CancellationToken"/>.
    /// </summary>
    /// <param name="executionCore">The execution instance to pack.</param>
    /// <returns>A token that carries the specified execution instance.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CancellationToken Pack(this ExecutionCore executionCore)
    {
        return Unsafe.As<ExecutionCore, CancellationToken>(ref executionCore);
    }

    [DoesNotReturn]
    internal static void ThrowDifferentRootException()
        => throw new InvalidOperationException("The stack and parent objects must be created from the same Root.");

    [DoesNotReturn]
    internal static void ThrowDifferentParentException()
        => throw new InvalidOperationException("The parent and child objects must be created from the same Root.");
}
