// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Arc.Collections;

namespace Arc.Threading;

#pragma warning disable SA1124 // Do not use regions
#pragma warning disable SA1629 // Documentation text should end with a period
#pragma warning disable SA1405 // Debug.Assert should provide message text

/// <summary>
/// Provides a job worker that processes pooled, reusable <typeparamref name="TJob"/> instances on a background task.<br/>
/// To process the actual job, either override <see cref="ProcessJobAsync(TJob, CancellationToken)"/> (recommended) or provide a <see cref="JobProcessor"/> in the constructor.<br/>
/// <br/>
/// Example: <br/>
/// var job = worker.Rent(); // Rent a job object from the pool.<br/>
/// job.Initialize(10); // Set the job parameters (user-defined).<br/>
/// worker.Add(job); // Enqueue the job.<br/>
/// await job.WaitAsync(); // Wait until the job is complete.<br/>
/// worker.Return(job); // Return the job object to the pool.
/// </summary>
/// <typeparam name="TJob">
/// The reusable job type handled by this worker. The type must inherit from <see cref="ReusableJob"/>
/// and expose a public parameterless constructor.
/// </typeparam>
/// <remarks>
/// This worker combines an internal object pool with a pending queue to reduce allocations and support high-throughput scheduling.<br/>
/// Jobs are expected to follow the lifecycle:<br/>
/// <see cref="ReusableJobState.Initial"/> -> <see cref="ReusableJobState.Pending"/> ->
/// <see cref="ReusableJobState.Running"/> -> <see cref="ReusableJobState.Completed"/> (or <see cref="ReusableJobState.Aborted"/>) -> <see cref="ReusableJobState.Pooled"/> (after <see cref="Return(TJob)"/>).<br/>
/// Jobs run under the execution context captured when the worker was created, not that of the thread that adds them.<br/>
/// When the worker is terminated (its parent is terminated or it is disposed), pending jobs are aborted and their waiters are released.
/// By default, the worker disposes itself when its processing loop exits, or when it receives the start signal after termination
/// (see <see cref="ExecutionCoreOptions.NoDisposeOnCompletion"/>).
/// </remarks>
public class ReusableJobWorker<TJob> : TaskCore<ReusableJobWorker<TJob>>, IDisposable
    where TJob : ReusableJob, new()
{
    private const int DefaultPoolCapacity = 32;
    private const int FallbackPollMilliseconds = 1_000; // Bounds a completion wait even if an idle notification is missed.

    /// <summary>
    /// Represents the method that processes a job.
    /// </summary>
    /// <param name="worker">The <see cref="ReusableJobWorker{TJob}"/> instance which owns the job.</param>
    /// <param name="job">The job to process. Its state is <see cref="ReusableJobState.Running"/>.</param>
    /// <remarks>
    /// The delegate runs synchronously on a background thread. An exception marks the job as <see cref="ReusableJobState.Aborted"/>.
    /// </remarks>
    public delegate void JobProcessor(object worker, TJob job);

    private static async Task Process(ReusableJobWorker<TJob> worker)
    {
        while (worker.CanContinue)
        {
            var addEvent = worker.addEvent;
            if (addEvent is null)
            {// Disposed
                goto Terminated;
            }

            try
            {
                if (await addEvent.WaitAsync(worker.CancellationToken).ConfigureAwait(false) != true)
                {
                    goto Terminated;
                }
            }
            catch
            {
                goto Terminated;
            }

            // worker.OnBeforeProcessJob();
            Interlocked.Increment(ref worker.numberOfTasks);
            while (worker.pendingJobs.TryDequeue(out var job))
            {
                Debug.Assert(job.State == ReusableJobState.Pending);
                var numberOfPendingJobs = Interlocked.Decrement(ref worker.numberOfPendingJobs);

                if (worker.MaxConcurrentTasks > 1)
                {
                    worker.TryAddConcurrentTask(numberOfPendingJobs);
                }

                await ProcessJob(worker, job).ConfigureAwait(false);

                if (!worker.CanContinue)
                {// To prevent the job from freezing, complete the acquired job first, then check whether it has been terminated.
                    worker.DecrementTasks();
                    goto Terminated;
                }
            }

            worker.DecrementTasks();
            // worker.OnAfterProcessJob();
        }

Terminated:
        worker.AbortAllJobs();
        if (Volatile.Read(ref worker.numberOfTasks) != 0)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Full fence: the publication must be visible before re-reading the counter (pairs with Interlocked.Decrement in DecrementTasks).
            Interlocked.Exchange(ref worker.concurrentTasksCompletion, completion);
            if (Volatile.Read(ref worker.numberOfTasks) != 0)
            {
                await completion.Task.ConfigureAwait(false);
            }
        }

        worker.OnTerminated();
    }

    // Pooled builder: avoids a state machine allocation for each job that completes asynchronously. Each result is awaited exactly once.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private static async ValueTask ProcessJob(ReusableJobWorker<TJob> worker, TJob job)
    {
        try
        {
            if (!worker.CanContinue)
            {
                job.State = ReusableJobState.Aborted;
                return;
            }

            job.State = ReusableJobState.Running;
            if (worker.processJob is null)
            {
                await worker.ProcessJobAsync(job, worker.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                worker.processJob(worker, job);
            }

            job.State = ReusableJobState.Completed;
        }
        catch
        {
            job.State = ReusableJobState.Aborted;
        }
        finally
        {
            worker.FinishJob(job);
        }
    }

    #region FieldAndProperty

    /// <summary>
    /// Gets or sets the maximum number of worker tasks allowed to process queued jobs concurrently.
    /// </summary>
    /// <value>
    /// The concurrency limit for background processing. The default value is <c>1</c>.
    /// </value>
    /// <remarks>
    /// Values must be positive. Lowering the limit does not interrupt active processors.<br/>
    /// With a limit greater than 1, the processor delegate, <see cref="ProcessJobAsync(TJob, CancellationToken)"/>, and <see cref="OnJobFinished(TJob)"/> may run concurrently.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxConcurrentTasks
    {
        get => Volatile.Read(ref this.maxConcurrentTasks);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            Volatile.Write(ref this.maxConcurrentTasks, value);
        }
    }

    /// <summary>
    /// Gets a value indicating whether no job is pending and no job is being processed.
    /// </summary>
    public bool IsIdle
        => Volatile.Read(ref this.numberOfOutstandingJobs) == 0 &&
           Volatile.Read(ref this.numberOfTasks) == 0;

    /// <summary>
    /// Gets the current number of jobs waiting to be processed.
    /// </summary>
    public int PendingJobCount => Volatile.Read(ref this.numberOfPendingJobs);

    // Initialized before the base constructor publishes this worker to its parent, so an early Start and termination can safely abort jobs.
    private readonly ConcurrentQueue<TJob> pendingJobs = new();
    private readonly ExecutionContext? executionContext = ExecutionContext.Capture(); // The context of the main processing loop.
    private readonly JobProcessor? processJob;
    private readonly ObjectPool<TJob> freeJobs;
    private AsyncPulseEvent? addEvent = new();
    private TaskCompletionSource? concurrentTasksCompletion;
    private TaskCompletionSource? idleCompletion; // Completed when the worker becomes idle while a completion wait is pending.
    private int numberOfPendingJobs;
    private int numberOfTasks;
    private int numberOfConcurrentTasks;
    private int maxConcurrentTasks = 1;
    private int numberOfOutstandingJobs;

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableJobWorker{TJob}"/> class.
    /// </summary>
    /// <param name="parent">The parent execution group used for lifecycle coordination.</param>
    /// <param name="processJob">
    /// Optional delegate used to process each job. If <see langword="null"/>, <see cref="ProcessJobAsync(TJob, CancellationToken)"/> is invoked.
    /// </param>
    /// <param name="poolCapacity">
    /// The maximum number of idle jobs kept for reuse, rounded up to a power of two (at least 2). Jobs returned while the pool is full are discarded.
    /// </param>
    /// <param name="options">Behavior flags controlling startup and completion semantics.</param>
    /// <remarks>
    /// Unless <see cref="ExecutionCoreOptions.DelayedStart"/> is set, the worker starts immediately.
    /// If <paramref name="parent"/> is already terminated, the worker is terminated at once and jobs added to it are aborted.
    /// Unless <see cref="ExecutionCoreOptions.DelayedStart"/> or <see cref="ExecutionCoreOptions.NoDisposeOnCompletion"/> is set, an instance of exactly this type is also disposed
    /// before the constructor returns; an instance of a derived type is not, because its constructor has not run yet.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="parent"/> has been disposed.</exception>
    public ReusableJobWorker(ExecutionGroup parent, JobProcessor? processJob = default, int poolCapacity = DefaultPoolCapacity, ExecutionCoreOptions options = ExecutionCoreOptions.None)
        : base(parent, Process, options, true)
    {
        this.processJob = processJob;
        this.freeJobs = new(() => new(), poolCapacity);
        if ((options & ExecutionCoreOptions.DelayedStart) == 0)
        {
            this.StartFromConstructor(typeof(ReusableJobWorker<TJob>));
        }
        else
        {// A delayed worker terminated before Start never runs Process; release the jobs queued so far on cancellation.
            this.CancellationToken.UnsafeRegister(
                static state =>
                {
                    var worker = (ReusableJobWorker<TJob>)state!;
                    if (worker.IsTerminated)
                    {// Not started. A started worker aborts pending jobs in Process.
                        worker.AbortAllJobs();
                    }
                },
                this);
        }
    }

    /// <summary>
    /// Rents a reusable job instance from the internal pool, or creates one if the pool is empty.
    /// </summary>
    /// <param name="options">
    /// Options that control the behavior of the job.
    /// With <see cref="ReusableJobOptions.ReturnToPoolOnCompletion"/>, do not access the job after passing it to <see cref="Add(TJob)"/>.
    /// </param>
    /// <returns>A job in the <see cref="ReusableJobState.Initial"/> state. Custom fields keep the values set before the job was returned.</returns>
    public TJob Rent(ReusableJobOptions options = default)
    {
        var job = this.freeJobs.Rent();
        job.State = ReusableJobState.Initial;
        job.Options = options;
        job._PrepareSynchronizationPrimitive(); // Options must be assigned first.
        return job;
    }

    /// <summary>
    /// Returns a used job to the internal pool.<br/>
    /// Reset custom state and finish all waits before returning the job. Do not access it afterward.
    /// </summary>
    /// <param name="job">The job to return.</param>
    /// <remarks>
    /// Only jobs in the <see cref="ReusableJobState.Completed"/> or <see cref="ReusableJobState.Aborted"/> state are accepted.<br/>
    /// Other jobs are silently ignored.<br/>
    /// Return a job only after its wait has completed: the state becomes final before <see cref="OnJobFinished(TJob)"/> runs and waiters are released,<br/>
    /// so returning a job as soon as <see cref="ReusableJob.State"/> is final lets the worker touch a job that may already be rented again.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="job"/> is <see langword="null"/>.</exception>
    public void Return(TJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var currentState = Volatile.Read(ref job.state);
        if (currentState == (byte)ReusableJobState.Completed ||
            currentState == (byte)ReusableJobState.Aborted)
        {// Completed -> Pooled, Aborted -> Pooled
            if (Interlocked.CompareExchange(ref job.state, (byte)ReusableJobState.Pooled, currentState) == currentState)
            {
                job.Options = default;
                job._ResetSynchronizationPrimitive();
                // job.OnReturnToPool();
                this.freeJobs.Return(job);
            }
        }
    }

    /// <summary>
    /// Enqueues a rented or newly created job for background processing.
    /// </summary>
    /// <param name="job">The job to enqueue.</param>
    /// <remarks>
    /// The job transitions from <see cref="ReusableJobState.Initial"/> to <see cref="ReusableJobState.Pending"/>.
    /// A stopped or disposed worker aborts the job and releases its waiters.<br/>
    /// Jobs added to a worker created with <see cref="ExecutionCoreOptions.DelayedStart"/> stay pending until it is started or terminated.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="job"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="job"/> is not in the <see cref="ReusableJobState.Initial"/> state.
    /// </exception>
    public void Add(TJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        // Initial -> Pending
        if (Interlocked.CompareExchange(ref job.state, (byte)ReusableJobState.Pending, (byte)ReusableJobState.Initial) != (byte)ReusableJobState.Initial)
        {
            throw new InvalidOperationException("A job can be enqueued only when it is in ReusableJobState.Initial");
        }

        Interlocked.Increment(ref this.numberOfOutstandingJobs);
        Interlocked.Increment(ref this.numberOfPendingJobs);
        this.pendingJobs.Enqueue(job);
        this.addEvent?.Pulse();

        if (this.MaxConcurrentTasks > 1 && Volatile.Read(ref this.numberOfTasks) != 0)
        {
            this.TryAddConcurrentTask(Volatile.Read(ref this.numberOfPendingJobs));
        }

        if (!this.CanContinue)
        {
            this.AbortAllJobs();
        }
    }

    /// <summary>
    /// Waits indefinitely for all pending and active jobs to complete.
    /// </summary>
    /// <param name="cancellationToken">
    /// A cancellation token that can be used to cancel the wait operation.
    /// </param>
    /// <returns>
    /// A task that returns <see langword="true"/> once every added job has completed or been aborted,<br/>
    /// or <see langword="false"/> if the wait is canceled, the worker has been disposed, or the worker stops with jobs outstanding.
    /// </returns>
    public Task<bool> WaitForCompletionAsync(CancellationToken cancellationToken = default)
        => this.WaitForCompletionAsync(Timeout.Infinite, cancellationToken);

    /// <summary>
    /// Waits for the completion of all jobs.
    /// </summary>
    /// <param name="timeout">The maximum wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
    /// <param name="cancellationToken">
    /// A cancellation token that can be used to cancel the wait operation.
    /// </param>
    /// <returns>
    /// A task that returns <see langword="true"/> once every added job has completed or been aborted;<br/>
    /// otherwise, <see langword="false"/> on timeout, on cancellation, or if the worker has been disposed or stops with jobs outstanding.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative other than -1 ms, or exceeds <see cref="int.MaxValue"/> milliseconds.</exception>
    public Task<bool> WaitForCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return this.WaitForCompletionAsync(Timeout.Infinite, cancellationToken);
        }
        else if (timeout < TimeSpan.Zero ||
            timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return this.WaitForCompletionAsync((int)timeout.TotalMilliseconds, cancellationToken);
    }

    /// <summary>
    /// Waits for the completion of all jobs.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait, -1 to wait indefinitely, or 0 to check once without waiting.</param>
    /// <param name="cancellationToken">
    /// A cancellation token that can be used to cancel the wait operation.
    /// </param>
    /// <returns>
    /// A task that returns <see langword="true"/> once every added job has completed or been aborted;<br/>
    /// otherwise, <see langword="false"/> on timeout, on cancellation, or if the worker has been disposed or stops with jobs outstanding.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is less than -1.</exception>
    /// <remarks>The wait is signaled when the worker becomes idle, and re-checks the state at least once per second as a fallback.</remarks>
    public async Task<bool> WaitForCompletionAsync(int millisecondsTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, Timeout.Infinite);
        if (this.IsDisposed)
        {
            // throw new ObjectDisposedException(this.GetType().Name);
            return false;
        }

        long startTimestamp = 0;
        if (millisecondsTimeout != Timeout.Infinite)
        {
            startTimestamp = Stopwatch.GetTimestamp();
        }

        CancellationTokenSource? linkedSource = null;
        try
        {
            while (true)
            {
                if (this.IsIdle)
                {
                    return true;
                }
                else if (this.IsDisposed)
                {
                    return false;
                }
                else if (this.IsTerminated)
                {
                    return false;
                }
                else if (cancellationToken.IsCancellationRequested || this.IsCancellationRequested)
                {// Canceled, or the worker is stopping with jobs outstanding.
                    return false;
                }

                var waitMilliseconds = FallbackPollMilliseconds;
                if (millisecondsTimeout != Timeout.Infinite)
                {
                    var remainingMilliseconds = millisecondsTimeout - (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                    if (remainingMilliseconds <= 0)
                    {
                        return false;
                    }
                    else if (waitMilliseconds > remainingMilliseconds)
                    {
                        waitMilliseconds = (int)remainingMilliseconds;
                    }
                }

                var idleTask = this.GetIdleTask();
                if (this.IsIdle)
                {// Re-check after publishing the completion source (pairs with NotifyIfIdle).
                    return true;
                }

                CancellationToken token;
                if (!cancellationToken.CanBeCanceled || cancellationToken == this.CancellationToken)
                {
                    token = this.CancellationToken;
                }
                else
                {// One linked source per call, not per wake-up.
                    linkedSource ??= CancellationTokenSource.CreateLinkedTokenSource(this.CancellationToken, cancellationToken);
                    token = linkedSource.Token;
                }

                await idleTask.WaitAsync(TimeSpan.FromMilliseconds(waitMilliseconds), token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        finally
        {
            linkedSource?.Dispose();
        }
    }

    /// <summary>
    /// Starts the worker once for <see cref="ExecutionSignal.Start"/>; other signals are ignored.
    /// </summary>
    /// <param name="signal">The received execution signal.</param>
    /// <remarks>If the start is skipped because the worker is already terminated, pending jobs are aborted.</remarks>
    /// <exception cref="InvalidOperationException">The task cannot be started, for example because it was started directly instead of through this signal.</exception>
    public override void OnSignalReceived(ExecutionSignal signal)
    {
        base.OnSignalReceived(signal);
        if (signal == ExecutionSignal.Start && this.IsTerminated)
        {// Covers a cancellation that raced with the start, which the cancellation callback cannot observe.
            this.AbortAllJobs();
        }
    }

    /// <summary>
    /// Processes a single job instance.
    /// </summary>
    /// <param name="job">The job to process. The job's state will be <see cref="ReusableJobState.Running"/> when this method is called.</param>
    /// <param name="cancellationToken">The worker's cancellation token, which is canceled when the worker is terminated or disposed.</param>
    /// <returns>A task representing the asynchronous job processing operation.</returns>
    /// <remarks>
    /// Override this method to implement custom job processing logic.<br/>
    /// This method is called automatically by the worker when a job is dequeued from the pending queue.<br/>
    /// Alternatively, you can provide a <c>processJob</c> delegate in the constructor; this method is then not called.<br/>
    /// Exceptions mark the job as <see cref="ReusableJobState.Aborted"/> and do not stop other jobs.<br/>
    /// Calls run on background threads (the worker's dedicated thread or thread-pool threads), concurrently when <see cref="MaxConcurrentTasks"/> is greater than 1.
    /// </remarks>
    protected virtual Task ProcessJobAsync(TJob job, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when a job has completed or been aborted, before its waiters are released.
    /// If this method throws, the job is marked <see cref="ReusableJobState.Aborted"/> and waiters are still released.
    /// </summary>
    /// <param name="job">A completed or aborted job.</param>
    /// <remarks>
    /// May run concurrently and on any thread, including the caller of <see cref="Add(TJob)"/> or of disposal or termination when pending jobs are aborted.<br/>
    /// A job rented with <see cref="ReusableJobOptions.ReturnToPoolOnCompletion"/> is returned to the pool right after this call; do not keep a reference to it.
    /// </remarks>
    protected virtual void OnJobFinished(TJob job)
    {
    }

    /*/// <summary>
    /// Called before the worker begins processing currently pending jobs.
    /// </summary>
    protected virtual void OnBeforeProcessJob()
    {
    }

    /// <summary>
    /// Called after the worker finishes processing the current batch of pending jobs.
    /// </summary>
    protected virtual void OnAfterProcessJob()
    {
    }*/

    /// <summary>
    /// Called when the worker loop exits, after pending jobs are aborted and all active processors finish.
    /// </summary>
    /// <remarks>Not called if the worker is terminated or disposed before it starts.</remarks>
    protected virtual void OnTerminated()
    {
    }

    /// <summary>
    /// Releases the resources used by this worker, and aborts the pending jobs.
    /// </summary>
    /// <param name="disposing"><see langword="true"/> to release both managed and unmanaged resources; <see langword="false"/> to release only unmanaged resources.</param>
    /// <remarks>
    /// Termination is requested before pending jobs are aborted. Jobs that are already running are not awaited; they observe the canceled token.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (!this.IsDisposed)
        {
            base.Dispose(disposing); // Cancel first, so the processing loop can exit only after CanContinue is false.

            if (disposing)
            {// Release the jobs which will never be processed (e.g. the worker has not been started).
                this.addEvent = null;
                this.AbortAllJobs();
            }
        }
    }

    private void TryAddConcurrentTask(int numberOfPendingJobs)
    {// Let each processor recruit another until the queue or concurrency limit is exhausted.
        while (true)
        {
            var currentTasks = Volatile.Read(ref this.numberOfConcurrentTasks);
            if (numberOfPendingJobs <= 0 || !this.CanContinue ||
                currentTasks >= this.MaxConcurrentTasks - 1)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref this.numberOfConcurrentTasks, currentTasks + 1, currentTasks) == currentTasks)
            {
                break;
            }
        }

        Interlocked.Increment(ref this.numberOfTasks);
        if (!this.CanContinue)
        {// Add may race with shutdown; reserve the processor before the final cancellation check.
            this.CompleteConcurrentTask();
            return;
        }

        // Do not flow the recruiting thread's context: jobs from other producers would observe its AsyncLocal values.
        ThreadPool.UnsafeQueueUserWorkItem(static worker => worker.StartConcurrentProcessor(), this, preferLocal: false);
    }

    private void StartConcurrentProcessor()
    {
        if (this.executionContext is { } context)
        {// Run additional processors under the same context as the main processing loop.
            ExecutionContext.Run(context, static state => _ = ((ReusableJobWorker<TJob>)state!).ProcessConcurrentJobsAsync(), this);
        }
        else
        {
            _ = this.ProcessConcurrentJobsAsync();
        }
    }

    private async Task ProcessConcurrentJobsAsync()
    {
        try
        {
            while (this.pendingJobs.TryDequeue(out var job))
            {
                var numberOfPendingJobs = Interlocked.Decrement(ref this.numberOfPendingJobs);
                this.TryAddConcurrentTask(numberOfPendingJobs);
                await ProcessJob(this, job).ConfigureAwait(false);

                if (!this.CanContinue)
                {// To prevent the job from freezing, complete the acquired job first, then check whether it has been terminated.
                    return;
                }
            }
        }
        finally
        {
            this.CompleteConcurrentTask();
        }
    }

    private void CompleteConcurrentTask()
    {
        Interlocked.Decrement(ref this.numberOfConcurrentTasks);

        // A submission may have seen a full worker after this processor found the queue empty.
        // Hand off that work while this processor is still counted for termination.
        if (!this.pendingJobs.IsEmpty)
        {
            this.TryAddConcurrentTask(Volatile.Read(ref this.numberOfPendingJobs));
        }

        this.DecrementTasks();
    }

    private void DecrementTasks()
    {
        if (Interlocked.Decrement(ref this.numberOfTasks) == 0)
        {
            Volatile.Read(ref this.concurrentTasksCompletion)?.TrySetResult();
            this.NotifyIfIdle();
        }
    }

    private Task GetIdleTask()
    {
        var source = Volatile.Read(ref this.idleCompletion);
        if (source is null)
        {
            var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source = Interlocked.CompareExchange(ref this.idleCompletion, created, null) ?? created;
        }

        Interlocked.MemoryBarrier(); // The publication must be visible before the caller re-checks IsIdle.
        return source.Task;
    }

    private void NotifyIfIdle()
    {// Called right after a counter reaches 0 through an Interlocked decrement, which is a full fence.
        if (Volatile.Read(ref this.idleCompletion) is not null && this.IsIdle)
        {
            Interlocked.Exchange(ref this.idleCompletion, null)?.TrySetResult();
        }
    }

    private void AbortAllJobs()
    {
        while (this.pendingJobs.TryDequeue(out var job))
        {// Mark pending jobs as Aborted and return control.
            Interlocked.Decrement(ref this.numberOfPendingJobs);
            job.State = ReusableJobState.Aborted;
            this.FinishJob(job);
        }
    }

    private void FinishJob(TJob job)
    {
        var returnToPool = job.ReturnsToPoolOnCompletion;
        try
        {
            this.OnJobFinished(job);
        }
        catch
        {
            job.State = ReusableJobState.Aborted;
        }
        finally
        {
            job._SetSynchronizationPrimitive();
            if (returnToPool)
            {
                this.Return(job);
            }

            if (Interlocked.Decrement(ref this.numberOfOutstandingJobs) == 0)
            {
                this.NotifyIfIdle();
            }
        }
    }
}
