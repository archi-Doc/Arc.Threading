# Arc.Threading

![NuGet](https://img.shields.io/nuget/v/Arc.Threading) ![Build and Test](https://github.com/archi-Doc/Arc.Threading/workflows/Build%20and%20Test/badge.svg)

Arc.Threading is a .NET 10 library for managing the lifetime of threads and tasks. It arranges threads, tasks, and groups in a cancellable tree, so you can start, signal, terminate, and await whole subtrees. It also provides a pooled job worker and small synchronization primitives designed for low allocation.

- [Quick start](#quick-start)
- [Execution trees](#execution-trees)
  - [Execution types](#execution-types)
  - [Ownership and groups](#ownership-and-groups)
  - [Startup and signals](#startup-and-signals)
  - [Termination and completion](#termination-and-completion)
  - [Disposal](#disposal)
- [Execution stacks](#execution-stacks)
- [Reusable jobs](#reusable-jobs)
  - [Worker](#worker)
  - [Job types](#job-types)
  - [Job lifecycle](#job-lifecycle)
  - [Waiting for jobs](#waiting-for-jobs)
  - [Concurrency and threading](#concurrency-and-threading)
  - [Pooling rules](#pooling-rules)
- [Pulse events](#pulse-events)
- [Locks](#locks)
- [Other utilities](#other-utilities)
- [Performance and allocation notes](#performance-and-allocation-notes)
- [Upgrade notes](#upgrade-notes)
- [NativeAOT](#nativeaot)
- [Build, test, and coverage](#build-test-and-coverage)

## Quick start

Install the package in a .NET 10 project:

```sh
dotnet add package Arc.Threading
```

Create a root, attach work, then request and await shutdown (a top-level program with implicit usings):

```csharp
using Arc.Threading;

using var root = new ExecutionRoot();

// Starts immediately on a dedicated long-running task.
_ = new TaskCore(root, async core =>
{
    while (await core.TryDelay(100)) // false once termination is requested
    {
        Console.WriteLine("Working");
    }
});

await Task.Delay(500);
root.RequestTermination();            // Cancel the work attached to the root.
await root.WaitForTerminationAsync(); // Wait until it has exited.
```

Termination is cooperative: delegates must check `CanContinue` or observe their cancellation token. `Dispose()` requests termination but does not wait, so await shutdown before disposing the root or resources that the work uses. Disposing the root terminates everything left in the tree.

## Execution trees

A tree starts at an `ExecutionRoot`. Every execution belongs to one root and has at most one parent group. Termination requests, waits, and signals flow down the tree.

### Execution types

| Type | Purpose |
| --- | --- |
| `ExecutionCore` | A cancellable node derived from `CancellationTokenSource`. An instance of exactly this type disposes itself when terminated. |
| `ExecutionGroup` | A container for child executions, with no thread or task of its own. |
| `ExecutionRoot` | The tree root. It owns `BaseGroup` and `IndependentGroup`, which are both independent. |
| `ThreadCore` | Runs an `Action<ThreadCore>` on a dedicated foreground thread. |
| `TaskCore` / `TaskCore<TSelf>` | Runs an asynchronous delegate on a dedicated long-running task. The generic form passes the derived instance as `TSelf`. |
| `TaskCompletionCore` | A core with a `CompletionTask` that completes when `SetCompleted()` is called. |
| `TaskCompletionGroup` | A group with a `CompletionTask`. It always belongs to an [execution stack](#execution-stacks). |
| `ReusableJobWorker<TJob>` | A `TaskCore` that processes pooled jobs. See [Reusable jobs](#reusable-jobs). |

| `ExecutionCoreOptions` | Effect on thread/task cores |
| --- | --- |
| `None` | Start immediately. Dispose the core when its delegate exits, or when a start is skipped because the core was already canceled. |
| `DelayedStart` | Do not start until `ExecutionSignal.Start` is received. |
| `NoDisposeOnCompletion` | Never dispose the core automatically. |

`TerminationOptions.None` (the default) skips independent descendants; `TerminationOptions.IncludeIndependent` includes them.

### Ownership and groups

- Constructors attach the new execution to `parent`. A disposed parent throws `ObjectDisposedException`. Work attached to a terminated parent is terminated immediately.
- `IsIndependent` (a constructor argument or property) makes recursive termination and waiting skip that execution and its subtree unless `IncludeIndependent` is set. Requests made directly on it still apply, and signals still reach it.
- `GetOrAddGroup(isIndependent, name)` returns the direct child group with that name (ordinal comparison), or creates it. It throws `InvalidOperationException` if the existing group has a different independence setting. The returned group may already be terminated; dispose it so that the next call creates a new one. `ExecutionRoot.GetOrAddUnitGroup(name)` does the same for independent groups under `IndependentGroup`.
- Set `Parent` or call `AddChild()` to move an execution within the same root. Self-parenting, cycles, and cross-root moves throw `InvalidOperationException`; disposed executions throw `ObjectDisposedException`. `Parent = null` detaches the execution, so ancestors no longer terminate or await it.
- `FindChild(id)` and `TryGetChildCancellationToken(id, out token)` search direct children only. `GetChildren()` returns a cached snapshot; treat it as read-only. `ChildCount` is advisory while membership changes.
- `Id` defaults to a process-wide unique, nonzero number (the root uses 0) and is reset to 0 on disposal. You can assign it, but uniqueness is then not enforced. `Name` is a display name.
- `core.CancellationToken` (or `core.Token`) is a token whose source is the core itself. `token.AsExecutionCore()` and `token.AsExecution<T>()` recover the execution, or return `null` for any other token. `core.ToCancellationToken()` converts the other way.

### Startup and signals

Thread/task cores start as soon as they are constructed, unless `DelayedStart` is set. A derived core whose delegate reads fields set in its constructor should use the protected `delayStart` constructor and start itself when ready:

```csharp
sealed class CounterCore : TaskCore<CounterCore>
{
    public CounterCore(ExecutionGroup parent, int intervalMilliseconds)
        : base(parent, Process, ExecutionCoreOptions.None, delayStart: true)
    {
        this.IntervalMilliseconds = intervalMilliseconds;
        this.SendSignal(ExecutionSignal.Start); // Fields are ready.
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

- Pass `DelayedStart` when the owner decides when to start, then call `core.SendSignal(ExecutionSignal.Start)`. Sending `Start` to a group or the root starts every delayed core below it, including independent ones.
- A core starts at most once; later `Start` signals are ignored.
- If a core is canceled before it starts, `Start` abandons it (the delegate never runs) and disposes it unless `NoDisposeOnCompletion` is set. As a result, a non-delayed `TaskCore`, `ThreadCore`, or `ReusableJobWorker<TJob>` created under a terminated parent is disposed inside its constructor: `Parent` is `null` and `TaskCore.Task` is already completed. An instance of a derived type is not disposed there, because its own constructor has not run yet; it stays terminated until its owner disposes it. A delayed core that is canceled and never receives `Start` also stays in the tree, terminated, until it or an ancestor is disposed.
- `ThreadCore` uses a foreground thread, which keeps the process alive until the delegate returns. For a background thread, create the core with `DelayedStart`, set `core.Thread.IsBackground = true`, then send `Start`.
- Thread/task cores react only to `Start`. `Cancel` and `Terminate` are application-defined notifications with no built-in effect; call `RequestTermination()` to cancel.
- Groups forward every signal to all current children, including independent ones. If a child throws, forwarding continues; afterwards a single exception is rethrown unchanged, and several are thrown as an `AggregateException`.
- An `ExecutionSignalHandler` passed to the `ExecutionCore` or `TaskCompletionGroup` constructor, or to `ExecutionStack.PushNew()`, replaces `OnSignalReceived()`, including group forwarding. Derived types can override `OnSignalReceived()` instead; call the base method to keep `Start` handling and forwarding.

For full control, a derived type can create its own task with the protected `TaskCore(parent, options)` constructor, `CreateLongRunningTask(core, Func<Task>)`, and `Initialize(task, startAutomatically)`.

### Termination and completion

| Member | Behavior |
| --- | --- |
| `RequestTermination(options)` | Cancels this execution and its descendants (independent ones only with `IncludeIndependent`). Idempotent, and ignored after disposal. Work attached during the request is canceled too. Exceptions from cancellation callbacks are ignored. |
| `CanContinue` | `false` once cancellation is requested. |
| `IsTerminated` | Thread/task cores: the delegate has exited, or the core was canceled before it started. Other cores and groups: cancellation was requested. |
| `WaitForTerminationAsync(timeout, options, ct)` | Returns `true` once the target has terminated, or `false` on timeout or cancellation. It does not request termination. |
| `TryDelay(delay, ct)` | Returns `true` after the delay, or `false` as soon as this execution or `ct` is canceled. |
| `SetCompleted()` | Completes the `CompletionTask` of a completion core or group. Completion and termination are independent. |

- Termination is cooperative. The inherited `Cancel()` and `CancelAfter()` cancel only that core's token and do not traverse the tree.
- Thread/task cores dispose themselves when the delegate exits, unless `NoDisposeOnCompletion` is set.
- An `OperationCanceledException` that escapes the delegate after termination was requested is a normal exit: `TaskCore.Task` completes successfully, and a `ThreadCore` ends quietly. Any other exception, including an `OperationCanceledException` thrown before termination was requested, faults `TaskCore.Task` and is unhandled on a `ThreadCore`, which terminates the process.
- Do not start `TaskCore.Task` yourself. It is already completed if the core was terminated when the task was created, and it stays unstarted if a delayed core is canceled before `Start`. Use `WaitForTerminationAsync()` to observe shutdown.
- A group is only a container: waiting on it waits for its non-group descendants (independent ones only with `IncludeIndependent`). A group with no such descendants counts as terminated even if it is not canceled. Waits check every 10 ms.
- `ExecutionRoot.WaitForTerminationAsync()` first requests termination of the whole `BaseGroup` subtree (including independent descendants) and waits for it. It then waits for the rest of the tree within the remaining timeout. It does **not** cancel the root's other children, so call `root.RequestTermination()` first. `IndependentGroup` is waited for only with `IncludeIndependent`, and the wait never cancels it.
- `CompletionTask` runs continuations asynchronously. Cancellation and disposal do not complete it.

A graceful shutdown of the whole tree:

```csharp
root.RequestTermination(TerminationOptions.IncludeIndependent);
var terminated = await root.WaitForTerminationAsync(TimeSpan.FromSeconds(5), TerminationOptions.IncludeIndependent);
root.Dispose();
```

### Disposal

- `Dispose()` requests termination, removes the execution from its parent and stack, and resets `Id` to 0. It does not join threads or tasks, and once disposed, the execution is no longer awaited by its ancestors. Repeated calls have no effect.
- Disposing a group first detaches it from its parent and stack, so lookups such as `GetOrAddGroup()` and `FindChild()` no longer return it. It then cancels its whole subtree, repeating until nothing attached is left uncanceled. Work attached to a descendant during disposal, for example by a cancellation callback or another thread, is therefore terminated as well. Finally, the non-independent descendants are detached from their parents and stacks. They are canceled but not disposed; thread/task cores still dispose themselves when their delegates exit.
- Independent children of a disposed group are detached **without** being canceled. They keep running outside the tree, so terminate them yourself.
- Disposing an `ExecutionRoot` terminates the whole tree, including `BaseGroup`, `IndependentGroup`, unit groups, and all independent descendants. It does not wait; for a graceful shutdown, await `WaitForTerminationAsync()` first.
- A disposed group rejects new children with `ObjectDisposedException`.

## Execution stacks

An `ExecutionStack` tracks executions of one root separately from the parent-child tree.

```csharp
var stack = new ExecutionStack(root);
using var operation = stack.PushNew(root.BaseGroup);
operation.SetCompleted();
await operation.CompletionTask;
```

- `PushNew(parent, signalHandler)` creates a non-independent `TaskCompletionGroup` under `parent` and adds it to the stack.
- `TryPush(core)` adds an existing execution. It returns `true` if the execution is now (or already) on this stack, and `false` if it is on another stack or has been disposed. An execution from a different root throws `InvalidOperationException`.
- An execution belongs to at most one stack. It leaves the stack when it, or a group that contains it, is disposed.
- `FirstCore`, `LastCore`, `Count`, `IsEmpty`, and `Find(id)` inspect the stack. Counts are advisory while membership changes.

## Reusable jobs

### Worker

`ReusableJobWorker<TJob>` is a `TaskCore` that queues jobs and pools the job objects, so a steady stream of work allocates little. Process jobs with a `JobProcessor` delegate (synchronous), or override `ProcessJobAsync(job, cancellationToken)` (asynchronous); the token is the worker's own token.

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

| Member | Behavior |
| --- | --- |
| Constructor | `(parent, processJob = null, poolCapacity = 32, options = None)`. `poolCapacity` limits the idle jobs kept for reuse. |
| `Rent(options)` | Returns a pooled or new job in the `Initial` state. |
| `Add(job)` | Queues an `Initial` job (it becomes `Pending`); other states throw `InvalidOperationException`. A stopped or disposed worker aborts the job. |
| `Return(job)` | Pools a `Completed` or `Aborted` job; jobs in other states are ignored. |
| `WaitForCompletionAsync(timeout, ct)` | Waits until the worker is idle. See [Waiting for jobs](#waiting-for-jobs). |
| `MaxConcurrentTasks` | The maximum number of concurrent processors (default 1, minimum 1). |
| `IsIdle` / `PendingJobCount` | Whether no job is pending or running / the number of queued jobs. |
| `OnJobFinished(job)` | Override hook. Runs after a job completes or aborts, before its waiters are released. |
| `OnTerminated()` | Override hook. Runs when the worker loop exits, after all active processors finish. |

### Job types

| Job type | Wait primitive |
| --- | --- |
| `ReusableTaskJob` | `Task` / `WaitAsync()`; recommended for asynchronous callers. Uses a new completion source for each rental. |
| `ReusableBlockingJob` | `Wait()`, backed by one reusable `ManualResetEventSlim`. |
| `ReusableJob` | None; for fire-and-forget work. |

Derive a `record class` from one of these types and add your own properties. Rent with `ReusableJobOptions.ReturnToPoolOnCompletion` to have the worker return the job automatically when it finishes (fire-and-forget).

### Job lifecycle

`Initial` (rented) -> `Pending` (added) -> `Running` -> `Completed` or `Aborted` -> `Pooled` (returned).

- A job is `Aborted` when the worker is stopping, when processing throws, or when `OnJobFinished()` throws. The exception is not reported anywhere else, so log failures inside the processor. A failing job does not stop the worker or other jobs.
- `State` becomes final before `OnJobFinished()` runs. Waiters are released after it, even if it throws.
- When the worker is terminated (by `RequestTermination()` on it or an ancestor), it aborts pending jobs, lets running jobs finish (they see the canceled token), waits for all processors, calls `OnTerminated()`, and then disposes itself unless `NoDisposeOnCompletion` is set.
- `Dispose()` cancels the worker and aborts pending jobs immediately. It does not wait for running jobs.
- A `DelayedStart` worker that is terminated before `Start` aborts its queued jobs when canceled, and never calls `OnTerminated()`.

### Waiting for jobs

- A job wait completes for both `Completed` and `Aborted`, so inspect `State`. Canceling or timing out a wait never cancels the job.
- `ReusableTaskJob.WaitAsync(ct)` throws `OperationCanceledException` on cancellation; `WaitAsync(timeout, ct)` also throws `TimeoutException` on timeout.
- `ReusableBlockingJob.Wait(timeout, ct)` returns `false` on timeout and throws `OperationCanceledException` on cancellation.
- Waiting on a returned job throws `InvalidOperationException`. A `ReusableTaskJob` reused from the pool and rented with `ReturnToPoolOnCompletion` has no completion source, so its `Task` and `WaitAsync()` throw as well.
- `worker.WaitForCompletionAsync()` returns `true` as soon as no job is pending or running, including aborted jobs, so it does not mean every job succeeded. It returns `false` on timeout, on cancellation, if the worker is disposed, or if it stops with jobs outstanding. The wait is event-driven (with a 1-second safety check), not a fixed-interval poll. It observes idleness and is not a barrier against later `Add()` calls.

### Concurrency and threading

- Set `MaxConcurrentTasks` before submitting work. Raising it adds processors as jobs arrive; lowering it does not interrupt active processors.
- With a limit above 1, the `JobProcessor` delegate, `ProcessJobAsync()`, and `OnJobFinished()` run concurrently and must be thread-safe. Jobs are dequeued in FIFO order but may finish in any order.
- The main processing loop is hosted by the worker's long-running task; additional processors run on the thread pool.
- All jobs run under the `ExecutionContext` captured when the worker was constructed. `AsyncLocal` values of the thread that calls `Add()` do not flow into jobs.
- `OnJobFinished()` may run on any thread, including the thread that calls `Add()`, `Dispose()`, or `RequestTermination()` when pending jobs are aborted.

### Pooling rules

- Return a job only to the worker that rented it, and only after processing and all waits have finished. A final `State` is not enough: the worker may still be running `OnJobFinished()` and releasing waiters.
- Do not touch a job after returning it. `Return()` does not reset your properties, so reset them before reuse.
- Jobs rented with `ReturnToPoolOnCompletion` are fire-and-forget: do not await, access, or return them after `Add()`.
- The pool keeps up to `poolCapacity` idle jobs; extra returned jobs are discarded.
- Jobs use reference equality. A copy made with `with` is a new job in the `Initial` state, with default options and its own wait primitive; your properties are copied. The compiler still generates `GetHashCode()` from the properties of your derived record, so override it (`public override int GetHashCode() => base.GetHashCode();`) if you use jobs as hash keys. `ToString()` is safe on pooled jobs.

## Pulse events

`AsyncPulseEvent` supports **one waiter at a time**. A pulse that arrives while no one is waiting is retained by default, and several retained pulses coalesce into one. Pass `retainPulseIfNoWaiter: false` to drop such pulses instead.

```csharp
var pulse = new AsyncPulseEvent();
var waiting = pulse.WaitAsync(TimeSpan.FromSeconds(5), root.CancellationToken);
pulse.Pulse();
bool signaled = await waiting;
```

- `WaitAsync(timeout, ct)` returns `true` for a pulse and `false` on timeout or cancellation; it throws for neither. Continuations of pending waits run asynchronously.
- A second concurrent wait throws `InvalidOperationException`.
- An already canceled token returns `false` without consuming a retained pulse. A zero timeout only checks for a retained pulse.
- A timeout must be infinite or between 0 and `int.MaxValue` milliseconds.
- `Pulse()` is lock-free and can be called from any thread.

## Locks

`SemaphoreLock` is a non-reentrant exclusive lock for synchronous and asynchronous callers.

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

| Member | Behavior |
| --- | --- |
| `Enter()` / `EnterScope()` | Blocks until the lock is acquired. Throws `ThreadInterruptedException` if the thread is interrupted, without leaving a queued waiter behind. |
| `TryEnter()` | Acquires the lock only if it is free. |
| `EnterAsync()` / `EnterScopeAsync()` | Waits asynchronously and always acquires the lock. |
| `EnterAsync(timeout)`, `EnterAsync(ct)`, `EnterAsync(timeout, ct)` | Returns `false` on timeout or cancellation. `TimeSpan.Zero` only tries once. |
| `Exit()` | Releases the lock. Throws `SynchronizationLockException` if it is not held. |
| `IsLocked` | Whether the lock is held. |

- Pair each successful `Enter()`, `EnterAsync()`, or `TryEnter()` with exactly one `Exit()`, normally in `finally`. Scopes release automatically.
- Ownership is not tracked: entering again from the same flow deadlocks, and any thread may call `Exit()` (for example, after an `await`).
- Keep the instance private, because it locks itself internally (`lock (this)`).
- An already canceled token returns `false` immediately, even if the lock is free. An invalid timeout (negative other than -1 ms, or above 4,294,967,294 ms) throws `ArgumentOutOfRangeException` before the lock or its queue changes.
- If acquisition wins a race with cancellation, the result is `true` and you must call `Exit()`.
- Continuations of asynchronous waiters never run inline inside `Exit()` or `CancellationTokenSource.Cancel()`.

| Type | Purpose |
| --- | --- |
| `ILockable` / `IAsyncLockable` | Lock interfaces (`Enter()`, `Exit()`, `IsLocked`, `EnterAsync()`) with default `EnterScope()` / `EnterScopeAsync()` helpers. |
| `MonitorLock` | A reentrant `Monitor` wrapper. It is thread-affine: release it on the acquiring thread and never hold it across `await`. `IsLocked` reports ownership by the current thread. |
| `LockScope` | A disposable struct that releases its lock once. `IsLocked` tells whether it holds the lock. Do not copy it: each copy has its own ownership flag. |
| `ILockProvider` | Exposes a `System.Threading.Lock` through `LockObject`. |

## Other utilities

| API | Behavior |
| --- | --- |
| `DelayedTaskExecutor` | Coalesces `Request()` calls into one delayed run of an asynchronous action. |
| `SingleTask` | `TryRun()` runs work on the thread pool when idle, and returns `null` while busy. `RunningTask` is the current task. |
| `UniqueWork` | `Run()` starts the work on the thread pool, or returns the task already in progress. |
| `MicroSleep` | A blocking sleep specified in microseconds, using native timers. |
| `ExecutionId.Get()` | Returns an ambient `long` ID for the current asynchronous flow. |
| `CancellationTokenPool` | `Rent()` and `Return()` pooled `CancellationTokenSource` instances (up to `PoolCapacity`, 256). |
| `EstimateSize` | `Struct<T>()` returns the managed size of `T` (pointer size for reference types). `Class<T>()` and `Constructor(factory)` return the average bytes allocated per construction on the current thread. |
| `Task.TryDelay(delay, ct)` | A C# 14 static extension on `Task` that returns `false` on cancellation instead of throwing. |
| `AbortOrComplete` | A result enum: `Abort` or `Complete`. |
| `PanicException` | An application-defined fatal error. Throwing it does not terminate the process by itself. |

- **DelayedTaskExecutor** (`new DelayedTaskExecutor(action, delay, cancellationToken)`): the delay starts at the first request; later requests do not restart it. Requests during the delay coalesce. Requests during a run schedule at most one more run, which happens even if the current run fails. A zero delay still runs asynchronously. `Request()` returns `true` if it changed the state. Cancellation drops pending runs. Handle exceptions inside the action, because `Request()` does not expose the background task.
- **SingleTask** and **UniqueWork**: failures and cancellation propagate through the returned task (to every caller sharing a `UniqueWork` run). A new run can start once the current one completes. `UniqueWork` always starts work on the thread pool, never inline under its lock.
- **MicroSleep**: uses `nanosleep` on Unix and a high-resolution waitable timer on Windows, falling back to `timeBeginPeriod(1)` and `Thread.Sleep` (rounded up to whole milliseconds). `CurrentMode` shows which is used. `Sleep(int microseconds)` rejects negative values and throws `ObjectDisposedException` after disposal; an interrupted Unix sleep resumes for the remaining time. It is not thread-safe, and precision depends on the OS. Dispose it to release native resources (a finalizer also releases the fallback timer period).

  ```csharp
  using var sleeper = new MicroSleep();
  sleeper.Sleep(microseconds: 500);
  ```

- **ExecutionId**: assigned on the first call and then returned within the flow, including flows started from it afterwards. An ID first assigned in a child flow (such as an awaited method) does not flow back to the caller, so call `Get()` at the start of the logical operation. IDs are unique only within the process.
- **CancellationTokenPool**: a rented source is yours alone. Return it once, after its registrations finish and its old tokens are no longer used. Canceled sources cannot be reset and are disposed. Linked and derived sources are disposed too, because a linked source stays subscribed to its parents. Returning a disposed plain source throws `ObjectDisposedException`.
- **EstimateSize**: includes allocations made inside the constructor or factory. One untimed warm-up call excludes one-time costs such as static constructors, so the factory runs 1,001 times.

## Performance and allocation notes

- Allocation-free paths: consuming a retained pulse, uncontended `SemaphoreLock.EnterAsync()` and `EnterScopeAsync()` (one cached task per lock), `TryDelay()` that returns immediately (zero delay or already canceled), `WaitForTerminationAsync()` on a target that has already terminated, `FindChild()`, and `GetChildren()` while membership is unchanged. `TaskCompletionCore` and `TaskCompletionGroup` create their completion source only when `CompletionTask` is first read.
- Pending waits allocate. A cancelable wait adds a token registration, and a timed pulse wait also rents a pooled `CancellationTokenSource`. `SemaphoreLock.EnterAsync(ct)` registers directly on the token, without a timer or an async state machine.
- `WaitForTerminationAsync()` polls every 10 ms using one `PeriodicTimer` per call, so expect up to about 10 ms of latency.
- Each running `TaskCore`, including each worker, occupies a dedicated thread that hosts its asynchronous delegate. Use one worker for a large stream of jobs instead of one core per job.
- Workers use pooled async state machines for job processing, start additional processors without capturing the caller's context, and wake `WaitForCompletionAsync()` by event instead of polling.
- `ReusableTaskJob` allocates a completion source per rental (none for auto-returned pooled jobs), `ReusableBlockingJob` reuses its event, and `ReusableJob` has no wait primitive.

## Upgrade notes

Several public members were renamed; see [Arc.Threading Name Changes](doc/Arc.Threading%20Name%20Changes.md). For example, the parameter of `MicroSleep.Sleep()` is now `microseconds`. These behavior changes may surprise existing code:

- Disposing `ExecutionRoot` terminates the whole tree, including `BaseGroup`, `IndependentGroup`, and unit groups. ([Disposal](#disposal))
- Disposing a group also terminates work attached to its subtree during disposal. ([Disposal](#disposal))
- A thread/task core that is canceled before it starts is disposed when `Start` arrives. A non-delayed core of a library type created under a terminated parent is disposed inside its constructor; derived types are not. ([Startup and signals](#startup-and-signals))
- An `OperationCanceledException` thrown after termination was requested completes `TaskCore.Task` successfully, and no longer crashes the process from a `ThreadCore`. ([Termination and completion](#termination-and-completion))
- `ThreadCore` runs on a foreground thread. ([Startup and signals](#startup-and-signals))
- Groups forward a signal to every child even if one throws, and rethrow afterwards. ([Startup and signals](#startup-and-signals))
- Default `Id` values come from a process-wide counter instead of random numbers, and disposal resets `Id` to 0. ([Ownership and groups](#ownership-and-groups))
- `ReusableJobWorker.WaitForCompletionAsync()` is event-driven and returns as soon as the worker is idle. ([Waiting for jobs](#waiting-for-jobs))
- All jobs run under the `ExecutionContext` captured when the worker was constructed, not the producer's. ([Concurrency and threading](#concurrency-and-threading))
- Jobs use reference equality, and `with` copies start fresh in the `Initial` state. A pooled `ReusableTaskJob` rented with `ReturnToPoolOnCompletion` has no `Task`. ([Pooling rules](#pooling-rules))
- `CancellationTokenPool.Return()` disposes linked and derived sources instead of pooling them. ([Other utilities](#other-utilities))
- `DelayedTaskExecutor` still performs a rerun that was requested during a failed run. ([Other utilities](#other-utilities))

## NativeAOT

The library enables `IsAotCompatible`. `NativeAotSmokeTest` exercises execution trees, generic cores and workers, pooling, token conversion, synchronization, ambient IDs, allocation helpers, and native sleep. It fails when run on a JIT (dynamic-code) runtime, so run the published executable.

The smoke project roots the whole library for trimming analysis and treats compiler, trimming, and AOT warnings as errors. CI publishes and runs it for Windows x64 and Linux x64. Install the [NativeAOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#prerequisites) before publishing.

```powershell
dotnet publish NativeAotSmokeTest/NativeAotSmokeTest.csproj -c Release -r win-x64 -o artifacts/nativeaot/win-x64
./artifacts/nativeaot/win-x64/NativeAotSmokeTest.exe
```

```sh
dotnet publish NativeAotSmokeTest/NativeAotSmokeTest.csproj -c Release -r linux-x64 -o artifacts/nativeaot/linux-x64
./artifacts/nativeaot/linux-x64/NativeAotSmokeTest
```

## Build, test, and coverage

```sh
dotnet build Arc.Threading.slnx -c Release
dotnet test --project xUnitTest/xUnitTest.csproj -c Release
dotnet test --project xUnitTest/xUnitTest.csproj -c Release --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory artifacts/coverage
```

The test project uses Microsoft.Testing.Platform (selected in `global.json`) and its code coverage extension. Reports are written under `artifacts/coverage`. The Build and Test workflow runs the tests with coverage on Windows and Linux, and runs the NativeAOT smoke tests; OS-specific native paths are exercised only on the matching OS.

Tests cover execution ownership and shutdown, delayed startup, worker concurrency and pooling, lock interruption and contention, cancellation races, and allocation-sensitive operations. Coverage percentages describe executed lines and branches; they do not prove the absence of concurrency bugs.

The 2026-10-15 Windows x64 Release run passed 183 tests: line coverage was 94.81% (1,170/1,234) and branch coverage was 94.77% (634/669). Before this review, 96 tests covered 93.22% of lines and 92.87% of branches. Remaining gaps are mainly the Unix and fallback paths of `MicroSleep`, which cannot run on Windows, and rare race-retry branches.

Run the allocation benchmarks (BenchmarkDotNet with `MemoryDiagnoser`) with:

```sh
dotnet run --project Benchmark/Benchmark.csproj -c Release -- --filter '*HotPathBenchmark*'
```
