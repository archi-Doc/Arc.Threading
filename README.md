# Arc.Threading

![NuGet](https://img.shields.io/nuget/v/Arc.Threading) ![Build and Test](https://github.com/archi-Doc/Arc.Threading/workflows/Build%20and%20Test/badge.svg)

Arc.Threading is a .NET 10 library for cooperative thread/task lifecycles, pooled jobs, and synchronous/asynchronous coordination.

- [Quick start](#quick-start)
- [Execution trees](#execution-trees)
- [Reusable jobs](#reusable-jobs)
- [Pulse events](#pulse-events)
- [Locks](#locks)
- [Other utilities](#other-utilities)
- [Performance and ownership](#performance-and-ownership)
- [Build, test, and coverage](#build-test-and-coverage)
- [NativeAOT](#nativeaot)

## Quick start

Install the package in a .NET 10 project:

```sh
dotnet add package Arc.Threading
```

Create a root, attach work, then request and await shutdown:

```csharp
using Arc.Threading;

using var root = new ExecutionRoot();
using var worker = new TaskCore(root, async core =>
{
    while (await core.TryDelay(100))
    {
        Console.WriteLine("Working");
    }
});

await Task.Delay(500);
root.RequestTermination(TerminationOptions.IncludeIndependent);
await root.WaitForTerminationAsync(TerminationOptions.IncludeIndependent);
```

Termination is cooperative: delegates must observe `CanContinue` or their cancellation token. `Dispose()` requests termination and detaches the execution, but does not wait for running work. Await shutdown before disposing resources used by that work.

## Execution trees

| Type | Purpose |
| --- | --- |
| `ExecutionCore` | A cancellable execution unit derived from `CancellationTokenSource`. |
| `ExecutionGroup` | A container for child executions, with no thread or task of its own. |
| `ExecutionRoot` | A tree root with predefined `BaseGroup` and `IndependentGroup` children. |
| `ThreadCore` | Runs a delegate on a dedicated thread. |
| `TaskCore` / `TaskCore<TSelf>` | Hosts an asynchronous delegate on a long-running task; the generic form passes the derived instance. |
| `TaskCompletionCore` / `TaskCompletionGroup` | Adds a `CompletionTask` completed explicitly by `SetCompleted()`. |
| `ExecutionStack` | Tracks executions separately from their parent-child tree. |

### Groups and ownership

Use `GetOrAddGroup(isIndependent, name)` to reuse a named child group. `ExecutionRoot.GetOrAddUnitGroup(name)` creates or finds an independent group under `IndependentGroup`.

`Parent` and `AddChild()` move executions within the same root. Cycles, cross-root moves, and disposed parents are rejected. A terminated parent immediately requests termination of newly attached work. `FindChild(id)` and `TryGetChildCancellationToken(id, out token)` search direct children only.

`GetChildren()` returns a cached snapshot. Treat the returned array as read-only; later membership changes produce a new snapshot. Disposing an execution removes it from its parent and stack.

### Startup and signals

Thread/task cores start immediately unless `ExecutionCoreOptions.DelayedStart` is set. Use delayed start when a derived delegate needs fields initialized by its constructor:

```csharp
sealed class CounterCore : TaskCore<CounterCore>
{
    public CounterCore(ExecutionGroup parent, int intervalMilliseconds)
        : base(parent, Process, ExecutionCoreOptions.DelayedStart)
    {
        this.IntervalMilliseconds = intervalMilliseconds;
    }

    public int IntervalMilliseconds { get; }

    private static async Task Process(CounterCore core)
    {
        while (await core.TryDelay(core.IntervalMilliseconds))
        {
            Console.WriteLine("Tick");
        }
    }
}

```

After construction:

```csharp
using var counter = new CounterCore(root, 1000);
counter.SendSignal(ExecutionSignal.Start);
```

`SendSignal(ExecutionSignal.Start)` starts delayed thread/task cores at most once. Groups forward signals to all children, including independent children. Supplying a signal handler replaces the default `OnSignalReceived()` dispatch, including group forwarding.

`Cancel` and `Terminate` signals are application-defined notifications; they do not cancel execution automatically. Call `RequestTermination()` for cancellation. If a delayed `TaskCore` is canceled before it starts, its `Task` remains unstarted; use `WaitForTerminationAsync()` to observe shutdown.

### Termination and completion

| Member | Behavior |
| --- | --- |
| `RequestTermination(options)` | Cancels this execution and selected descendants. Independent descendants are excluded unless `IncludeIndependent` is set. |
| `CanContinue` | Becomes `false` when cancellation is requested. |
| `IsTerminated` | For thread/task cores, indicates delegate exit or cancellation before startup. For plain cores and groups, indicates cancellation. |
| `WaitForTerminationAsync(timeout, options, ct)` | Returns `true` when the selected work has terminated, or `false` on timeout/cancellation. Groups wait for their executable descendants. |
| `TryDelay(duration, ct)` | Returns `false` if this execution or the optional additional token is canceled; otherwise returns `true` after the delay. |
| `SetCompleted()` | Completes a completion core/group's `CompletionTask`; it does not request termination. |

`ExecutionRoot.WaitForTerminationAsync()` first requests and waits for termination of **all** `BaseGroup` descendants, including independent ones. It then waits for the rest of the tree using the same timeout budget. `IndependentGroup` is excluded unless `IncludeIndependent` is specified; including it in a wait does not itself cancel it.

Thread/task cores dispose themselves when their delegates exit unless `ExecutionCoreOptions.NoDisposeOnCompletion` is set. Calling inherited `Cancel()` directly cancels only that core's token and does not traverse the tree. Cancellation or disposal does not complete `CompletionTask`.

Use `core.CancellationToken` or inherited `core.Token` with cancellable APIs. `token.AsExecutionCore()` and `token.AsExecution<T>()` recover the associated execution, or return `null` for an unrelated token.

### Execution stacks

```csharp
var stack = new ExecutionStack(root);
using var operation = stack.PushNew(root.BaseGroup);
operation.SetCompleted();
await operation.CompletionTask;
```

`TryPush(core)` associates an existing execution with one stack under the same root. `FirstCore`, `LastCore`, `Count`, `IsEmpty`, and `Find(id)` inspect membership. Disposing a member removes it from the stack.

## Reusable jobs

`ReusableJobWorker<TJob>` queues and pools job objects. Set `MaxConcurrentTasks` before submitting work to establish a fixed concurrency limit (default: 1). Lowering the limit does not interrupt active processors.

| Job type | Completion primitive |
| --- | --- |
| `ReusableTaskJob` | `Task` / `WaitAsync()`; recommended for asynchronous callers. |
| `ReusableBlockingJob` | `Wait()` backed by a reusable `ManualResetEventSlim`. |
| `ReusableJob` | No completion primitive; suitable for fire-and-forget work. |

```csharp
using var worker = new ReusableJobWorker<PrintJob>(root, (_, job) =>
{
    Console.WriteLine(job.Message);
});
worker.MaxConcurrentTasks = 4;

var job = worker.Rent();
job.Message = "Hello";
worker.Add(job);
await job.WaitAsync();
Console.WriteLine(job.State); // Completed or Aborted
worker.Return(job);
await worker.WaitForCompletionAsync();

public record class PrintJob : ReusableTaskJob
{
    public string Message { get; set; } = "";
}
```

Override `ProcessJobAsync(job, cancellationToken)` instead of supplying a delegate for asynchronous processing. The job lifecycle is `Initial` -> `Pending` -> `Running` -> `Completed`/`Aborted` -> `Pooled`.

Processing exceptions mark a job `Aborted`. `OnJobFinished(job)` runs before waiters are released; an exception from this hook also marks the job `Aborted` and still releases waiters. Job waits complete for either outcome, so inspect `State`. A timed `WaitAsync()` throws `TimeoutException`; canceling a job wait throws `OperationCanceledException` and does not cancel the job.

`WaitForCompletionAsync()` returns `true` when the queue and processors are idle; this does not mean every job succeeded. It returns `false` on timeout, cancellation while waiting, or disposal. It is an observation of idleness, not a barrier against future submissions.

Termination aborts pending jobs and waits for active processors before the worker task exits. `OnTerminated()` runs after active processing finishes. `Dispose()` aborts pending jobs immediately without blocking for active work.

## Pulse events

`AsyncPulseEvent` supports **one waiter at a time**. Pulses arriving before a wait are retained by default; multiple retained pulses coalesce into one. Pass `retainPulseIfNoWaiter: false` to discard pulses when idle.

```csharp
var pulse = new AsyncPulseEvent();
var waiting = pulse.WaitAsync(TimeSpan.FromSeconds(5), root.CancellationToken);
pulse.Pulse();
bool signaled = await waiting;
```

A wait returns `true` for a pulse or `false` for timeout/cancellation. A second concurrent wait throws `InvalidOperationException`. An already canceled token returns `false` without consuming a retained pulse. A zero timeout polls immediately. Other timeouts must be nonnegative and at most `int.MaxValue` milliseconds, or infinite.

## Locks

`SemaphoreLock` is a non-reentrant lock for synchronous and asynchronous callers. Keep it private because its internal monitor locks the instance itself.

```csharp
var mutex = new SemaphoreLock();
using (mutex.EnterScope())
{
    Console.WriteLine("Synchronous access");
}

using (await mutex.EnterScopeAsync())
{
    await Console.Out.WriteLineAsync("Asynchronous access");
}
```

`TryEnter()` attempts acquisition without waiting for the exclusive lock to be released. Pair a successful `Enter()`, `EnterAsync()`, or `TryEnter()` with exactly one `Exit()`, normally in `finally`. Scope overloads release automatically.

Timed `EnterAsync()` returns `false` on timeout or cancellation. Invalid timeouts throw before changing the lock or queue. If acquisition wins a cancellation race, the result is `true` and the caller must release the lock.

| Type | Purpose |
| --- | --- |
| `ILockable` / `IAsyncLockable` | Lock interfaces with scope helpers. |
| `MonitorLock` | A reentrant `Monitor` wrapper; release it on the acquiring thread and never hold it across `await`. |
| `LockScope` | A disposable struct that releases its acquired lock. Do not copy it: each copy retains its ownership flag. |
| `ILockProvider` | Exposes a `System.Threading.Lock`. |

## Other utilities

| API | Behavior |
| --- | --- |
| `DelayedTaskExecutor` | Coalesces requests into one delayed asynchronous action. Requests during execution schedule at most one additional delayed run. |
| `SingleTask.TryRun()` | Schedules work on the thread pool when idle; returns `null` while busy. `RunningTask` exposes the current task. |
| `UniqueWork.Run()` | Schedules work on the thread pool; overlapping callers share its task. Asynchronous work does not block a thread. |
| `MicroSleep` | Sleeps for a duration in microseconds using native timers. Not thread-safe; scheduling precision depends on the platform. Dispose after use. |
| `ExecutionId.Get()` | Gets an ambient ID for the asynchronous flow. Child flows inherit an already assigned ID. |
| `CancellationTokenPool` | Rents and returns cancellation token sources with exclusive ownership. |
| `EstimateSize.Struct<T>()` | Returns the managed value size, or pointer size for a reference type. |
| `EstimateSize.Class<T>()` / `Constructor(factory)` | Measures average allocations on the current thread, including allocations inside constructors/factories. |
| `Task.TryDelay()` | Returns `false` on cancellation instead of throwing. |
| `AbortOrComplete` | A result enum for aborted or completed operations. |
| `PanicException` | An application-defined fatal error; throwing it does not itself terminate the process. |

`DelayedTaskExecutor` starts its delay at the first request; later requests do not restart it. A zero delay still schedules the action asynchronously. Handle action exceptions inside the action when failure reporting is needed, because `Request()` does not expose the background task.

`SingleTask` and `UniqueWork` propagate failures through their returned tasks and allow another run after completion. `MicroSleep` rejects negative durations and use after disposal, and retries interrupted Unix sleeps for the remaining duration.

## Performance and ownership

- Retained pulse waits, uncontended `SemaphoreLock.EnterAsync()`, and zero-duration `ExecutionCore.TryDelay()` reuse completed tasks. Pending waits allocate; timed/cancelable waits require extra state.
- `FindChild()` avoids a search delegate. Group snapshots are reused until membership changes.
- `TaskCore` uses a dedicated long-running task that synchronously hosts its asynchronous delegate. Reuse workers for large job streams rather than creating a core per job.
- `ReusableTaskJob` creates a completion source for each rental. `ReusableBlockingJob` reuses its event. `ReusableJob` has no completion primitive.
- Return jobs only to their originating worker, after processing and all waits finish. Reset custom fields before reuse. Do not clone active jobs or access a job after returning it. `ReturnToPoolOnCompletion` is for fire-and-forget use; do not await or return those jobs manually.
- Return a pooled cancellation source only after registrations finish and old tokens are no longer used. Canceled sources are disposed because they cannot be reset. Returning a disposed source throws.
- Worker shutdown awaits processor completion without periodic delay allocations. Additional processors reuse a cached delegate.

## Build, test, and coverage

```sh
dotnet build Arc.Threading.slnx -c Release
dotnet test --project xUnitTest/xUnitTest.csproj -c Release
dotnet test --project xUnitTest/xUnitTest.csproj -c Release --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory artifacts/coverage
```

The test project uses Microsoft.Testing.Platform and its code coverage extension. Reports are written under `artifacts/coverage`. The Build and Test workflow runs tests on Windows and Linux; native paths require the corresponding operating system.

Tests cover execution ownership and shutdown, delayed startup, worker concurrency and pooling, lock interruption/contention, cancellation races, and allocation-sensitive operations. Coverage percentages describe executed lines and branches; they do not prove the absence of concurrency bugs.

The 2026-10-06 Windows x64 Release run passed 96 tests: line coverage was 93.13% (962/1,033) and branch coverage was 92.87% (482/519). Before the review, 72 tests covered 90.87% of lines and 89.86% of branches. Remaining gaps include Unix sleep paths, Windows timer fallback/native failures, and rare scheduling/error branches.

After warmup, 1,000 pairs of zero-delay calls with an external token and already-canceled termination waits allocated 704,000 bytes in the original implementation and zero bytes after the changes. Eight targeted regression cases reproduce the original ownership, lock, worker, and retained-reference bugs against the original source. These are allocation and correctness checks, not throughput measurements.

Run allocation benchmarks with:

```sh
dotnet run --project Benchmark/Benchmark.csproj -c Release -- --filter '*HotPathBenchmark*'
```

## NativeAOT

The library enables `IsAotCompatible`. `NativeAotSmokeTest` exercises execution trees, generic cores/workers, pooling, token conversion, synchronization, ambient IDs, allocation helpers, and native sleep. It rejects execution under a dynamic-code runtime.

The smoke project roots the library for trimming analysis and treats compiler, trimming, and AOT warnings as errors. CI publishes and runs it for Windows x64 and Linux x64. Install the [NativeAOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#prerequisites) before publishing.

```powershell
dotnet publish NativeAotSmokeTest/NativeAotSmokeTest.csproj -c Release -r win-x64 -o artifacts/nativeaot/win-x64
./artifacts/nativeaot/win-x64/NativeAotSmokeTest.exe
```

```sh
dotnet publish NativeAotSmokeTest/NativeAotSmokeTest.csproj -c Release -r linux-x64 -o artifacts/nativeaot/linux-x64
./artifacts/nativeaot/linux-x64/NativeAotSmokeTest
```
