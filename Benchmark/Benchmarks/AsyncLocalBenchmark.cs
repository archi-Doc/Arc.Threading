﻿// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;
using Arc.Threading;
using BenchmarkDotNet.Attributes;

namespace Benchmark;

[Config(typeof(BenchmarkConfig))]
public class AsyncLocalBenchmark
{
    public static readonly AsyncLocal<int> AsyncLocalInstance = new();

    public AsyncLocalBenchmark()
    {
    }

    [GlobalSetup]
    public void Setup()
    {
    }

    [Benchmark]
    public void SetValue()
    {
        AsyncLocalInstance.Value = 2;
    }

    [Benchmark]
    public int GetValue()
    {
        return AsyncLocalInstance.Value;
    }

    [Benchmark]
    public int UpdateValue()
    {
        var v = AsyncLocalInstance.Value;
        if (v == 0)
        {
            AsyncLocalInstance.Value = 2;
            v = 2;
        }

        return v;
    }

    [Benchmark]
    public long GetExecutionId()
        => ExecutionId.Get();

    // [Benchmark]
    public ExecutionContext? GetExecutionContext()
        => System.Threading.Thread.CurrentThread.ExecutionContext;
}
