// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Diagnostics;
using Arc;
using Arc.Threading;

namespace QuickStart;

internal class TestLock
{
    public const int N = 1_00_000;
    public const int Concurrency = 10;

    public int x;

    public SemaphoreSlim semaphoreSlim = new(1, 1);
    public SemaphoreLock semaphoreLock = new();
    // public SemaphoreDual semaphoreDual = new(1_000);

    public async Task Run(string name, Action<TestLock> action)
    {
        this.x = 0;
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(1, Concurrency).Select(async _ =>
        {
            await Task.Delay(1).ConfigureAwait(false);
            for (int i = 0; i < N; ++i)
            {
                action(this);
            }
        }).ToArray();

        Task.WaitAll(tasks);
        Console.WriteLine($"{name}: {sw.ElapsedMilliseconds} ms, {this.x}");
        this.x = 0;
    }

    public async Task Run(string name, Func<TestLock, Task> action)
    {
        this.x = 0;
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(1, Concurrency).Select(async _ =>
        {
            await Task.Delay(1).ConfigureAwait(false);
            for (int i = 0; i < N; ++i)
            {
                await action(this);
            }
        }).ToArray();

        Task.WaitAll(tasks);
        Console.WriteLine($"{name}: {sw.ElapsedMilliseconds} ms, {this.x}");
        this.x = 0;
    }

    public async Task Run(string name, Action<TestLock> action, Func<TestLock, Task> action2)
    {
        this.x = 0;
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(1, Concurrency).Select(async _ =>
        {
            await Task.Delay(1).ConfigureAwait(false);
            for (int i = 0; i < N / 2; ++i)
            {
                action(this);
            }
        });

        var tasks2 = Enumerable.Range(1, Concurrency).Select(async _ =>
        {
            await Task.Delay(1).ConfigureAwait(false);
            for (int i = 0; i < N / 2; ++i)
            {
                await action2(this);
            }
        });

        Task.WaitAll(tasks.Concat(tasks2).ToArray());
        Console.WriteLine($"{name}: {sw.ElapsedMilliseconds} ms, {this.x}");
        this.x = 0;
    }
}

internal class Program
{
    public static ExecutionRoot Root { get; } = new();

    public static async Task Main(string[] args)
    {
        AppCloseHandler.Set(() =>
        {// Closing the console window or terminating the process.
            Root.RequestTermination(); // Send a termination signal to the root.
            Root.WaitForTermination(TimeSpan.FromSeconds(2)).Wait();
        });

        Console.CancelKeyPress += (s, e) =>
        {// Ctrl+C pressed.
            e.Cancel = true;
            Root.RequestTermination(); // Send a termination signal to the root.
        };

        Console.WriteLine(EstimateSize.Class<SemaphoreLock>());

        // await TestSemaphoreDual();
        // await TestLock();
        // await TestThreadCore_Termination();
        // await TestAsyncPulseEvent();
        // await TestExecutionStack();

        await Root.WaitForTermination(-1); // Wait for the termination infinitely.
    }

    /*private static async Task TestSemaphoreDual()
    {
        var semaphore = new SemaphoreDual(1_000);
        var time = await semaphore.Enter1Async();
        await Console.Out.WriteLineAsync("Enter1");

        await Console.Out.WriteLineAsync($"Enter2: {semaphore.Enter2(time).ToString()}");
        await Console.Out.WriteLineAsync($"Exit2: {semaphore.Exit2(time).ToString()}");

        var time2 = await semaphore.Enter1Async();
        await Console.Out.WriteLineAsync("Enter1");

        semaphore.Exit1(time);
    }*/

    private static async Task TestExecutionStack()
    {
        /*var executionStack = new ExecutionStack();

        executionStack.Peek();
        using (var scope = executionStack.Add())
        {
            executionStack.CancelTop();
            executionStack.CancelTop();
        }*/
    }

    private static async Task TestLock()
    {
        var lockObject = new Lock();
        var obj = new object();
        Monitor.Enter(obj);
        // await Task.Delay(1000); // Error
        Monitor.Exit(obj);

        var semaphore = new SemaphoreLock();
        semaphore.Enter();
        await Task.Delay(100);
        semaphore.Exit();

        using (semaphore.EnterScope())
        {
            await Task.Delay(100);
        }

        var testLock = new TestLock();

        await testLock.Run("Simple", test =>
        {
            test.x++;
        });

        await testLock.Run("Interlocked", test =>
        {
            Interlocked.Increment(ref test.x);
        });

        await testLock.Run("lock", test =>
        {
            lock (test)
            {
                test.x++;
            }
        });

        await testLock.Run("Lock", test =>
        {
            using (lockObject.EnterScope())
            {
                test.x++;
            }
        });

        await testLock.Run("SemaphoreSlim", test =>
        {
            try
            {
                test.semaphoreSlim.Wait();
                test.x++;
            }
            finally
            {
                test.semaphoreSlim.Release();
            }
        });

        await testLock.Run("SemaphoreLock", test =>
        {
            try
            {
                test.semaphoreLock.Enter();
                test.x++;
            }
            finally
            {
                test.semaphoreLock.Exit();
            }
        });

        await testLock.Run("SemaphoreLock.Lock()", test =>
        {
            using (test.semaphoreLock.EnterScope())
            {
                test.x++;
            }
        });

        await testLock.Run("SemaphoreSlim Async", async test =>
        {
            try
            {
                await test.semaphoreSlim.WaitAsync();
                test.x++;
            }
            finally
            {
                test.semaphoreSlim.Release();
            }
        });

        await testLock.Run("SemaphoreLock Async", async test =>
        {
            try
            {
                await test.semaphoreLock.EnterAsync();
                test.x++;
            }
            finally
            {
                test.semaphoreLock.Exit();
            }
        });

        await testLock.Run("SemaphoreSlim Sync+Async",
            test =>
            {
                try
                {
                    test.semaphoreSlim.Wait();
                    test.x++;
                }
                finally
                {
                    test.semaphoreSlim.Release();
                }
            },
            async test =>
            {
                try
                {
                    await test.semaphoreSlim.WaitAsync();
                    test.x++;
                }
                finally
                {
                    test.semaphoreSlim.Release();
                }
            });

        await testLock.Run("SemaphoreLock Sync+Async",
            test =>
            {
                try
                {
                    test.semaphoreLock.Enter();
                    test.x++;
                }
                finally
                {
                    test.semaphoreLock.Exit();
                }
            },
            async test =>
            {
                try
                {
                    await test.semaphoreLock.EnterAsync();
                    test.x++;
                }
                finally
                {
                    test.semaphoreLock.Exit();
                }
            });
    }


    private static async Task TestThreadCore_Termination()
    {
        var c1 = new TaskCore(Root, async parameter =>
        {
            var core = (TaskCore)parameter!; // Get ThreadCore from the parameter.
            Console.WriteLine("TaskCore 1: Start");

            try
            {
                // Task.Delay(2000).Wait(); // No CancellationToken
                await Task.Delay(3000, Root.CancellationToken);
            }
            catch
            {
                Console.WriteLine("TaskCore 1: Canceled");
                return;
            }

            Console.WriteLine("TaskCore 1: End");
        }, false);

        c1.SendSignal(ExecutionSignal.Start);
        var c2 = new ExecutionGroup(Root);

        try
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                Root.RequestTermination();
            });

            try
            {
                await Task.Delay(2000, Root.CancellationToken);
            }
            catch
            {
                throw new Exception();
            }
        }
        catch
        {
            Root.RequestTermination();
            Root.WaitForTermination(-1).Wait();
        }

        // c1.Start();

        // c1.ChangeParent(c2);
        // c2.Start(true);
        // c2.Terminate();
    }

    private class WaitPulseTask : TaskCore
    {
        public WaitPulseTask(ExecutionGroup parent, AsyncPulseEvent pulseEvent, int index)
            : base(parent, Process)
        {
            this.pulseEvent = pulseEvent;
            this.index = index;
        }

        private static async Task Process(object? parameter)
        {
            var core = (WaitPulseTask)parameter!;

            Console.WriteLine($"Wait start {core.index}");
            await core.pulseEvent.WaitAsync();
            Console.WriteLine($"Wait end {core.index}");
        }

        private AsyncPulseEvent pulseEvent;
        private int index;
    }

    private static async Task TestAsyncPulseEvent()
    {
        Console.WriteLine("AsyncPulseEvent.");

        var pulseEvent = new AsyncPulseEvent();

        var c2 = new TaskCore(Root, async parameter =>
        {
            var core = (TaskCore)parameter!; // Get TaskCore from the parameter.

            await Task.Delay(1000);
            Console.WriteLine("Set");
            pulseEvent.Pulse();
        });

        for (var i = 0; i < 20; i++)
        {
            new WaitPulseTask(Root, pulseEvent, i);
        }
    }
}
