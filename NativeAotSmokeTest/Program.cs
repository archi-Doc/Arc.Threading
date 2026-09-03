// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Runtime.CompilerServices;
using Arc.Threading;

namespace NativeAotSmokeTest;

internal static class Program
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static async Task<int> Main()
    {
        // Also bound synchronous waits and foreground threads if a regression hangs the process.
        using var watchdog = new Timer(_ =>
        {
            Console.Error.WriteLine("FAIL: NativeAOT smoke tests timed out.");
            Environment.Exit(1);
        }, null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);

        try
        {
            Check(!RuntimeFeature.IsDynamicCodeSupported, "Run the published NativeAOT executable.");
            await TestExecutionTree();
            await TestWorkers();
            await TestSynchronization();
            TestNativeInteropAndAllocation();
            Console.WriteLine("PASS: All NativeAOT smoke tests completed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task TestExecutionTree()
    {
        using var root = new ExecutionRoot();
        using var group = new ExecutionGroup(root);
        var token = group.Pack();
        Check(token == group.Token, "Packed token must match CancellationTokenSource.Token.");
        Check(ReferenceEquals(token.Extract<ExecutionGroup>(), group), "Generic token extraction failed.");
        Check(ReferenceEquals(group.CancellationToken.ExtractCore(), group), "Token extraction failed.");
        Check(CancellationToken.None.ExtractCore() is null, "Empty token must not contain a core.");
        using var ordinarySource = new CancellationTokenSource();
        Check(ordinarySource.Token.ExtractCore() is null, "Ordinary token must not contain a core.");

        var cancellationObserved = false;
        using var registration = token.Register(() => cancellationObserved = true);
        var threadRan = false;
        using var thread = new ThreadCore(group, _ => threadRan = true,
            ExecutionCoreOptions.DelayedStart | ExecutionCoreOptions.KeepAliveOnCompletion);
        using var task = new CustomCore(group);
        group.SendSignal(ExecutionSignal.Start);
        await task.Task.WaitAsync(TestTimeout);
        Check(thread.Thread.Join(TestTimeout) && threadRan, "ThreadCore did not execute.");
        Check(task.Ran && task.IsTerminated, "TaskCore<TSelf> did not execute.");

        var stack = new ExecutionStack(root);
        using (var completion = stack.PushNew(group))
        {
            Check(ReferenceEquals(stack.Find(completion.Id), completion), "ExecutionStack lookup failed.");
            completion.TrySetCompleted();
            await completion.CompletionTask.WaitAsync(TestTimeout);
        }

        Check(stack.IsEmpty, "Disposed execution must be removed from its stack.");
        using var completionCore = new TaskCompletionCore(group);
        completionCore.TrySetCompleted();
        await completionCore.CompletionTask.WaitAsync(TestTimeout);

        var delay = group.Delay(Timeout.Infinite);
        group.RequestTermination();
        Check(cancellationObserved && token.IsCancellationRequested, "Packed token did not observe cancellation.");
        Check(!await delay.WaitAsync(TestTimeout), "Termination must cancel the pending delay.");
        Check(await group.WaitForTermination(TestTimeout), "Execution tree did not terminate.");
        Console.WriteLine("PASS: Execution tree, generic cores, token conversion, and cancellation.");
    }

    private static async Task TestWorkers()
    {
        using var root = new ExecutionRoot();
        using var worker = new CustomWorker(root);
        worker.SendSignal(ExecutionSignal.Start);
        var job = worker.Rent();
        for (var i = 0; i < 2; i++)
        {
            job.Value = 21;
            worker.Add(job);
            await job.WaitAsync(TestTimeout);
            Check(job.State == ReusableJobState.Completed && job.Value == 42, "Asynchronous job failed.");
            Check(await worker.WaitForCompletion(TestTimeout), "Worker did not complete.");
            worker.Return(job);
            Check(job.State == ReusableJobState.Pooled, "Job was not returned to the pool.");
            if (i == 0)
            {
                var reused = worker.Rent();
                Check(ReferenceEquals(job, reused), "Job was not reused.");
                job = reused;
            }
        }

        using var threadWorker = new ReusableJobWorker<ThreadJob>(root, (_, item) => item.Value = 42,
            options: ExecutionCoreOptions.DelayedStart);
        var threadJob = threadWorker.Rent();
        threadWorker.Add(threadJob);
        threadWorker.SendSignal(ExecutionSignal.Start);
        Check(threadJob.Wait(TestTimeout) && threadJob.Value == 42, "Synchronous job failed.");
        Check(await threadWorker.WaitForCompletion(TestTimeout), "Thread job worker did not complete.");
        threadWorker.Return(threadJob);

        root.RequestTermination(TerminationOptions.IncludeIndependent);
        Check(await root.WaitForTermination(TestTimeout, TerminationOptions.IncludeIndependent), "Workers did not terminate.");
        Console.WriteLine("PASS: Generic worker dispatch, task/thread jobs, and object pooling.");
    }

    private static async Task TestSynchronization()
    {
        var pulse = new AsyncPulseEvent();
        var wait = pulse.WaitAsync(TestTimeout);
        pulse.Pulse();
        Check(await wait, "Pending pulse wait failed.");
        pulse.Pulse();
        Check(await pulse.WaitAsync(TestTimeout), "Retained pulse wait failed.");
        Check(!await pulse.WaitAsync(TimeSpan.FromMilliseconds(10)), "Pulse timeout failed.");
        using var source = new CancellationTokenSource();
        wait = pulse.WaitAsync(source.Token);
        source.Cancel();
        Check(!await wait.WaitAsync(TestTimeout), "Pulse cancellation failed.");

        var semaphore = new SemaphoreLock();
        Check(semaphore.Enter(), "Synchronous lock acquisition failed.");
        var acquire = semaphore.EnterAsync();
        Check(!acquire.IsCompleted, "Contended lock must wait.");
        semaphore.Exit();
        Check(await acquire.WaitAsync(TestTimeout), "Asynchronous lock acquisition failed.");
        semaphore.Exit();
        using (await semaphore.EnterScopeAsync())
        {
            Check(semaphore.IsLocked, "Async scope must hold the lock.");
        }

        Check(!semaphore.IsLocked, "Async scope must release the lock.");
        var monitor = new MonitorLock();
        using (monitor.EnterScope())
        {
            Check(monitor.IsLocked, "Monitor scope must hold the lock.");
        }

        Check(!monitor.IsLocked, "Monitor scope must release the lock.");
        var id = ExecutionId.Get();
        await Task.Yield();
        Check(id != 0 && ExecutionId.Get() == id, "Ambient execution ID did not flow across await.");
        Check(!await Task.TryDelay(1, source.Token), "TryDelay must observe cancellation.");
        var pooledSource = CancellationTokenPool.Rent();
        Check(!pooledSource.IsCancellationRequested, "Rented token must not be canceled.");
        CancellationTokenPool.TryResetAndReturn(pooledSource);
        Console.WriteLine("PASS: Pulse events, locks, ambient IDs, and cancellation token pooling.");
    }

    private static void TestNativeInteropAndAllocation()
    {
        using var sleep = new MicroSleep();
        if (OperatingSystem.IsWindows())
        {
            Check(sleep.CurrentMode == MicroSleep.Mode.WaitableTimerEx, "Windows waitable timer initialization failed.");
        }
        else
        {
            Check(sleep.CurrentMode == MicroSleep.Mode.Nanosleep, "nanosleep initialization failed.");
        }

        sleep.Sleep(1_000);
        Check(EstimateSize.Struct<long>() == sizeof(long), "Struct size estimation failed.");
        Check(EstimateSize.Class<TaskJob>() > 0, "Generic class allocation failed.");
        Check(EstimateSize.Constructor(() => new ThreadJob()) > 0, "Delegate allocation failed.");
        Console.WriteLine($"PASS: Native interop ({sleep.CurrentMode}) and allocation helpers.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class CustomCore : TaskCore<CustomCore>
    {
        public CustomCore(ExecutionGroup parent)
            : base(parent, async core =>
            {
                await Task.Yield();
                core.Ran = true;
            }, ExecutionCoreOptions.DelayedStart | ExecutionCoreOptions.KeepAliveOnCompletion)
        {
        }

        public bool Ran { get; private set; }
    }

    private sealed record TaskJob : ReusableTaskJob
    {
        public int Value { get; set; }
    }

    private sealed record ThreadJob : ReusableThreadJob
    {
        public int Value { get; set; }
    }

    private sealed class CustomWorker : ReusableJobWorker<TaskJob>
    {
        public CustomWorker(ExecutionGroup parent)
            : base(parent, options: ExecutionCoreOptions.DelayedStart)
        {
        }

        protected override async Task OnJobProcessing(TaskJob job, CancellationToken cancellationToken)
        {
            await Task.Yield();
            job.Value *= 2;
        }
    }
}
