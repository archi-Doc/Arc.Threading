// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

#pragma warning disable SA1202 // Elements should be ordered by access

/*
/// <summary>
/// This class executes a method at fixed intervals.<br/>
/// It runs the method at the specified interval(intervalInMillisecond), but calling Pulse() allows the method to start immediately.<br/>
/// Pulse() is lightweight, so even if it is called many times concurrently, the process will only run once.<br/>
/// You set the actual processing function in the constructor, and call Start() to launch the task.
/// </summary>
public class PulseTask : TaskCore
{
    public delegate Task PulseTaskProcessDelegate(PulseTask pulseTask);

    private readonly PulseTaskProcessDelegate process;
    private readonly TimeSpan intervalTimeSpan;
    private TaskCompletionSource? currentTcs;

    public PulseTask(ThreadCoreBase? parent, PulseTaskProcessDelegate process, int intervalInMillisecond)
        : base(parent, MainLoop, false)
    {
        this.process = process;
        this.intervalTimeSpan = TimeSpan.FromMilliseconds(intervalInMillisecond);
    }

    private static async Task MainLoop(object? parameter)
    {
        var core = (PulseTask)parameter!;
        while (!core.IsTerminated)
        {
            // Prepare TaskCompletionSource
            TaskCompletionSource? current;
            do
            {
                current = core.currentTcs;
                if (current is not null)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref core.currentTcs, new(TaskCreationOptions.RunContinuationsAsynchronously), null) != null);

            try
            {
                await current!.Task.WaitAsync(core.intervalTimeSpan, core.CancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {// Timeout
            }
            catch (OperationCanceledException)
            {// TaskCore is canceled.
                return;
            }
            catch
            {
            }

            // Process
            await core.process(core).ConfigureAwait(false);
        }
    }

    public void Pulse()
    {
        // Uses Interlocked.CompareExchange to set currentTcs to null only if it is not already null.
        TaskCompletionSource? current;
        do
        {
            current = this.currentTcs;
            if (current is null)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref this.currentTcs, null, current) != current);

        current.SetResult();
    }
}*/
