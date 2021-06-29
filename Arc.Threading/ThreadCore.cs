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
        /// Gets the root (application) object of all ThreadCoreBase classes.
        /// </summary>
        public static ThreadCoreRoot Root { get; } = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="ThreadCore"/> class.
        /// </summary>
        /// <param name="parent">The parent.</param>
        /// <param name="method">The method that executes on a System.Threading.Thread.</param>
        public ThreadCore(ThreadCoreBase parent, Action<object?> method)
            : base(parent)
        {
            this.Thread = new Thread(new ParameterizedThreadStart(method));
            this.Thread.Start(this);
        }

        /// <inheritdoc/>
        public override bool IsAlive => this.Thread.IsAlive;

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
        public TaskCore(ThreadCoreBase parent, Func<object?, Task> method)
            : base(parent)
        {
            // this.Task = System.Threading.Tasks.Task.Run(async () => { await method(this); });
            // this.Task = System.Threading.Tasks.Task.Factory.StartNew(async () => { await method(this); }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();

            this.Task = new Task(() => method(this).Wait());
            this.Task.Start();
        }

        /*public override void Start()
        {
            this.Task.Start();
        }*/

        /// <inheritdoc/>
        public override bool IsAlive => !this.Task.IsCompleted; // this.Task.Status != TaskStatus.RanToCompletion && this.Task.Status != TaskStatus.Canceled && this.Task.Status != TaskStatus.Faulted;

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
    /// method: async <see cref="System.Threading.Tasks.Task{TResult}"/> Method(<see cref="object"/>? parameter).
    /// </summary>
    /// <typeparam name="TResult">The type of the result produced by this task.</typeparam>
    public class TaskCore<TResult> : ThreadCoreBase
    {
        public TaskCore(ThreadCoreBase parent, Func<object?, Task<TResult>> method)
            : base(parent)
        {
            this.Task = new Task<TResult>(() => method(this).Result);
            this.Task.Start();
        }

        /*public override void Start()
        {
            this.Task.Start();
        }*/

        /// <inheritdoc/>
        public override bool IsAlive => !this.Task.IsCompleted;

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
    /// Class for the root object.
    /// </summary>
    public class ThreadCoreRoot : ThreadCoreBase
    {
        internal ThreadCoreRoot()
            : base(null)
        {
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
        /// Initializes a new instance of the <see cref="ThreadCoreBase"/> class.
        /// </summary>
        /// <param name="parent">The parent.</param>
        internal ThreadCoreBase(ThreadCoreBase? parent)
        {
            this.CancellationToken = this.cancellationTokenSource.Token;
            lock (TreeSync)
            {
                if (++cleanupCount >= CleanupThreshold)
                {
                    cleanupCount = 0;
                    ThreadCore.Root?.Clean();
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
        public CancellationToken CancellationToken { get; }

        /// <summary>
        /// Gets a value indicating whether this thread/task is terminated (or under termination process).<br/>
        /// This value is identical to this.<see cref="CancellationToken.IsCancellationRequested"/>.
        /// </summary>
        public bool IsTerminated => this.CancellationToken.IsCancellationRequested; // Volatile.Read(ref this.terminated);

        /// <summary>
        /// Gets a value indicating whether this thread/task is paused.
        /// </summary>
        public bool IsPaused => Volatile.Read(ref this.paused);

        /// <summary>
        /// Gets a value indicating whether the thread/task is running.
        /// </summary>
        public virtual bool IsAlive => true;

        /*public virtual void Start()
        {
        }*/

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
                // c.terminated = true;
                c.cancellationTokenSource.Cancel();
                foreach (var x in c.hashSet)
                {
                    TerminateCore(x);
                }
            }
        }

        /// <summary>
        /// Wait for the termination of the thread/task.<br/>
        /// Note that you need to call <see cref="Terminate"/> to terminate the object from outside the thread/task.
        /// </summary>
        /// <param name="millisecondTimeout">The number of milliseconds to wait before termination, or -1 to wait indefinitely.</param>
        /// <returns>A task that represents waiting for termination.</returns>
        public async Task<bool> WaitForTermination(int millisecondTimeout)
        {
            int interval = 5;
            var sw = new Stopwatch();
            sw.Start();

            var c = ThreadCore.Root;
            while (true)
            {
                lock (TreeSync)
                {
                    var array = c.hashSet.ToArray();
                    foreach (var x in array)
                    {
                        if (!x.IsAlive)
                        {
                            x.Dispose();
                        }
                    }

                    if (c.hashSet.Count == 0)
                    {
                        if (c.parent == null || !c.IsAlive)
                        {// Root or not running
                            return true;
                        }
                    }
                }

                await Task.Delay(interval);
                if (millisecondTimeout >= 0 && sw.ElapsedMilliseconds >= millisecondTimeout)
                {
                    return false;
                }

                continue;
            }
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

        internal void Clean()
        {// lock(TreeSync) required
            CleanCore(this);

            static bool CleanCore(ThreadCoreBase c)
            {
                if (c.hashSet.Count > 0)
                {
                    var array = c.hashSet.ToArray();
                    foreach (var x in array)
                    {
                        if (!CleanCore(x))
                        {
                            x.Dispose();
                        }
                    }
                }

                return c.IsAlive;
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
