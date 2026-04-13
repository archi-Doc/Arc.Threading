// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;
using BenchmarkDotNet.Attributes;

namespace Benchmark;

internal record class TestReusableJob : ReusableThreadJob
{
    public int Id { get; private set; }

    public long Result { get; set; }

    public TestReusableJob()
    {
    }

    public TestReusableJob(int id)
    {
        this.Id = id;
    }

    public void Initialize(int id)
    {
        this.Id = id;
    }

    public override void Reset()
    {
    }
}

internal record class TestReusableJob2 : ReusableTaskJob
{
    public int Id { get; private set; }

    public long Result { get; set; }

    public TestReusableJob2()
    {
    }

    public void Initialize(int id)
    {
        this.Id = id;
    }
}

internal class TestReusableWorker : ReusableJobWorker<TestReusableJob>
{
    private int count;

    public TestReusableWorker(ThreadCoreBase? parent)
        : base(parent)
    {
    }

    protected override void ProcessJob(TestReusableJob job)
    {
        Interlocked.Increment(ref this.count);
    }
}

[Config(typeof(BenchmarkConfig))]
public class WorkerBenchmark
{
    private static int count;
    private readonly ThreadWorker<TestWork> threadWorker;
    private readonly TaskWorkerSlim<TestTaskWorkSlim> taskWorkerSlim;
    private readonly ReusableJobWorker<TestReusableJob> jobWorker;
    private readonly ReusableJobWorker<TestReusableJob2> jobWorker3;
    private readonly TestReusableWorker jobWorker2;

    public WorkerBenchmark()
    {
        this.threadWorker = new ThreadWorker<TestWork>(ThreadCore.Root, EmptyMethod2);
        this.taskWorkerSlim = new TaskWorkerSlim<TestTaskWorkSlim>(ThreadCore.Root, EmptyMethodTaskSlim);
        this.jobWorker = new(ThreadCore.Root, job => { Interlocked.Increment(ref WorkerBenchmark.count); });
        this.jobWorker2 = new(ThreadCore.Root);
        this.jobWorker3 = new(ThreadCore.Root, job => { Interlocked.Increment(ref WorkerBenchmark.count); });
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

    [Benchmark]
    public int ReusableJobWorker2()
    {
        var job = this.jobWorker2.Rent();
        job.Initialize(10);
        this.jobWorker2.Add(job);
        job.Wait();
        this.jobWorker2.Return(job);
        return job.Id;
    }

    [Benchmark]
    public async Task<int> ReusableJobWorker3()
    {
        var job = this.jobWorker3.Rent();
        job.Initialize(10);
        this.jobWorker3.Add(job);
        await job.Task;
        this.jobWorker3.Return(job);
        return job.Id;
    }

    /*[GlobalCleanup]
    public void Cleanup()
    {
        this.taskWorkerSlim.Dispose();
        this.threadWorker.Dispose();
        this.jobWorker.Dispose();
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

