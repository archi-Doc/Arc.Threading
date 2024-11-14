// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1124 // Do not use regions

namespace Arc.Threading;

/// <summary>
/// Support class for <see cref="System.Threading.Thread"/>.
/// </summary>
public class ThreadCore : ThreadCoreBase
{
    /// <summary>
    /// The default interval time in milliseconds.
    /// </summary>
    public const int DefaultInterval = 10;

    /// <summary>
    /// Gets the root object of all ThreadCoreBase classes.
    /// </summary>
    public static ThreadCoreRoot Root { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadCore"/> class.
    /// </summary>
    /// <param name="parent">The parent of this thread/task.<br/>
    /// Specify <see langword="null"/> to be independent (does not receive a termination signal from parent).</param>
    /// <param name="method">The method that executes on a System.Threading.Thread.</param>
    /// <param name="startImmediately">Starts the thread immediately.<br/>
    /// <see langword="false"/>: Manually call <see cref="Start"/> to start the thread.</param>
    public ThreadCore(ThreadCoreBase? parent, Action<object?> method, bool startImmediately = true)
    {
        this.Thread = new Thread(new ParameterizedThreadStart(method));
        this.Prepare(parent); // this.Thread (this.IsRunning) might be referenced after this method.
        if (startImmediately)
        {
            this.Start(false);
        }
    }

    /// <inheritdoc/>
    public override void Start(bool includeChildren = false)
    {
        if (Interlocked.CompareExchange(ref this.started, 1, 0) == 0)
        {
            this.Thread.Start(this);
        }

        if (includeChildren)
        {
            this.StartChildren();
        }
    }

    /// <inheritdoc/>
    public override bool IsRunning => (this.started != 0) && (this.Thread.ThreadState & System.Threading.ThreadState.Stopped) == 0; // this.Thread.IsRunning;

    /// <inheritdoc/>
    public override bool IsThreadOrTask => true;

    /// <summary>
    /// Gets an instance of <see cref="System.Threading.Thread"/>.
    /// </summary>
    public Thread Thread { get; }

    private int started;

    #region NanoSleep

    internal struct Timespec
    {
        internal Timespec(long seconds, long nanoseconds)
        {
            this.tv_sec = seconds;
            this.tv_nsec = nanoseconds;
        }

        internal Timespec(TimeSpan timeSpan)
        {
            this.tv_sec = (long)timeSpan.Seconds;
            this.tv_nsec = (long)((timeSpan.TotalSeconds - this.tv_sec) * 1000000000d);
        }

#pragma warning disable SA1307 // Accessible fields should begin with upper-case letter
#pragma warning disable SA1310 // Field names should not contain underscore
        internal long tv_sec; // Seconds.
        internal long tv_nsec; // Nanoseconds.
#pragma warning restore SA1307 // Accessible fields should begin with upper-case letter
#pragma warning restore SA1310 // Field names should not contain underscore
    }

    [System.Runtime.InteropServices.DllImport("libc")]
#pragma warning disable SA1300 // Element should begin with upper-case letter
    private static extern int nanosleep(ref Timespec req, ref Timespec rem);
#pragma warning restore SA1300 // Element should begin with upper-case letter

    static ThreadCore()
    {
        try
        {
            var request = default(Timespec);
            var remaining = default(Timespec);
            nanosleep(ref request, ref remaining);
            isNanoSleepAvailable = true;
        }
        catch
        {
        }
    }

    private static bool isNanoSleepAvailable = false;

    public void TryNanoSleep(long nanoSeconds)
    {
        if (isNanoSleepAvailable)
        {
            var seconds = nanoSeconds / 1_000_000_000;
            var request = new Timespec(seconds, nanoSeconds % 1_000_000_000);
            var remaining = default(Timespec);
            nanosleep(ref request, ref remaining);
        }
        else
        {
            var milliseconds = nanoSeconds / 1_000_000;
            if (milliseconds == 0 && nanoSeconds != 0)
            {
                milliseconds = 1;
            }

            this.Sleep((int)milliseconds);
        }
    }

    #endregion

    #region IDisposable Support

    /// <summary>
    /// Finalizes an instance of the <see cref="ThreadCore"/> class.
    /// </summary>
    ~ThreadCore()
    {
        this.Dispose(false);
    }

    /// <summary>
    /// free managed/native resources.
    /// </summary>
    /// <param name="disposing">true: free managed resources.</param>
    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
            }

            base.Dispose(disposing);
        }
    }
    #endregion
}

/// <summary>
/// ThreadCoreGroup is a collection of ThreadCore objects and it's not associated with Thread/Task.
/// </summary>
public class ThreadCoreGroup : ThreadCoreBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadCoreGroup"/> class.<br/>
    /// </summary>
    /// <param name="parent">The parent of this thread/task.<br/>
    /// Specify <see langword="null"/> to be independent (does not receive a termination signal from parent).</param>
    public ThreadCoreGroup(ThreadCoreBase? parent)
    {
        this.Prepare(parent);
    }

    /// <inheritdoc/>
    public override bool IsRunning => !this.IsTerminated;
}

/// <summary>
/// <see cref="UnitCore"/> is a <see cref="ThreadCoreGroup"/> for unit, directly created from <see cref="ThreadCore.Root"/>.
/// </summary>
public class UnitCore : ThreadCoreGroup
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnitCore"/> class.<br/>
    /// </summary>
    public UnitCore()
        : base(ThreadCore.Root)
    {
    }
}

/// <summary>
/// Class for the root object.
/// </summary>
public class ThreadCoreRoot : ThreadCoreBase
{
    internal ThreadCoreRoot()
    {
        this.Prepare(null);
    }

    /// <summary>
    /// Gets a ManualResetEvent which can be used by application termination process.
    /// </summary>
    public ManualResetEvent TerminationEvent { get; } = new(false);
}

/// <summary>
/// Base class for thread/task.
/// </summary>
public class ThreadCoreBase : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadCoreBase"/> class.<br/>
    /// </summary>
    protected ThreadCoreBase()
    {
    }

    /// <summary>
    /// Adds this object to the tree structure and notifies it's ready to use.
    /// </summary>
    /// <param name="parent">The parent.</param>
    protected void Prepare(ThreadCoreBase? parent)
    {
        this.CancellationToken = this.cancellationTokenSource.Token;
        using (TreeLock.EnterScope())
        {
            if (++cleanupCount >= CleanupThreshold)
            {
                cleanupCount = 0;
                ThreadCore.Root?.Clean(out _);
            }

            this.parent = parent;
            if (parent != null)
            {
                parent.hashSet.Add(this);
                if (parent.IsTerminated)
                {
                    this.cancellationTokenSource.Cancel();
                }
            }
        }
    }

    /// <summary>
    /// Gets a <see cref="System.Threading.CancellationToken"/> which is used to terminate thread/task.
    /// </summary>
    public CancellationToken CancellationToken { get; private set; }

    /// <summary>
    /// Gets a value indicating whether this thread/task is independent<br/>
    /// (does not receive a termination signal from parent).
    /// </summary>
    public bool IsIndependent => this.parent == null;

    /// <summary>
    /// Gets a value indicating whether this thread/task is being terminated or has been terminated.<br/>
    /// This value is identical to <see cref="ThreadCoreBase.CancellationToken"/>.IsCancellationRequested.
    /// </summary>
    public bool IsTerminated => this.CancellationToken.IsCancellationRequested; // Volatile.Read(ref this.terminated);

    /// <summary>
    /// Gets a value indicating whether this thread/task is paused.
    /// </summary>
    public bool IsPaused => Volatile.Read(ref this.paused);

    /// <summary>
    /// Gets a value indicating whether the thread/task is running.<br/>
    /// <see langword="false"/>: The thread/task is either not started or is complete.<br/>
    /// Use <see cref="IsTerminated"/> property to determine if a termination signal has been send.
    /// </summary>
    public virtual bool IsRunning => true;

    /// <summary>
    /// Gets a value indicating whether the object is associated with Thread/Task.
    /// </summary>
    public virtual bool IsThreadOrTask => false;

    /// <summary>
    /// Change the parent <see cref="ThreadCoreBase"/>.
    /// </summary>
    /// <param name="newParent">The new parent.</param>
    /// <returns><see langword="true"/>: The parent is successfully changed.</returns>
    public bool ChangeParent(ThreadCoreBase? newParent)
    {
        using (TreeLock.EnterScope())
        {// checked
            if (newParent is not null && newParent.IsTerminated)
            {// Terminated
                return false;
            }

            if (this.parent is not null)
            {
                this.parent.hashSet.Remove(this);
            }

            this.parent = newParent;
            if (newParent is not null)
            {
                newParent.hashSet.Add(this);
            }

            return true;
        }
    }

    /// <summary>
    /// Starts the thread/task.
    /// </summary>
    /// <param name="includeChildren"><see langword="true" />: Start the child objects [the default is <see langword="false"/>].</param>
    public virtual void Start(bool includeChildren = false)
    {
        if (includeChildren)
        {
            this.StartChildren();
        }
    }

    /*public void LockTreeSync()
    {// Check deadlock
        using (TreeLock.EnterScope())
        {// checked
        }
    }*/

    /// <summary>
    /// Sends a termination signal (calls <see cref="CancellationTokenSource.Cancel()"/>) to the object and the children.
    /// </summary>
    public void Terminate()
    {
        if (this.IsTerminated)
        {
            return;
        }

        List<CancellationTokenSource>? ctsToCancel = null;
        using (TreeLock.EnterScope())
        {// checked
            TerminateCore(this);
        }

        if (ctsToCancel != null)
        {
            foreach (var x in ctsToCancel)
            {
                x.Cancel();
            }
        }

        void TerminateCore(ThreadCoreBase c)
        {
            // c.cancellationTokenSource.Cancel(); // Moved to the outside of lock statement due to the mysterious behavior.
            var array = c.hashSet.ToArray();
            foreach (var x in array.Where(a => !a.IsIndependent))
            {
                TerminateCore(x);
            }

            if (!c.cancellationTokenSource.IsCancellationRequested)
            {
                ctsToCancel ??= new();
                ctsToCancel.Add(c.cancellationTokenSource);
            }
        }
    }

    /// <summary>
    /// Waits for the termination of the thread/task.<br/>
    /// Note that you need to call <see cref="Terminate"/> to terminate the object from outside the thread/task.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait before termination, or -1 to wait indefinitely.</param>
    /// <returns><see langword="true"/>: The thread/task is terminated.</returns>
    public bool WaitForTermination(int millisecondsTimeout)
    {
        var sw = new Stopwatch();
        sw.Start();

        while (true)
        {
            using (TreeLock.EnterScope())
            {
                this.Clean(out var numberOfActiveObjects);

                if (numberOfActiveObjects == 0)
                {
                    if (!this.IsThreadOrTask || !this.IsRunning)
                    {// Not active (e.g. Root, ThreadCoreGroup) or not running.
                        return true;
                    }
                }
            }

            Thread.Sleep(ThreadCore.DefaultInterval);
            if (millisecondsTimeout >= 0 && sw.ElapsedMilliseconds >= millisecondsTimeout)
            {
                return false;
            }

            continue;
        }
    }

    /// <summary>
    /// Waits for the termination of the thread/task.<br/>
    /// Note that you need to call <see cref="Terminate"/> to terminate the object from outside the thread/task.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait before termination, or -1 to wait indefinitely.</param>
    /// <returns>A task that represents waiting for termination.</returns>
    public async Task<bool> WaitForTerminationAsync(int millisecondsTimeout)
    {
        var sw = new Stopwatch();
        sw.Start();

        while (true)
        {
            using (TreeLock.EnterScope())
            {
                this.Clean(out var numberOfActiveObjects);

                if (numberOfActiveObjects == 0)
                {
                    if (!this.IsThreadOrTask || !this.IsRunning)
                    {// Not active (e.g. Root, ThreadCoreGroup) or not running.
                        return true;
                    }
                }
            }

            await Task.Delay(ThreadCore.DefaultInterval).ConfigureAwait(false);
            if (millisecondsTimeout >= 0 && sw.ElapsedMilliseconds >= millisecondsTimeout)
            {
                return false;
            }

            continue;
        }
    }

    /// <summary>
    /// Suspends the current thread/task for the specified amount of time (<see cref="Task.Delay(int)"/>).
    /// </summary>
    /// <param name="millisecondsToWait">The number of milliseconds to wait.</param>
    /// <returns><see langword="true"/> if the time successfully elapsed, <see langword="false"/> if the thread/task is terminated.</returns>
    public Task<bool> Delay(int millisecondsToWait) => this.Delay(TimeSpan.FromMilliseconds(millisecondsToWait));

    /// <summary>
    /// Wait for the specified time (<see cref="Task.Delay(TimeSpan)"/>).
    /// </summary>
    /// <param name="timeToWait">The TimeSpan to wait.</param>
    /// <returns><see langword="true"/> if the time successfully elapsed, <see langword="false"/> if the thread/task is terminated.</returns>
    public async Task<bool> Delay(TimeSpan timeToWait)
    {
        try
        {
            await Task.Delay(timeToWait).WaitAsync(this.CancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Suspends the current thread/task for the specified amount of time (<see cref="Thread.Sleep(int)"/>).
    /// </summary>
    /// <param name="millisecondsToWait">The number of milliseconds to wait.</param>
    /// <param name="interval">The interval time to wait in milliseconds (<see cref="Thread.Sleep(int)"/>).</param>
    /// <returns><see langword="true"/> if the time successfully elapsed, <see langword="false"/> if the thread/task is terminated.</returns>
    public bool Sleep(int millisecondsToWait, int interval = ThreadCore.DefaultInterval) => this.Sleep(TimeSpan.FromMilliseconds(millisecondsToWait), TimeSpan.FromMilliseconds(interval));

    /// <summary>
    /// Wait for the specified time (<see cref="Thread.Sleep(TimeSpan)"/>).
    /// </summary>
    /// <param name="timeToWait">The TimeSpan to wait.</param>
    /// <param name="interval">The interval time to wait (<see cref="Thread.Sleep(TimeSpan)"/>).</param>
    /// <returns>true if the time successfully elapsed, false if the thread/task is terminated.</returns>
    public bool Sleep(TimeSpan timeToWait, TimeSpan interval)
    {
        timeToWait = timeToWait < TimeSpan.Zero ? TimeSpan.Zero : timeToWait;
        interval = interval > timeToWait ? timeToWait : interval;
        var end = Stopwatch.GetTimestamp() + (long)(timeToWait.TotalSeconds * (double)Stopwatch.Frequency);

        while (!this.IsTerminated)
        {
            if (Stopwatch.GetTimestamp() >= end)
            {
                return true;
            }

            try
            {
                var cancelled = this.CancellationToken.WaitHandle.WaitOne(interval);
                if (cancelled)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        return false; // terminated
    }

    /// <summary>
    /// Sends a pause signal (sets <see cref="ThreadCoreBase.paused"/> to true) to the object and the children.
    /// </summary>
    public void Pause()
    {
        using (TreeLock.EnterScope())
        {// checked
            PauseCore(this);
        }

        static void PauseCore(ThreadCoreBase c)
        {
            Volatile.Write(ref c.paused, true);
            foreach (var x in c.hashSet)
            {
                PauseCore(x);
            }
        }
    }

    /// <summary>
    /// Sends a resume signal (sets <see cref="ThreadCoreBase.paused"/> to false) to the object and the children.
    /// </summary>
    public void Resume()
    {
        using (TreeLock.EnterScope())
        {// checked
            ResumeCore(this);
        }

        static void ResumeCore(ThreadCoreBase c)
        {
            Volatile.Write(ref c.paused, false);
            foreach (var x in c.hashSet)
            {
                ResumeCore(x);
            }
        }
    }

    /// <summary>
    /// Gets the child objects of this thread/task.
    /// </summary>
    /// <returns>An array of child objects.</returns>
    public ThreadCoreBase[] GetChildren()
    {
        using (TreeLock.EnterScope())
        {// checked
            return this.hashSet.ToArray();
        }
    }

    /// <summary>
    /// Gets the parent object of this thread/task.
    /// </summary>
    /// <returns>The parent object.</returns>
    public ThreadCoreBase? GetParent() => this.parent;

    internal void Clean(out int numberOfActiveObjects)
    {// using (TreeLock.EnterScope()) required
        numberOfActiveObjects = 0;
        CleanCore(this, ref numberOfActiveObjects);

        static bool CleanCore(ThreadCoreBase c, ref int numberOfActiveObjects)
        {
            if (c.hashSet.Count > 0)
            {
                var array = c.hashSet.ToArray();
                foreach (var x in array)
                {
                    if (!CleanCore(x, ref numberOfActiveObjects))
                    {
                        // x.Dispose();

                        // Avoid deadlock.
                        if (x.IsTerminated)
                        {
                            if (x.parent != null)
                            {
                                x.parent.hashSet.Remove(x);
                                x.parent = null;
                            }
                        }
                    }
                    else if (x.IsThreadOrTask)
                    {// Active (associated with Thread/Task) object
                        numberOfActiveObjects++;
                    }
                }
            }

            return c.IsRunning == true;
        }
    }

    protected void StartChildren()
    {
        foreach (var x in this.GetChildren())
        {
            x.Start(true);
        }
    }

    protected const int CleanupThreshold = 16;

    private static Lock TreeLock { get; } = new();

    private static int cleanupCount = 0;

    private ThreadCoreBase? parent;
    private HashSet<ThreadCoreBase> hashSet = new();
    // private bool terminated = false;
    private CancellationTokenSource cancellationTokenSource = new();
    private bool paused = false;

    #region IDisposable Support
#pragma warning disable SA1202 // Elements should be ordered by access
#pragma warning disable SA1401 // Fields should be private
    protected bool disposed = false; // To detect redundant calls.
#pragma warning restore SA1401 // Fields should be private
#pragma warning restore SA1202 // Elements should be ordered by access

    /// <summary>
    /// Finalizes an instance of the <see cref="ThreadCoreBase"/> class.
    /// </summary>
    ~ThreadCoreBase()
    {
        this.Dispose(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// free managed/native resources.
    /// </summary>
    /// <param name="disposing">true: free managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                // free managed resources.
                if (!this.IsTerminated)
                {
                    this.Terminate();
                }

                using (TreeLock.EnterScope())
                {// checked
                    if (this.parent != null)
                    {
                        this.parent.hashSet.Remove(this);
                        this.parent = null;
                    }
                }
            }

            // free native resources here if there are any.
            this.disposed = true;
        }
    }
    #endregion
}
