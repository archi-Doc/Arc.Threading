// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Benchmark;

using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;
using BenchmarkDotNet.Attributes;

[Config(typeof(BenchmarkConfig))]
public class AsyncPulseEventBenchmark
{
    private AsyncPulseEvent ev = default!;
    private AsyncPulseEvent4 ev4 = default!;

    [GlobalSetup]
    public void Setup()
    {
        this.ev = new AsyncPulseEvent();
        this.ev4 = new AsyncPulseEvent4();
    }

    [Benchmark]
    public Task PulseThenWaitAsync()
    {
        this.ev.Pulse();
        return this.ev.WaitAsync();
    }

    [Benchmark]
    public Task PulseThenWaitAsync4()
    {
        this.ev4.Pulse();
        return this.ev4.WaitAsync();
    }

    [Benchmark]
    public Task WaitThenPulseAsync()
    {
        var task = this.ev.WaitAsync();
        this.ev.Pulse();
        return task;
    }

    [Benchmark]
    public Task WaitThenPulseAsync4()
    {
        var task = this.ev4.WaitAsync();
        this.ev4.Pulse();
        return task;
    }

    [Benchmark]
    public async Task WaitThenPulse_FromThreadPoolAsync()
    {
        var task = this.ev.WaitAsync();
        await Task.Run(() => this.ev.Pulse()).ConfigureAwait(false);
        await task.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task WaitThenPulse_FromThreadPoolAsync4()
    {
        var task = this.ev4.WaitAsync();
        await Task.Run(() => this.ev4.Pulse()).ConfigureAwait(false);
        await task.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task CancelThenReuseAsync()
    {
        using var cts = new CancellationTokenSource();
        var task = this.ev.WaitAsync(cts.Token);
        cts.Cancel();

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
        }

        var next = this.ev.WaitAsync();
        this.ev.Pulse();
        await next.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task CancelThenReuseAsync4()
    {
        using var cts = new CancellationTokenSource();
        var task = this.ev4.WaitAsync(cts.Token);
        cts.Cancel();

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
        }

        var next = this.ev4.WaitAsync();
        this.ev4.Pulse();
        await next.ConfigureAwait(false);
    }
}
