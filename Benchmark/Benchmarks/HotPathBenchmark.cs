// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Benchmark;

using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;
using BenchmarkDotNet.Attributes;

[MemoryDiagnoser]
public class HotPathBenchmark
{
    private readonly AsyncPulseEvent pulse = new();
    private readonly CancellationTokenSource source = new();
    private readonly SemaphoreLock mutex = new();
    private readonly ExecutionRoot root = new();
    private TaskCompletionCore child = default!;

    [GlobalSetup]
    public void Setup() => this.child = new(this.root);

    [GlobalCleanup]
    public void Cleanup()
    {
        this.child.Dispose();
        this.root.Dispose();
        this.source.Dispose();
    }

    [Benchmark]
    public Task<bool> RetainedPulse()
    {
        this.pulse.Pulse();
        return this.pulse.WaitAsync();
    }

    [Benchmark]
    public Task<bool> PendingPulse()
    {
        var task = this.pulse.WaitAsync();
        this.pulse.Pulse();
        return task;
    }

    [Benchmark]
    public Task<bool> CancelablePulse()
    {
        var task = this.pulse.WaitAsync(this.source.Token);
        this.pulse.Pulse();
        return task;
    }

    [Benchmark]
    public Task<bool> TimedPulse()
    {
        var task = this.pulse.WaitAsync(60_000, this.source.Token);
        this.pulse.Pulse();
        return task;
    }

    [Benchmark]
    public ExecutionCore? FindChild() => this.root.FindChild(this.child.Id);

    [Benchmark]
    public bool UncontendedLock()
    {
        var entered = this.mutex.EnterAsync().GetAwaiter().GetResult();
        this.mutex.Exit();
        return entered;
    }
}
