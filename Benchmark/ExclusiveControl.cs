using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;
using Nito.AsyncEx;

namespace Benchmark;

internal static class ExclusiveControl
{
    private const int Concurrency = 4;
    private const int Repetition = 5;
    private const int N = 200_000;
    private readonly static object syncObject = new();
    private readonly static Lock lockObject = new();
    private readonly static SemaphoreSlim semaphoreSlim = new(1, 1);
    private readonly static SemaphoreLock semaphoreLock = new();
    private readonly static ReaderWriterLockSlim readerWriterLockSlim = new();
    private readonly static AsyncLock asyncLock = new();

    private record class Benchmark(string Name, Action EnterDelegate, Action ExitDelegate)
    {
        private int count;

        public async Task Run()
        {
            var benchTimer = new BenchTimer();

            for (var i = 0; i < Repetition; i++)
            {
                this.count = 0;
                var tasks = new Task[Concurrency];
                for (var t = 0; t < Concurrency; t++)
                {
                    tasks[t] = Task.Run(async () =>
                    {
                        for (var n = 0; n < N; n++)
                        {
                            EnterDelegate();
                            this.count++;
                            ExitDelegate();
                        }
                    });
                }

                benchTimer.Start();

                await Task.WhenAll(tasks);

                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult($"{this.Name}({this.count})"));
        }
    }

    private record class Benchmark2(string Name, Func<Task> EnterDelegate, Action ExitDelegate)
    {
        private int count;

        public async Task Run()
        {
            var benchTimer = new BenchTimer();

            for (var i = 0; i < Repetition; i++)
            {
                this.count = 0;
                var tasks = new Task[Concurrency];
                for (var t = 0; t < Concurrency; t++)
                {
                    tasks[t] = Task.Run(async () =>
                    {
                        for (var n = 0; n < N; n++)
                        {
                            await EnterDelegate();
                            this.count++;
                            ExitDelegate();
                        }
                    });
                }

                benchTimer.Start();

                await Task.WhenAll(tasks);

                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult($"{this.Name}({this.count})"));
        }
    }

    private record class Benchmark3(string Name, Func<AwaitableDisposable<IDisposable>> LockDisposable)
    {
        private int count;

        public async Task Run()
        {
            var benchTimer = new BenchTimer();

            for (var i = 0; i < Repetition; i++)
            {
                this.count = 0;
                var tasks = new Task[Concurrency];
                for (var t = 0; t < Concurrency; t++)
                {
                    tasks[t] = Task.Run(async () =>
                    {
                        for (var n = 0; n < N; n++)
                        {
                            using (await LockDisposable())
                            {
                                this.count++;
                            }
                        }
                    });
                }

                benchTimer.Start();

                await Task.WhenAll(tasks);

                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult($"{this.Name}({this.count})"));
        }
    }

    static ExclusiveControl()
    {
    }

    public static async Task Test()
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;
        int timeoutInMilliseconds = 10_000;
        var timeout = TimeSpan.FromSeconds(10);

        var objectBenchmark = new Benchmark("object", () => Monitor.Enter(syncObject), () => Monitor.Exit(syncObject));
        // var objectBenchmark2 = new Benchmark2("object", () => { Monitor.Enter(syncObject); return Task.CompletedTask; }, () => Monitor.Exit(syncObject));
        var lockBenchmark = new Benchmark("Lock", () => lockObject.Enter(), () => lockObject.Exit());
        var semaphoreSlimBenchmark = new Benchmark2("SemaphoreSlim", () => semaphoreSlim.WaitAsync(), () => semaphoreSlim.Release());
        var semaphoreSlimBenchmark2 = new Benchmark2("SemaphoreSlim2", () => semaphoreSlim.WaitAsync(timeoutInMilliseconds, cancellationToken), () => semaphoreSlim.Release());
        var semaphoreLockBenchmark = new Benchmark2("SemaphoreLock", () => semaphoreLock.EnterAsync(), () => semaphoreLock.Exit());
        var semaphoreLockBenchmark2 = new Benchmark2("SemaphoreLock", () => semaphoreLock.EnterAsync(timeout, cancellationToken), () => semaphoreLock.Exit());
        var readerWriterLockSlimBenchmark = new Benchmark("ReaderWriterLockSlim", () => readerWriterLockSlim.EnterWriteLock(), () => readerWriterLockSlim.ExitWriteLock());
        var asyncLockBenchmark = new Benchmark3("AsyncLock", () => asyncLock.LockAsync());

        await objectBenchmark.Run();
        // await objectBenchmark2.Run();
        await lockBenchmark.Run();
        await semaphoreSlimBenchmark.Run();
        await semaphoreLockBenchmark.Run();
        await readerWriterLockSlimBenchmark.Run();
        await semaphoreSlimBenchmark2.Run();
        await semaphoreLockBenchmark2.Run();
    }
}
