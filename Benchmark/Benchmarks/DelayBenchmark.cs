// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;
using BenchmarkDotNet.Attributes;

namespace Benchmark;

[Config(typeof(BenchmarkConfig))]
public class DelayBenchmark
{
    // private const int MillisecondsToWait = 10;
    private readonly CancellationTokenSource Cts;
    private readonly CancellationTokenSource Cts2;
    private readonly CancellationToken CancellationToken;
    private readonly CancellationToken CancellationToken2;
    private readonly TimeSpan MillisecondsTimeSpan = TimeSpan.FromMilliseconds(10);

    public DelayBenchmark()
    {
        this.Cts = new();
        this.Cts2 = new();
        this.CancellationToken = this.Cts.Token;
        this.CancellationToken2 = this.Cts2.Token;

        var x = Delay6(MillisecondsTimeSpan, this.CancellationToken, this.CancellationToken2).Result;
    }

    [GlobalSetup]
    public void Setup()
    {
    }

    [Benchmark]
    public async Task Delay10()
    {
        await ThreadCore.Root.Delay(0, this.CancellationToken);
    }

    // [Benchmark]
    public async Task Delay10_Ct2()
    {
        await ThreadCore.Root.Delay2(MillisecondsTimeSpan, this.CancellationToken);
    }

    [Benchmark]
    public async Task Delay10_Ct4()
    {
        await Delay4(default, this.CancellationToken, this.CancellationToken2);
    }

    [Benchmark]
    public async Task Delay10_Ct5()
    {
        await Delay5(default, this.CancellationToken, this.CancellationToken2);
    }

    [Benchmark]
    public async Task Delay10_Ct6()
    {
        await Delay6(default, this.CancellationToken, this.CancellationToken2);
    }

    // [Benchmark]
    public async Task TaskDelay10_Ct()
    {
        await Task.Delay(MillisecondsTimeSpan, this.CancellationToken);
    }

    public static async Task<bool> Delay6(TimeSpan delay, CancellationToken cancellationToken1, CancellationToken cancellationToken2)
    {
        if (!cancellationToken2.CanBeCanceled)
        {
            try
            {
                await Task.Delay(delay, cancellationToken1).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        var cts = TaskHelper.CtsPool2.Rent();
        cts.Register(cancellationToken1, cancellationToken2);

        try
        {
            // await Task.Delay(delay, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            cts.Unregister();
            if (cts.TryReset())
            {
                TaskHelper.CtsPool2.Return(cts);
            }
        }
    }

    public static async Task<bool> Delay5(TimeSpan delay, CancellationToken cancellationToken1, CancellationToken cancellationToken2)
    {


        if (!cancellationToken2.CanBeCanceled)
        {
            try
            {
                await Task.Delay(delay, cancellationToken1).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        var linkedCts = TaskHelper.CtsPool.Rent();
        var registration1 = cancellationToken1.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), linkedCts);
        var registration2 = cancellationToken2.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), linkedCts);

        try
        {
            await Task.Delay(delay, linkedCts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            registration1.Dispose();
            registration2.Dispose();
            if (linkedCts.TryReset())
            {
                TaskHelper.CtsPool.Return(linkedCts);
            }
        }
    }

    public static async Task<bool> Delay4(TimeSpan delay, CancellationToken cancellationToken1, CancellationToken cancellationToken2)
    {
        try
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken1, cancellationToken2);
            await Task.Delay(delay, linkedCts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
