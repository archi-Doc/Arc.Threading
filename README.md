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



## Quick Start

First, install Arc.Threading using Package Manager Console.

```
Install-Package Arc.Threading
```

Arc.Threading targets .NET 10 and above.



## NativeAOT

Arc.Threading is marked with `IsAotCompatible=true`. NativeAOT publishing and execution have been verified locally on Windows x64 with .NET SDK 10.0.400 and Arc.Collections 1.44.0, with no build, trimming, or AOT warnings.

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
| `ExecutionCore` | A cancellable execution unit. It derives from `CancellationTokenSource`, so it can be passed anywhere a `CancellationToken` is expected. |
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
| `IsTerminated` | `true` if the thread/task has exited, or was canceled before it started. |
| `WaitForTermination(timeout, options, ct)` | Waits until all the target executions are terminated. |
| `Delay(milliseconds, ct)` | `Task.Delay()` which returns `false` instead of throwing when the execution is terminated. |
| `Dispose()` | Requests the termination, and removes this execution from the tree. |

By default, an execution disposes itself when the execution method exits.
Specify `ExecutionCoreOptions.KeepAliveOnCompletion` to keep the object alive.

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



## AsyncPulseEvent

`AsyncPulseEvent` is a thread synchronization event that a thread waits on until a pulse (signal) is received.
Only **one** waiter is supported at a time, and a pulse which arrives before the wait is retained by default (`retainPulseIfNoWaiter`).

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

`SemaphoreLock` is a simplified version of `SemaphoreSlim` (the size of an instance is 40 bytes).
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

```csharp
// Executes the action 500 ms after the last request.
var executor = new DelayedTaskExecutor(
    async cancellationToken => await SaveAsync(cancellationToken),
    TimeSpan.FromMilliseconds(500),
    core.CancellationToken);

executor.Request();
executor.Request(); // Coalesced into the request above.

// Returns false if the delay is canceled.
var delayed = await Task.TryDelay(1_000, cancellationToken);
```
