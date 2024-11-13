// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;
using BenchmarkDotNet.Attributes;

namespace Benchmark;

[Config(typeof(BenchmarkConfig))]
public class LockBenchmarkSlim
{
    private object syncObject = new();
    private Lock lockObject = new();
    private Semaphore semaphore = new(1, 1);
    private SemaphoreSlim semaphoreSlim = new(1, 1);
    private SemaphoreLock semaphoreLock = new();

    public LockBenchmarkSlim()
    {
    }

    [GlobalSetup]
    public void Setup()
    {
    }

    [Benchmark]
    public void SemaphoreSlim_WaitRelease()
    {
        this.semaphoreSlim.Wait();
        this.semaphoreSlim.Release();
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
    public void SemaphoreLock_Using()
    {
        using (((ILockable)this.semaphoreLock).Lock())
        {
        }
    }
}
