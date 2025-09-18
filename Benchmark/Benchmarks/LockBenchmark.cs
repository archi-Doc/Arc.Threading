// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;
using BenchmarkDotNet.Attributes;

namespace Benchmark;

[Config(typeof(BenchmarkConfig))]
public class LockBenchmark
{
    private object syncObject = new();
    private Lock lockObject = new();
    private Semaphore semaphore = new(1, 1);
    private SemaphoreSlim semaphoreSlim = new(1, 1);
    private SemaphoreLock semaphoreLock = new();
    private SemaphoreLock2 semaphoreLock2 = new();

    public LockBenchmark()
    {
    }

    [GlobalSetup]
    public void Setup()
    {
    }

    [Benchmark]
    public object NewObject() => new object();

    [Benchmark]
    public Lock NewLock() => new Lock();

    [Benchmark]
    public SemaphoreLock NewSemaphoreLock() => new SemaphoreLock();

    [Benchmark]
    public SemaphoreLock2 NewSemaphoreLock2() => new SemaphoreLock2();

    [Benchmark]
    public SemaphoreSlim NewSemaphoreSlim() => new SemaphoreSlim(1, 1);

    [Benchmark]
    public void Lock()
    {
        lock (this.syncObject)
        {
        }
    }

    [Benchmark]
    public void LockObject()
    {
        using (this.lockObject.EnterScope())
        {
        }
    }

    // [Benchmark]
    public void Monitor_EnterExit()
    {
        var lockTaken = false;
        try
        {
            Monitor.Enter(this.syncObject, ref lockTaken);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(this.syncObject);
            }
        }
    }

    // [Benchmark]
    public void Semaphore_WaitRelease()
    {
        try
        {
            this.semaphore.WaitOne();
        }
        finally
        {
            this.semaphore.Release();
        }
    }

    [Benchmark]
    public void SemaphoreSlim_WaitRelease()
    {
        try
        {
            this.semaphoreSlim.Wait(); // Wait(Timeout.Infinite, CancellationToken.None);
        }
        finally
        {
            this.semaphoreSlim.Release();
        }
    }

    [Benchmark]
    public void SemaphoreLock_EnterExit()
    {
        var lockTaken = false;
        try
        {
            lockTaken = this.semaphoreLock.Enter();
        }
        finally
        {
            if (lockTaken)
            {
                this.semaphoreLock.Exit();
            }
        }
    }

    [Benchmark]
    public void SemaphoreLock2_EnterExit()
    {
        var lockTaken = false;
        try
        {
            lockTaken = this.semaphoreLock2.Enter();
        }
        finally
        {
            if (lockTaken)
            {
                this.semaphoreLock2.Exit();
            }
        }
    }

    // [Benchmark]
    public void SemaphoreLock_Using()
    {
        using (((ILockable)this.semaphoreLock).EnterScope())
        {
        }
    }

    // [Benchmark]
    public void SemaphoreSlim_WaitRelease2()
    {
        var lockTaken = false;
        try
        {
            lockTaken = this.semaphoreSlim.Wait(Timeout.Infinite, CancellationToken.None);
        }
        finally
        {
            if (lockTaken)
            {
                this.semaphoreSlim.Release();
            }
        }
    }

    [Benchmark]
    public async Task SemaphoreSlim_WaitAsync()
    {
        try
        {
            await this.semaphoreSlim.WaitAsync().ConfigureAwait(false); // Wait(Timeout.Infinite, CancellationToken.None);
        }
        finally
        {
            this.semaphoreSlim.Release();
        }
    }

    // [Benchmark]
    public async Task SemaphoreLock_UsingAsync()
    {
        using (await ((IAsyncLockable)this.semaphoreLock).EnterScopeAsync().ConfigureAwait(false))
        {
        }
    }

    [Benchmark]
    public async Task SemaphoreLock_EnterAsync()
    {
        var lockTaken = false;
        try
        {
            lockTaken = await this.semaphoreLock.EnterAsync();
        }
        finally
        {
            if (lockTaken)
            {
                this.semaphoreLock.Exit();
            }
        }
    }

    [Benchmark]
    public async Task SemaphoreLock2_EnterAsync()
    {
        var lockTaken = false;
        try
        {
            lockTaken = await this.semaphoreLock2.EnterAsync();
        }
        finally
        {
            if (lockTaken)
            {
                this.semaphoreLock2.Exit();
            }
        }
    }
}
