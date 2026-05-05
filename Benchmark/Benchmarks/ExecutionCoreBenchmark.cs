// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Arc.Threading;
using BenchmarkDotNet.Attributes;

namespace Benchmark;

[Config(typeof(BenchmarkConfig))]
public class ExecutionCoreBenchmark
{
    public ExecutionCoreBenchmark()
    {
    }

    [GlobalSetup]
    public void Setup()
    {
    }

    [Benchmark]
    public CancellationTokenSource CreateCts()
    {
        var cts = new CancellationTokenSource();
        return cts;
    }

    [Benchmark]
    public CancellationTokenSource CreateAndDisposeCts()
    {
        var cts = new CancellationTokenSource();
        cts.Dispose();
        return cts;
    }

    [Benchmark]
    public ExecutionCore CreateExecutionCore()
    {
        using var core = new ExecutionCore(Program.Root);
        return core;
    }
}
