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
using System.Threading.Tasks;
using Arc.Threading;
using BenchmarkDotNet.Attributes;

namespace Benchmark;

[Config(typeof(BenchmarkConfig))]
public class WorkerBenchmark
{
    private static int count;
    private readonly ThreadWorker<TestWork> threadWorker;
    private readonly TaskWorkerSlim<TestTaskWorkSlim> taskWorkerSlim;

    public WorkerBenchmark()
    {
        this.threadWorker = new ThreadWorker<TestWork>(ThreadCore.Root, EmptyMethod2);
        this.taskWorkerSlim = new TaskWorkerSlim<TestTaskWorkSlim>(ThreadCore.Root, EmptyMethodTaskSlim);
    }

    [Benchmark]
    public async Task<bool> TaskWorkerSlim()
    {
        var work = new TestTaskWorkSlim(1);
        this.taskWorkerSlim.Add(work);
        return await work.WaitForCompletionAsync();
    }

    [Benchmark]
    public bool ThreadWorker()
    {
        var work = new TestWork(2);
        this.threadWorker.Add(work);
        return work.Wait(1_000);
    }

    private static async Task<AbortOrComplete> EmptyMethodTaskSlim(TaskWorkerSlim<TestTaskWorkSlim> worker, TestTaskWorkSlim work)
    {
        Interlocked.Increment(ref WorkerBenchmark.count);
        return AbortOrComplete.Complete;
    }

    private static AbortOrComplete EmptyMethod2(ThreadWorker<TestWork> worker, TestWork work)
    {
        Interlocked.Increment(ref WorkerBenchmark.count);
        return AbortOrComplete.Complete;
    }
}
w
