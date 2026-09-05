// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Provides cancellation-aware delays and conversions between <see cref="CancellationToken"/> and <see cref="ExecutionCore"/>.
/// </summary>
public static class ExecutionHelper
{
    extension(Task)
    {
        /// <summary>
        /// Asynchronously waits for the specified duration.
        /// </summary>
        /// <param name="delay">The duration to wait.</param>
        /// <param name="cancellationToken">The cancellation token used to cancel the delay.</param>
        /// <returns>
        /// A task that returns <see langword="true"/> if the delay elapsed successfully;
        /// otherwise, <see langword="false"/> if the delay was canceled.
        /// </returns>
        public static async Task<bool> TryDelay(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// Asynchronously waits for the specified number of milliseconds.
        /// </summary>
        /// <param name="millisecondsDelay">
        /// The number of milliseconds to wait, or <see cref="Timeout.Infinite"/> to wait indefinitely.
        /// </param>
        /// <param name="cancellationToken">The cancellation token used to cancel the delay.</param>
        /// <returns>
        /// A task that returns <see langword="true"/> if the delay elapsed successfully;
        /// otherwise, <see langword="false"/> if the delay was canceled.
        /// </returns>
        public static async Task<bool> TryDelay(int millisecondsDelay, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(millisecondsDelay, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

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
        // CancellationToken is a struct that holds a single CancellationTokenSource reference (null for CancellationToken.None).
        var cts = Unsafe.As<CancellationToken, CancellationTokenSource?>(ref cancellationToken);
        return cts as TExecution;
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

    /*public static Task<bool> Delay(this CancellationToken cancellationToken, TimeSpan delay)
    {
        if (cancellationToken.ExtractCore() is { } core)
        {
            return core.Delay(delay);
        }
        else
        {
            return DelayTask(delay, cancellationToken);
        }

        static async Task<bool> DelayTask(TimeSpan ts, CancellationToken ct)
        {
            try
            {
                await Task.Delay(ts, ct).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static Task<bool> Delay(this CancellationToken cancellationToken, int millisecondsToWait)
    {
        if (cancellationToken.ExtractCore() is { } core)
        {
            return core.Delay(millisecondsToWait);
        }
        else
        {
            return DelayTask(millisecondsToWait, cancellationToken);
        }

        static async Task<bool> DelayTask(int ms, CancellationToken ct)
        {
            try
            {
                await Task.Delay(ms, ct).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }*/

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
