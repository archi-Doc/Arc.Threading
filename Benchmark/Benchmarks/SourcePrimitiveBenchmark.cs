// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;
using BenchmarkDotNet.Attributes;

namespace Benchmark;

[Config(typeof(BenchmarkConfig))]
public class SourcePrimitiveBenchmark
{
    private static readonly AsyncLocal<uint> taskIdentifier = new();

    public SourcePrimitiveBenchmark()
    {
    }

    [GlobalSetup]
    public void Setup()
    {
    }

    /*[Benchmark]
    public TaskCompletionSource Create_TaskCompletionSource()
    {
        return new();
    }

    [Benchmark]
    public CancellationTokenSource Create_CancellationTokenSource()
    {
        return new();
    }

    [Benchmark]
    public SemaphoreLock Create_SemaphoreLock()
    {
        return new();
    }

    [Benchmark]
    public ReaderWriterLockSlim Create_ReaderWriterLockSlim()
    {
        return new();
    }*/

    [Benchmark]
    public uint Get_TaskIdentifier()
    {
        var id = Task.CurrentId;
        Task.Run(() =>
        {
            Console.WriteLine(Task.CurrentId);
        }).Wait();
        Console.WriteLine(Task.CurrentId);
        taskIdentifier.Value = 12345;
        return taskIdentifier.Value;
    }

    [Benchmark]
    public uint Get_ExecutionId()
        => this.GetExecutionId();

    private uint GetExecutionId()
    {
        if (Thread.CurrentThread.ExecutionContext is { } executionContext)
        {
            RuntimeHelpers.GetHashCode(executionContext);
            return (uint)Thread.CurrentThread.ExecutionContext.GetHashCode();
        }
        else
        {
            return 0;
        }
    }
    /*[Benchmark]
    public async Task<int> Test_TaskCompletionSource()
    {
        var tcs = new TaskCompletionSource<int>();
        _ = Task.Run(() => tcs.SetResult(42));
        return await tcs.Task;
    }

    [Benchmark]
    public async Task<int> Test_CancellationTokenSource()
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(() => cts.Cancel());
        try
        {
            await Task.Delay(1000, cts.Token);
        }
        catch
        {
        }

        return 42;
    }

    [Benchmark]
    public async Task<int> Test_CancellationTokenSource2()
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(() => cts.Cancel());

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        cts.Token.Register(static s => ((TaskCompletionSource<object?>)s!).TrySetResult(null), tcs);
        await tcs.Task;
        return 42;
    }*/
}
