# Arc.Threading Name Changes

This release renames public API members of Arc.Threading. **Behavior is unchanged.** Enum numeric values are unchanged, so serialized values stay compatible.

Most renames cause compile errors, so fix call sites by following the compiler. The items marked ⚠ need extra care (see [Pitfalls](#pitfalls)).

## Types

| Old | New | Notes |
| --- | --- | --- |
| `ExecutionHelper` | `ExecutionExtensions` | Static class; extension call sites need no change. |
| `LockStruct` | `LockScope` | Return type of `EnterScope()` / `EnterScopeAsync()`. |
| `ILockObject` | `ILockProvider` | Property `LockObject` is unchanged. |
| `ReusableJobFlags` | `ReusableJobOptions` | Member `ReturnToPoolOnCompletion` is unchanged. |
| `ReusableThreadJob` | `ReusableBlockingJob` | |
| `MicroSleep.Mode` (nested enum) | `MicroSleepMode` (top-level enum) | |
| `ReusableJobWorker<TJob>.ProcessJobDelegate` | `ReusableJobWorker<TJob>.JobProcessor` | Delegate. |

## Methods

| Type | Old | New |
| --- | --- | --- |
| `ExecutionExtensions` | `Pack(this ExecutionCore)` | `ToCancellationToken(this ExecutionCore)` |
| `ExecutionExtensions` | `Extract<TExecution>(this CancellationToken)` | `AsExecution<TExecution>(this CancellationToken)` |
| `ExecutionExtensions` | `ExtractCore(this CancellationToken)` | `AsExecutionCore(this CancellationToken)` |
| `ExecutionCore` | `Delay(...)` ⚠ | `TryDelay(...)` |
| `ExecutionCore`, `ExecutionRoot` | `WaitForTermination(...)` ⚠ | `WaitForTerminationAsync(...)` |
| `ExecutionCore` (protected static) | `ValidateParent(...)` | `ValidateArguments(...)` |
| `ExecutionRoot` | `UnitGroup(string)` | `GetOrAddUnitGroup(string)` |
| `ExecutionStack` | `Push(ExecutionCore)` | `TryPush(ExecutionCore)` |
| `TaskCompletionCore`, `TaskCompletionGroup` | `TrySetCompleted()` | `SetCompleted()` |
| `ReusableJobWorker<TJob>` | `WaitForCompletion(...)` | `WaitForCompletionAsync(...)` |
| `ReusableJobWorker<TJob>` (protected virtual) | `OnJobProcessing(TJob, CancellationToken)` ⚠ | `ProcessJobAsync(TJob, CancellationToken)` |
| `CancellationTokenPool` | `TryResetAndReturn(CancellationTokenSource)` | `Return(CancellationTokenSource)` |

## Properties

| Type | Old | New |
| --- | --- | --- |
| `ExecutionGroup` | `Count` ⚠ | `ChildCount` |
| `ReusableJobWorker<TJob>` | `IsCompleted` | `IsIdle` |
| `ReusableJobWorker<TJob>` | `NumberOfPendingJobs` | `PendingJobCount` |
| `ReusableJob` | `Flags` | `Options` |
| `ReusableJob` | `ReturnToPoolOnCompletion` ⚠ | `ReturnsToPoolOnCompletion` |
| `LockScope` | `LockableObject` | `Lockable` |

## Enum members

| Enum | Old | New |
| --- | --- | --- |
| `ExecutionSignal` | `Exit` | `Terminate` |
| `ExecutionCoreOptions` | `Default` | `None` |
| `ExecutionCoreOptions` | `KeepAliveOnCompletion` | `NoDisposeOnCompletion` |

## Parameters

These renames matter only for callers who use **named arguments**.

| Member | Old | New |
| --- | --- | --- |
| `ExecutionCore` constructors | `executionSignalHandler` | `signalHandler` |
| `TaskCompletionGroup` constructor | `executionSignalHandler` | `signalHandler` |
| `ExecutionStack.PushNew` | `processSignalHandler` | `signalHandler` |
| `ExecutionSignalHandler` delegate | `executionCore`, `executionSignal` | `core`, `signal` |
| `ExecutionCore.TryDelay(int, ...)` | `millisecondsToWait` | `millisecondsDelay` |
| `TaskCore<TSelf>` protected constructor | `deferStart` | `delayStart` |
| `ReusableJobWorker<TJob>.Rent` | `flags` | `options` |
| `SemaphoreLock.EnterAsync(int)` | `timeoutInMilliseconds` | `millisecondsTimeout` |
| `MicroSleep.Sleep` | `microSeconds` | `microseconds` |
| `SingleTask.TryRun(Action)` | `task` | `action` |
| `SingleTask.TryRun(Func<Task>)` | `task` | `asyncAction` |
| `UniqueWork(Func<Task>)` constructor | `task` | `asyncAction` |
| `EstimateSize.Constructor` | `constructor` | `factory` |
| `LockScope` constructor | `lockableObject` | `lockable` |

## Pitfalls

- **`OnJobProcessing` overrides**: rename the override to `ProcessJobAsync`. Otherwise the build fails with "no suitable method found to override".
- **`WaitForTermination` overrides**: classes that override it must rename the override to `WaitForTerminationAsync`.
- **`Delay` → `TryDelay`**: rename only calls on `ExecutionCore` and its derived types (`TaskCore`, `ThreadCore`, `ReusableJobWorker<TJob>`, ...). Do **not** rename `Task.Delay`.
- **`Count` → `ChildCount`**: only `ExecutionGroup` (and `ExecutionRoot`, `TaskCompletionGroup`) changed. `ExecutionStack.Count` is unchanged.
- **`ReturnToPoolOnCompletion`**: the `ReusableJob` bool property became `ReturnsToPoolOnCompletion`, but the enum member `ReusableJobOptions.ReturnToPoolOnCompletion` keeps its name.
- **`Options` on workers**: `ReusableJobWorker<TJob>.Options` (`ExecutionCoreOptions`, inherited from `TaskCore`) and `ReusableJob.Options` (`ReusableJobOptions`) are different properties.

## Suggested search/replace (regex, whole word)

Review each hit; `Delay`, `Count`, `Push`, `Flags`, `IsCompleted`, and `Exit` are too generic for blind replacement.

```text
\bExecutionHelper\b          -> ExecutionExtensions
\bLockStruct\b               -> LockScope
\bILockObject\b              -> ILockProvider
\bReusableJobFlags\b         -> ReusableJobOptions
\bReusableThreadJob\b        -> ReusableBlockingJob
\bMicroSleep\.Mode\b         -> MicroSleepMode
\bProcessJobDelegate\b       -> JobProcessor
\.Pack\(                     -> .ToCancellationToken(
\bExtractCore\b              -> AsExecutionCore
\.Extract<                   -> .AsExecution<
\bWaitForTermination\b       -> WaitForTerminationAsync
\bWaitForCompletion\b        -> WaitForCompletionAsync
\bOnJobProcessing\b          -> ProcessJobAsync
\bNumberOfPendingJobs\b      -> PendingJobCount
\bTrySetCompleted\b          -> SetCompleted
\bTryResetAndReturn\b        -> Return
\.UnitGroup\(                -> .GetOrAddUnitGroup(
\.LockableObject\b           -> .Lockable
\bKeepAliveOnCompletion\b    -> NoDisposeOnCompletion
\bExecutionCoreOptions\.Default\b -> ExecutionCoreOptions.None
\bExecutionSignal\.Exit\b    -> ExecutionSignal.Terminate
```
