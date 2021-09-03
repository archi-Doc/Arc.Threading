// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1124 // Do not use regions

namespace Arc.Threading
{
    /// <summary>
    /// Customized thread core class.
    /// </summary>
    public class CustomThreadCore : ThreadCore
    {
        public CustomThreadCore(ThreadCoreBase parent, Action<object?> method)
            : base(parent, method)
        {
        }

        public int CustomPropertyIfYouNeed { get; set; }
    }

    /// <summary>
    /// Support class for <see cref="System.Threading.Thread"/>.
    /// </summary>
    public class ThreadCore : ThreadCoreBase
    {
        /// <summary>
        /// Gets the root object of all ThreadCoreBase classes.
        /// </summary>
        public static ThreadCoreRoot Root { get; } = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="ThreadCore"/> class.
        /// </summary>
        /// <param name="parent">The parent.</param>
        /// <param name="method">The method that executes on a System.Threading.Thread.</param>
        /// <param name="startImmediately">Starts the thread immediately.<br/>
        /// <see langword="false"/>: Manually call <see cref="Start"/> to start the thread.</param>
        public ThreadCore(ThreadCoreBase parent, Action<object?> method, bool startImmediately = true)
        {
            this.Thread = new Thread(new ParameterizedThreadStart(method));
            this.Prepare(parent); // this.Thread (this.IsAlive) might be referenced after this method.
            if (startImmediately)
            {
                this.Start();
            }
        }

        /// <inheritdoc/>
        public override void Start()
        {
            this.Thread.Start(this);
        }

        /// <inheritdoc/>
        public override bool IsAlive => (this.Thread.ThreadState & System.Threading.ThreadState.Stopped) == 0; // this.Thread.IsAlive;

        /// <inheritdoc/>
        public override bool IsThreadOrTask => true;

        /// <summary>
        /// Gets an instance of <see cref="System.Threading.Thread"/>.
        /// </summary>
        public Thread Thread { get; }

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
    /// Support class for <see cref="System.Threading.Tasks.Task"/>.
    /// </summary>
    public class TaskCore : ThreadCoreBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TaskCore"/> class.<br/>
        /// method: async <see cref="System.Threading.Tasks.Task"/> Method(<see cref="object"/>? parameter).
        /// </summary>
        /// <param name="parent">The parent.</param>
        /// <param name="method">The method that executes on a <see cref="System.Threading.Tasks.Task"/>.</param>
        /// <param name="startImmediately">Starts the task immediately.<br/>
        /// <see langword="false"/>: Manually call <see cref="Start"/> to start the task.</param>
        public TaskCore(ThreadCoreBase parent, Func<object?, Task> method, bool startImmediately = true)
        {
            // this.Task = System.Threading.Tasks.Task.Run(async () => { await method(this); });
            // this.Task = System.Threading.Tasks.Task.Factory.StartNew(async () => { await method(this); }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();

            this.Task = new Task(() => method(this).Wait());
            this.Prepare(parent); // this.Task (this.IsAlive) might be referenced after this method.
            if (startImmediately)
            {
                this.Start();
            }
        }

        /// <inheritdoc/>
        public override void Start()
        {
            this.Task.Start();
        }

        /// <inheritdoc/>
        public override bool IsAlive => !this.Task.IsCompleted; // this.Task.Status != TaskStatus.RanToCompletion && this.Task.Status != TaskStatus.Canceled && this.Task.Status != TaskStatus.Faulted;

        /// <inheritdoc/>
        public override bool IsThreadOrTask => true;

        /// <summary>
        /// Gets an instance of <see cref="System.Threading.Tasks.Task"/>.
        /// </summary>
        public Task Task { get; }

        #region IDisposable Support

        /// <summary>
        /// Finalizes an instance of the <see cref="TaskCore"/> class.
        /// </summary>
        ~TaskCore()
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
                    // this.Task.Dispose(); // Not necessary
                }

                base.Dispose(disposing);
            }
        }
        #endregion
    }

    /// <summary>
    /// Support class for <see cref="System.Threading.Tasks.Task{TResult}"/>.<br/>
    /// </summary>
    /// <typeparam name="TResult">The type of the result produced by this task.</typeparam>
    public class TaskCore<TResult> : ThreadCoreBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TaskCore{TResult}"/> class.<br/>
        /// method: async <see cref="System.Threading.Tasks.Task{TResult}"/> Method(<see cref="object"/>? parameter).
        /// </summary>
        /// <param name="parent">The parent.</param>
        /// <param name="method">The method that executes on a <see cref="System.Threading.Tasks.Task{TResult}"/>.</param>
        /// <param name="startImmediately">Starts the task immediately.<br/>
        /// <see langword="false"/>: Manually call <see cref="Start"/> to start the task.</param>
        public TaskCore(ThreadCoreBase parent, Func<object?, Task<TResult>> method, bool startImmediately = true)
        {
            this.Task = new Task<TResult>(() => method(this).Result);
            this.Prepare(parent); // this.Task (this.IsAlive) might be referenced after this method.
            if (startImmediately)
            {
                this.Start();
            }
        }

        /// <inheritdoc/>
        public override void Start()
        {
            this.Task.Start();
        }

        /// <inheritdoc/>
        public override bool IsAlive => !this.Task.IsCompleted;

        /// <inheritdoc/>
        public override bool IsThreadOrTask => true;

        /// <summary>
        /// Gets an instance of <see cref="System.Threading.Tasks.Task{TResult}"/>.
        /// </summary>
        public Task<TResult> Task { get; }

        #region IDisposable Support

        /// <summary>
        /// Finalizes an instance of the <see cref="TaskCore{T}"/> class.
        /// </summary>
        ~TaskCore()
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
                    // this.Task.Dispose(); // Not necessary
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
        /// <param name="parent">The parent.</param>
        public ThreadCoreGroup(ThreadCoreBase parent)
        {
            this.Prepare(parent);
        }

        /// <inheritdoc/>
        public override bool IsAlive => !this.IsTerminated;
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
            lock (TreeSync)
            {
                if (++cleanupCount >= CleanupThreshold)
                {
                    cleanupCount = 0;
                    ThreadCore.Root?.Clean(out _);
                }

                this.parent = parent;
                if (parent != null && !parent.IsTerminated)
                {
                    parent.hashSet.Add(this);
                }
            }
        }

        /// <summary>
        /// Gets a <see cref="System.Threading.CancellationToken"/> which is used to terminate thread/task.
        /// </summary>
        public CancellationToken CancellationToken { get; private set; }

        /// <summary>
        /// Gets a value indicating whether this thread/task is being terminated or has been terminated.<br/>
        /// This value is identical to this.<see cref="CancellationToken.IsCancellationRequested"/>.
        /// </summary>
        public bool IsTerminated => this.CancellationToken.IsCancellationRequested; // Volatile.Read(ref this.terminated);

        /// <summary>
        /// Gets a value indicating whether this thread/task is paused.
        /// </summary>
        public bool IsPaused => Volatile.Read(ref this.paused);

        /// <summary>
        /// Gets a value indicating whether the thread/task is running.<br/>
        /// <see langword="false"/>: The thread/task is completed.<br/>
        /// Use <see cref="IsTerminated"/> property to determine if a termination signal has been send.
        /// </summary>
        public virtual bool IsAlive => true;

        /// <summary>
        /// Gets a value indicating whether the object is associated with Thread/Task.
        /// </summary>
        public virtual bool IsThreadOrTask => false;

        /// <summary>
        /// Starts the thread/task.
        /// </summary>
        public virtual void Start()
        {
        }

        /// <summary>
        /// Sends a termination signal (calls <see cref="CancellationTokenSource.Cancel()"/>) to the object and the children.
        /// </summary>
        public void Terminate()
        {
            lock (TreeSync)
            {
                TerminateCore(this);
            }

            static void TerminateCore(ThreadCoreBase c)
            {
                // c.cancellationTokenSource.Cancel(); // Moved to the end of the method due to mysterious behavior.
                var array = c.hashSet.ToArray();
                foreach (var x in array)
                {
                    TerminateCore(x);
                }

                c.cancellationTokenSource.Cancel();
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
            int interval = 5;
            var sw = new Stopwatch();
            sw.Start();

            while (true)
            {
                lock (TreeSync)
                {
                    this.Clean(out var numberOfActiveObjects);

                    if (numberOfActiveObjects == 0)
                    {
                        if (!this.IsThreadOrTask || !this.IsAlive)
                        {// Not active (e.g. Root, ThreadCoreGroup) or not running.
                            return true;
                        }
                    }
                }

                Thread.Sleep(interval);
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
        public async Task<bool> WaitAsyncForTermination(int millisecondsTimeout)
        {
            int interval = 5;
            var sw = new Stopwatch();
            sw.Start();

            while (true)
            {
                lock (TreeSync)
                {
                    this.Clean(out var numberOfActiveObjects);

                    if (numberOfActiveObjects == 0)
                    {
                        if (!this.IsThreadOrTask || !this.IsAlive)
                        {// Not active (e.g. Root, ThreadCoreGroup) or not running.
                            return true;
                        }
                    }
                }

                await Task.Delay(interval);
                if (millisecondsTimeout >= 0 && sw.ElapsedMilliseconds >= millisecondsTimeout)
                {
                    return false;
                }

                continue;
            }
        }

        /// <summary>
        /// Wait for the specified time (<see cref="Thread.Sleep(int)"/>).
        /// </summary>
        /// <param name="millisecondsToWait">The number of milliseconds to wait.</param>
        /// <param name="interval">The interval time to wait in milliseconds (<see cref="Thread.Sleep(int)"/>).</param>
        /// <returns>true if the time successfully elapsed, false if the thread/task is terminated.</returns>
        public bool Wait(int millisecondsToWait, int interval) => this.Wait(TimeSpan.FromMilliseconds(millisecondsToWait), TimeSpan.FromMilliseconds(interval));

        /// <summary>
        /// Wait for the specified time (<see cref="Thread.Sleep(TimeSpan)"/>).
        /// </summary>
        /// <param name="timeToWait">The TimeSpan to wait.</param>
        /// <param name="interval">The interval time to wait (<see cref="Thread.Sleep(TimeSpan)"/>).</param>
        /// <returns>true if the time successfully elapsed, false if the thread/task is terminated.</returns>
        public bool Wait(TimeSpan timeToWait, TimeSpan interval)
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

                Thread.Sleep(interval);
            }

            return false; // terminated
        }

        /// <summary>
        /// Sends a pause signal (sets <see cref="ThreadCoreBase.paused"/> to true) to the object and the children.
        /// </summary>
        public void Pause()
        {
            lock (TreeSync)
            {
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
            lock (TreeSync)
            {
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
            lock (TreeSync)
            {
                return this.hashSet.ToArray();
            }
        }

        internal void Clean(out int numberOfActiveObjects)
        {// lock(TreeSync) required
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
                            x.Dispose();
                        }
                        else if (x.IsThreadOrTask)
                        {// Active (associated with Thread/Task) object
                            numberOfActiveObjects++;
                        }
                    }
                }

                return c.IsAlive == true;
            }
        }

        protected const int CleanupThreshold = 16;

        private static object TreeSync { get; } = new object();

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

                    lock (TreeSync)
                    {
                        if (this.parent != null)
                        {
                            this.parent.hashSet.Remove(this);
                        }
                    }
                }

                // free native resources here if there are any.
                this.disposed = true;
            }
        }
        #endregion
    }
}
