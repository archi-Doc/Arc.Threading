// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
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
    }

    [GlobalSetup]
    public void Setup()
    {
    }

    [Benchmark]
    public void Delay10()
    {
        _ = ThreadCore.Root.Delay(MillisecondsTimeSpan, this.CancellationToken);
    }


    [Benchmark]
    public void Delay10_Ct2()
    {
        _ = ThreadCore.Root.Delay2(MillisecondsTimeSpan, this.CancellationToken);
    }
    [Benchmark]
    public void Delay10_Ct3()
    {
        _ = Delay3(MillisecondsTimeSpan, this.CancellationToken, this.CancellationToken2);
    }

    [Benchmark]
    public void Delay10_Ct4()
    {
        _ = Delay4(MillisecondsTimeSpan, this.CancellationToken, this.CancellationToken2);
    }

    [Benchmark]
    public void Delay10_Ct5()
    {
        _ = Delay5(MillisecondsTimeSpan, this.CancellationToken, this.CancellationToken2);
    }

    // [Benchmark]
    public void TaskDelay10_Ct()
    {
        _ = Task.Delay(MillisecondsTimeSpan, this.CancellationToken);
    }

    public static async Task<bool> Delay5(TimeSpan delay, CancellationToken cancellationToken1, CancellationToken cancellationToken2)
    {
        if (!cancellationToken2.CanBeCanceled)
        {
            try
            {
                _ = Task.Delay(delay, cancellationToken1).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        var linkedCts = CancellationTokenHelper.Pool.Rent();
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
                CancellationTokenHelper.Pool.Return(linkedCts);
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

    public static async Task<bool> Delay3(TimeSpan delay, CancellationToken cancellationToken1, CancellationToken cancellationToken2)
    {
        try
        {
            if (cancellationToken2.CanBeCanceled)
            {
                await Task.Delay(delay, cancellationToken1).WaitAsync(cancellationToken1);
            }
            else
            {
                await Task.Delay(delay, cancellationToken1);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
