## Arc.Threading
![Nuget](https://img.shields.io/nuget/v/Arc.Threading) ![Build and Test](https://github.com/archi-Doc/Arc.Threading/workflows/Build%20and%20Test/badge.svg)

**Arc.Threading** is a support library for Task/Thread.

- [Quick Start](#quick-start)
- [NativeAOT](#nativeaot)
- [Execution tree](#execution-tree)
  - [ExecutionRoot](#executionroot)
  - [ThreadCore and TaskCore](#threadcore-and-taskcore)
  - [Termination](#termination)
  - [Signals and delayed start](#signals-and-delayed-start)
  - [ExecutionStack](#executionstack)
- [ReusableJobWorker](#reusablejobworker)
- [AsyncPulseEvent](#asyncpulseevent)
- [Locks](#locks)
- [Other utilities](#other-utilities)
- [Performance and ownership](#performance-and-ownership)
- [Build, test, and coverage](#build-test-and-coverage)



## Quick Start

First, install Arc.Threading using Package Manager Console.

```
Install-Package Arc.Threading
```

Arc.Threading targets .NET 10 and above.



## NativeAOT

Arc.Threading is marked with `IsAotCompatible=true`. NativeAOT publishing and execution have been verified locally on Windows x64 with .NET SDK 10.0.400 and Arc.Collections 1.45.0, with no build, trimming, or AOT warnings.

`NativeAotSmokeTest` publishes a native executable and checks execution trees, generic task cores and job workers, job reuse, cancellation token conversion, pulse events, locks, ambient execution IDs, allocation helpers, and `MicroSleep` native interop. It fails if dynamic code is supported, ensuring that the published native executable is used for validation.

The test project uses `TrimmerRootAssembly` to analyze the entire library and the dependency code it uses, including APIs not called by the smoke tests, following the [library trimming guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/prepare-libraries-for-trimming). Compiler, trimming, and AOT warnings are treated as errors. The Build and Test workflow includes native publishing and execution jobs for Windows x64 and Linux x64; Linux execution has not been verified locally.

To repeat the Windows check from the repository root:

```powershell
dotnet publish NativeAotSmokeTest/NativeAotSmokeTest.csproj -c Release -r win-x64 -o artifacts/nativeaot/win-x64
./artifacts/nativeaot/win-x64/NativeAotSmokeTest.exe
```

On Linux:

```bash
dotnet publish NativeAotSmokeTest/NativeAotSmokeTest.csproj -c Release -r linux-x64 -o artifacts/nativeaot/linux-x64
./artifacts/nativeaot/linux-x64/NativeAotSmokeTest
```

Install the [NativeAOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#prerequisites) for the host platform before publishing.



## Execution tree

Arc.Threading manages threads and tasks as a tree of `ExecutionCore` objects.

| Class | Description |
| ---- | ---- |
| `ExecutionCore` | A cancellable execution unit derived from `CancellationTokenSource`. Pass its `CancellationToken` or `Token` property to cancellable APIs. |
| `ExecutionGroup` | An `ExecutionCore` which owns child executions. It is not associated with a Thread/Task. |
| `ExecutionRoot` | The root of an execution tree. |
| `ThreadCore` | An `ExecutionCore` backed by a dedicated `Thread`. |
| `TaskCore` | An `ExecutionCore` backed by a long-running `Task`. |
| `TaskCore<TSelf>` | A `TaskCore` which passes the derived instance to the execution method. |
| `TaskCompletionCore` / `TaskCompletionGroup` | An execution which also exposes a `CompletionTask`. |

The main purpose of the execution tree is:

1. Manage Thread/Task in a tree structure.
2. Terminate Thread/Task from outside the Thread/Task.
3. Unify the format of the method by passing the execution object as a parameter.

### ExecutionRoot

Create one `ExecutionRoot` instance for the application, and use it as the parent of all executions.

```csharp
public static ExecutionRoot Root { get; } = new();
```

`ExecutionRoot` provides two predefined groups.

| Property | Description |
| ---- | ---- |
| `BaseGroup` | Executions which provide base services for the application. `WaitForTermination()` requests the termination of this group first. |
| `IndependentGroup` | Executions which are managed independently. `Root.UnitGroup(name)` creates a named group under it. |

Executions marked as `IsIndependent` are excluded from the default termination/wait target.
Specify `TerminationOptions.IncludeIndependent` to include them.
`Root.WaitForTermination()` always requests and waits for termination of `BaseGroup`, including its independent descendants. It excludes `IndependentGroup` unless `IncludeIndependent` is specified.

Use `GetOrAddGroup(isIndependent, name)` to reuse a named child group, `FindChild(id)` or `TryGetChildCancellationToken(id, out token)` for direct-child lookup, and `Parent`/`AddChild()` to move executions within a root. Cycles and cross-root moves are rejected. Moving under a terminated parent immediately requests termination.

`GetChildren()` returns a cached snapshot: treat the returned array as read-only. Signals are forwarded to independent children as well.

### ThreadCore and TaskCore

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;

internal class Program
{
    public static ExecutionRoot Root { get; } = new();

    public static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (s, e) =>
        {// Ctrl+C pressed.
            e.Cancel = true;
            Root.RequestTermination(); // Send a termination request to the root.
        };

        // ThreadCore: runs on a dedicated thread. The execution starts immediately.
        var c1 = new ThreadCore(Root, core =>
        {
            Console.WriteLine("ThreadCore: Start");
            for (var n = 0; n < 50; n++)
            {
                Thread.Sleep(100);
                if (!core.CanContinue)
                {// Termination requested.
                    Console.WriteLine("ThreadCore: Canceled");
                    return;
                }
            }

            Console.WriteLine("ThreadCore: End");
        });

        // ExecutionGroup is a collection of executions, and it's not associated with Thread/Task.
        var group = new ExecutionGroup(Root);
        var c2 = new TaskCore(group, async core =>
        {// TaskCore: runs on a long-running task.
            Console.WriteLine("TaskCore: Start");

            // core.Delay() returns false if the execution is terminated during the delay.
            if (await core.Delay(3_000))
            {
                Console.WriteLine("TaskCore: End");
            }
            else
            {
                Console.WriteLine("TaskCore: Canceled");
            }
        });

        await Task.Delay(1_500);
        c2.RequestTermination(); // Terminate the TaskCore (and its children).

        // Request the termination of Root.BaseGroup, and wait until all the executions are terminated.
        await Root.WaitForTermination();
    }
}
```

Since `ExecutionCore` derives from `CancellationTokenSource`, `core.CancellationToken` can be passed to any cancellable API, and `ExtractCore()` restores the execution from a `CancellationToken`.

```csharp
await Task.Delay(1_000, core.CancellationToken); // Throws OperationCanceledException when terminated.
var core2 = cancellationToken.ExtractCore(); // Gets the ExecutionCore (null if the token is not associated with an execution).
```

To add a custom property or method, derive from `TaskCore<TSelf>` (or `ThreadCore`).
Use `DelayedStart` when a derived execution method needs fields initialized by its constructor; send the start signal after construction.

```csharp
internal class CustomCore : TaskCore<CustomCore>
{
    public CustomCore(ExecutionGroup parent)
        : base(parent, Process)
    {
    }

    public int CustomPropertyIfYouNeed { get; set; }

    private static async Task Process(CustomCore core)
    {// The derived instance is passed as a parameter.
        while (await core.Delay(1_000))
        {
        }
    }
}
```

### Termination

| Member | Description |
| ---- | ---- |
| `RequestTermination(options)` | Requests the termination of this execution and its children (cancels the `CancellationToken`). |
| `CanContinue` | `false` if the termination is requested. Check this property in the execution loop. |
| `IsTerminated` | For thread/task cores, `true` after exit or cancellation before startup. For plain cores and groups, reflects cancellation. |
| `WaitForTermination(timeout, options, ct)` | Waits until all the target executions are terminated. |
| `Delay(milliseconds, ct)` | `Task.Delay()` which returns `false` instead of throwing when the execution is terminated. |
| `Dispose()` | Requests the termination, and removes this execution from the tree. |

By default, an execution disposes itself when the execution method exits.
Specify `ExecutionCoreOptions.KeepAliveOnCompletion` to keep the object alive.
Termination is cooperative: running code must observe `CanContinue` or its cancellation token. `Dispose()` does not wait for running work to exit. Request termination and await `WaitForTermination()` before releasing resources used by that work. Directly calling the inherited `Cancel()` does not traverse the tree.

`TaskCompletionCore.CompletionTask` and `TaskCompletionGroup.CompletionTask` complete only when `TrySetCompleted()` is called. Completion does not request termination, and termination/disposal does not complete these tasks.

### Signals and delayed start

`ExecutionCoreOptions.DelayedStart` delays the start of a thread/task until an `ExecutionSignal.Start` signal is received.
A signal sent to an `ExecutionGroup` is forwarded to all its children.

```csharp
var core = new TaskCore(Root, Process, ExecutionCoreOptions.DelayedStart);
Root.SendSignal(ExecutionSignal.Start); // Starts all the delayed executions in the tree.
```

Override `OnSignalReceived()`, or pass an `ExecutionSignalHandler` to the constructor, in order to handle application-defined signals.

### ExecutionStack

`ExecutionStack` is a collection of executions which is independent from the parent-child tree (e.g. a stack of screens or nested operations).

```csharp
var stack = new ExecutionStack(Root);
var core = stack.PushNew(Root.BaseGroup); // Creates a TaskCompletionGroup associated with the stack.
var last = stack.LastCore; // The last execution added to the stack.
core.TrySetCompleted(); // Completes core.CompletionTask.
```

`Push(core)` associates an existing execution with one stack. `FirstCore`, `LastCore`, `Count`, `IsEmpty`, and `Find(id)` inspect the stack. Disposal removes an execution from its stack.



## ReusableJobWorker

`ReusableJobWorker<TJob>` is a `TaskCore` which receives and processes `TJob` objects.
Job objects are pooled, so a large number of jobs can be processed with few allocations.

| Job class | Wait method |
| ---- | ---- |
| `ReusableTaskJob` | `WaitAsync()` (`TaskCompletionSource`-based, recommended) |
| `ReusableThreadJob` | `Wait()` (`ManualResetEventSlim`-based) |
| `ReusableJob` | None (the completion cannot be awaited) |

```csharp
private static async Task TestWorker(ExecutionGroup parent)
{
    // Create a worker by specifying the type of job and the delegate.
    var worker = new ReusableJobWorker<TestJob>(parent, (worker, job) =>
    {
        Console.WriteLine($"Process: {job.Id}");
    });

    worker.MaxConcurrentTasks = 4; // Process jobs concurrently (1 by default).

    var job = worker.Rent(); // Rent a job object from the pool.
    job.Id = 1; // Set the parameters of the job.
    worker.Add(job); // Enqueue the job.
    await job.WaitAsync(); // Wait until the job is complete.
    // Check job.State for Completed or Aborted before returning it.
    worker.Return(job); // Return the job object to the pool.

    // ReusableJobFlags.ReturnToPoolOnCompletion returns the job object automatically (fire-and-forget).
    worker.Add(worker.Rent(ReusableJobFlags.ReturnToPoolOnCompletion));

    await worker.WaitForCompletion(); // Wait until all the jobs are processed.
    worker.Dispose(); // Terminate the worker (the pending jobs are aborted).
}

public record class TestJob : ReusableTaskJob
{
    public int Id { get; set; }
}
```

Instead of a delegate, `OnJobProcessing()` can be overridden. This is recommended, since it supports asynchronous processing.

```csharp
public class TestWorker : ReusableJobWorker<TestJob>
{
    public TestWorker(ExecutionGroup parent)
        : base(parent)
    {
    }

    protected override async Task OnJobProcessing(TestJob job, CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);
        Console.WriteLine($"Process: {job.Id}");
    }
}
```

The state of a job changes as follows: `Initial` -> `Pending` (`Add()`) -> `Running` -> `Completed`/`Aborted` -> `Pooled` (`Return()`).

`MaxConcurrentTasks` must be at least 1. Set it before submitting work for a fixed limit; lowering it does not interrupt active jobs. Processing exceptions mark jobs as `Aborted`. `OnJobFinished(job)` runs before waiters are released; exceptions from this hook also mark the job as `Aborted` and do not strand waiters.

`WaitForCompletion()` observes an empty queue and no active processing; `true` does not mean every job succeeded. `WaitAsync()` signals either completion or abortion: inspect `State` for the outcome. Its timed overload throws `TimeoutException`; cancellation throws `OperationCanceledException`.

Termination aborts pending jobs and waits for active processors before the worker task exits. `OnTerminated()` runs after active processing exits. `Dispose()` aborts pending jobs immediately but does not block for active work.



## AsyncPulseEvent

`AsyncPulseEvent` is a thread synchronization event that a thread waits on until a pulse (signal) is received.
Only **one** waiter is supported at a time, and a pulse which arrives before the wait is retained by default (`retainPulseIfNoWaiter`).
Multiple retained pulses coalesce into one. A second concurrent wait throws `InvalidOperationException`. A canceled token returns `false` without consuming a retained pulse. A zero timeout polls immediately; other timeouts must be nonnegative and at most `int.MaxValue` milliseconds, or `Timeout.InfiniteTimeSpan`.

```csharp
private static async Task TestAsyncPulseEvent(ExecutionGroup parent)
{
    var pulseEvent = new AsyncPulseEvent();

    var c = new TaskCore(parent, async core =>
    {// Send a pulse after 1 second.
        await core.Delay(1_000);
        pulseEvent.Pulse();
    });

    // Returns true if a pulse is received, or false if the timeout elapses or the token is canceled.
    var result = await pulseEvent.WaitAsync(TimeSpan.FromSeconds(5), parent.CancellationToken);
    Console.WriteLine($"Pulse received: {result}");
}
```



## Locks

`SemaphoreLock` is a compact, non-reentrant exclusive lock.
It is used for object mutual exclusion, and can also be used in code that includes await syntax.

```csharp
private readonly SemaphoreLock semaphoreLock = new(); // Should be a private member since it uses lock (this).

using (this.semaphoreLock.EnterScope())
{// Synchronous
    this.count++;
}

using (await this.semaphoreLock.EnterScopeAsync())
{// Asynchronous
    this.count++;
}

if (this.semaphoreLock.TryEnter())
{// Without waiting
    try
    {
        this.count++;
    }
    finally
    {
        this.semaphoreLock.Exit();
    }
}
```

| Class/Interface | Description |
| ---- | ---- |
| `SemaphoreLock` | An exclusive lock which supports both synchronous and asynchronous code. |
| `MonitorLock` | An `ILockable` wrapper for `Monitor`. |
| `ILockable` / `IAsyncLockable` | Interfaces of a lock object (`EnterScope()`, `Enter()`, `Exit()`). |
| `LockStruct` | The lock scope returned by `EnterScope()`. It releases the lock when disposed. |
| `ILockObject` | An object which exposes a `System.Threading.Lock` object. |

Timed `SemaphoreLock.EnterAsync()` overloads return `false` on timeout or cancellation, including an already canceled token. Invalid timeouts throw before the lock or wait queue changes. If acquisition wins a race with cancellation, the result is `true` and the caller must release the lock.

`MonitorLock` is reentrant and must be released on the acquiring thread; do not hold it across `await`. Dispose each `LockStruct` through its original variable. Copying the struct duplicates its ownership flag and can cause a double release.



## Other utilities

| Class | Description |
| ---- | ---- |
| `DelayedTaskExecutor` | Executes an asynchronous action after a delay. Requests during the delay are coalesced into one execution. |
| `SingleTask` | Executes a task only if no task is running (`TryRun()` returns `null` if a task is in progress). |
| `UniqueWork` | Executes a work only once simultaneously. Concurrent callers join the work in progress. |
| `MicroSleep` | Microsecond-level sleep (`nanosleep`/`CreateWaitableTimerEx`/`timeBeginPeriod`). |
| `ExecutionId` | An ambient id which is local to a given asynchronous control flow (`AsyncLocal`). |
| `CancellationTokenPool` | A shared pool of `CancellationTokenSource` instances. |
| `EstimateSize` | Estimates the memory size of a struct/class. |
| `Task.TryDelay()` | `Task.Delay()` which returns `false` instead of throwing when canceled. |
| `AbortOrComplete` | A result enum for completed or aborted operations. |
| `PanicException` | An exception type for application-defined fatal errors; it does not terminate the process itself. |

```csharp
// Executes the action 500 ms after the first request.
var executor = new DelayedTaskExecutor(
    async cancellationToken => await SaveAsync(cancellationToken),
    TimeSpan.FromMilliseconds(500),
    core.CancellationToken);

executor.Request();
executor.Request(); // Coalesced into the request above.

// Returns false if the delay is canceled.
var delayed = await Task.TryDelay(1_000, cancellationToken);
```

`DelayedTaskExecutor` coalesces requests without restarting the delay. A request during execution schedules at most one additional delayed run. Handle action exceptions inside the action when reporting failures is required; `Request()` does not expose its background task.

`SingleTask.TryRun()` returns `null` during an active run; `RunningTask` exposes the current task. `UniqueWork.Run()` returns the same task to overlapping callers and awaits asynchronous work without blocking a thread. Both allow another run after completion or failure.

`MicroSleep` is not thread-safe and does not guarantee exact scheduling precision. Dispose it after use; negative durations and calls after disposal throw. `EstimateSize` deliberately allocates objects, and its class estimates include constructor allocations.

## Performance and ownership

- Retained pulse waits and uncontended `SemaphoreLock.EnterAsync()` reuse completed tasks. Pending waits allocate a task; timed/cancelable waits also require registration and cleanup state.
- `FindChild()` does not allocate a search delegate. Group snapshots are reused until membership changes.
- `ReusableTaskJob` allocates a new completion source for each rental. `ReusableThreadJob` reuses its event. Use `ReusableJob` for fire-and-forget work that needs no completion primitive.
- Return jobs to the worker that rented them only after processing and all waiters have finished. Reset custom fields before reuse. Do not clone active jobs or access jobs after returning them. `ReturnToPoolOnCompletion` is for fire-and-forget use; do not await or return those jobs manually.
- Return a pooled cancellation source only with exclusive ownership, after registrations finish and old tokens are no longer used. Canceled sources are disposed because they cannot be reset. Passing an already disposed source throws.
- `TaskCore` uses a long-running task that synchronously hosts its asynchronous delegate. Creating many task cores creates many dedicated threads; reuse a worker for large job streams.

## Build, test, and coverage

```sh
dotnet build Arc.Threading.slnx -c Release
dotnet test xUnitTest/xUnitTest.csproj -c Release
dotnet test xUnitTest/xUnitTest.csproj -c Release --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory artifacts/coverage
```

The test project uses Microsoft.Testing.Platform and its code coverage extension. Tests cover execution trees, startup and shutdown, pooled jobs, cancellation races, lock contention, utilities, and allocation-sensitive lookup. Coverage reports are written under `artifacts/coverage`; platform-specific native paths require tests on the corresponding operating system.

Run the allocation benchmarks with:

```sh
dotnet run --project Benchmark/Benchmark.csproj -c Release -- --filter '*HotPathBenchmark*'
```

See [the review report](docs/REVIEW.md) for measured coverage, allocation changes, and validation limits.
