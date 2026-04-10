// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;
using BenchmarkDotNet.Attributes;

namespace Benchmark;

internal class TestReusableJob : ReusableThreadJob
{
    public int Id { get; private set; }

    public long Result { get; set; }

    public TestReusableJob()
    {
    }

    public void Initialize(int id)
    {
        this.Id = id;
    }
}

[Config(typeof(BenchmarkConfig))]
public class WorkerBenchmark
{
    private static int count;
    private readonly ThreadWorker<TestWork> threadWorker;
    private readonly TaskWorkerSlim<TestTaskWorkSlim> taskWorkerSlim;
    private readonly ReusableJobWorker<TestReusableJob> jobWorker;

    public WorkerBenchmark()
    {
        this.threadWorker = new ThreadWorker<TestWork>(ThreadCore.Root, EmptyMethod2);
        this.taskWorkerSlim = new TaskWorkerSlim<TestTaskWorkSlim>(ThreadCore.Root, EmptyMethodTaskSlim);
        this.jobWorker = new(job => { });
    }

    /*[Benchmark]
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
    }*/

    [Benchmark]
    public int ReusableJobWorker()
    {
        var job = this.jobWorker.Rent();
        job.Initialize(10);
        this.jobWorker.Add(job);
        job.Wait();
        this.jobWorker.Return(job);
        return job.Id;
    }

    /*[Benchmark]
    public async Task<int> ReusableJobWorker()
    {
        var job = this.jobWorker.Rent();
        job.Initialize(10);
        this.jobWorker.Add(job);
        await job.Wait();
        this.jobWorker.Return(job);
        return job.Id;
    }*/

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

