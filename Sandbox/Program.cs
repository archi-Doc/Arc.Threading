// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;

namespace Sandbox;

internal class CustomCore : ThreadCore
{
    public static void Process(object? parameter)
    {
        var core = (CustomCore)parameter!;
        Console.WriteLine("CustomCore: Start");

        while (!core.IsTerminated)
        {
            if (core.Count++ > 10)
            {
                break;
            }

            Thread.Sleep(100);
        }

        Console.WriteLine("CustomCore: End");
    }

    public CustomCore(ThreadCoreBase parent)
        : base(parent, Process, false)
    {
    }

    public int Count { get; private set; }
}

public static class TaskId
{
    private static volatile int currentId;
    private static readonly AsyncLocal<int> asyncLocal = new();

    public static int Get()
    {
        var v = asyncLocal.Value;
        if (v != 0)
        {
            return v;
        }
        else
        {
            v = Interlocked.Increment(ref currentId);
            asyncLocal.Value = v;
            return v;
        }
    }
}

public readonly struct TaskId2
{
    private static volatile int currentId;
    private static readonly AsyncLocal<TaskId2> asyncLocal = new();

    public static TaskId2 Get()
    {
        var v = asyncLocal.Value.Value;
        if (v != 0)
        {
            return new(v);
        }
        else
        {
            var taskId = new TaskId2(Interlocked.Increment(ref currentId));
            asyncLocal.Value = taskId;
            return taskId;
        }
    }

    public TaskId2(int id)
    {
        this.Value = id;
    }

    public readonly int Value;
}

internal class Program
{
    public static readonly AsyncLocal<int> AsyncLocalInstance = new();

    public static int EstimateSize<TClass>()
        where TClass : class, new()
    {
        const int N = 1000;
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < N; i++)
        {
            var obj = new TClass();
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        return (int)((after - before) / N);
    }

    public static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {// Console window closing or process terminated.
            ThreadCore.Root.Terminate(); // Send a termination signal to the root.
            ThreadCore.Root.TerminationEvent.WaitOne(2000); // Wait until the termination process is complete (#1).
        };

        Console.CancelKeyPress += (s, e) =>
        {// Ctrl+C pressed
            e.Cancel = true;
            ThreadCore.Root.Terminate(); // Send a termination signal to the root.
        };

        Console.WriteLine("Sandbox.");
        Console.WriteLine($"{EstimateSize<SemaphoreLock>()}");

        // await TestSingleTask();
        // await TestBinarySemaphore();
        // await TestSemaphoreLock();
        // await TestUniqueCore();
        // TestThreadCore();
        // TestThreadWorker();
        // await TestTaskWorker();
        // await TestTaskWorker2();
        // await TestMicroSleep();
        await TestSemaphoreLock2();

        var taskcore = new TaskCore(ThreadCore.Root, async core =>
        {
            Console.WriteLine("TaskCore: Start");
            await Task.Delay(1_000);
            Console.WriteLine("TaskCore: End");
        });

        Console.WriteLine();

        var semaphoreLock = new SemaphoreLock();
        Console.WriteLine($"EnterAsync: {await semaphoreLock.EnterAsync(500)}");
        semaphoreLock.Exit();
        Console.WriteLine($"Exit");

        Console.WriteLine($"EnterAsync: {await semaphoreLock.EnterAsync(500)}");
        Console.WriteLine($"EnterAsync: {await semaphoreLock.EnterAsync(500)}");
        semaphoreLock.Exit();
        Console.WriteLine($"Exit");

        Console.WriteLine("Terminated.");

        await ThreadCore.Root.WaitForTerminationAsync(-1); // Wait for the termination infinitely.
        ThreadCore.Root.TerminationEvent.Set(); // The termination process is complete (#1).
    }

    private static async Task TestMicroSleep()
    {
        var microSleep = new MicroSleep();
        var stopwatch = new Stopwatch();

        Console.WriteLine(microSleep.CurrentMode);

        stopwatch.Start();
        microSleep.Sleep(1_000);
        var mics = (double)stopwatch.ElapsedTicks / Stopwatch.Frequency * 1_000_000;
        Console.WriteLine(mics);

        microSleep.Dispose();
    }

    private static async Task TestSemaphoreLock2()
    {
        var semaphoreLock = new SemaphoreLock();
    }

    private static async Task TestSingleTask()
    {
        var singleTask = new SingleTask();

        var task = singleTask.TryRun(() =>
        {
            Thread.Sleep(500);
            Console.WriteLine("1");
            Thread.Sleep(500);
        });

        Console.WriteLine($"task: {task is not null}");

        var task2 = singleTask.TryRun(() =>
        {
            Thread.Sleep(500);
            Console.WriteLine("2");
            Thread.Sleep(500);
        });

        if (task2 is not null)
        {
            await task2;
        }

        Console.WriteLine($"task2: {task2 is not null}");

        if (task is not null)
        {
            await task;
        }

        var task3 = singleTask.TryRun(async () =>
        {
            Thread.Sleep(500);
            Console.WriteLine("3");
            Thread.Sleep(500);
        });

        if (task3 is not null)
        {
            await task3;
        }
    }

    private static async Task TestSemaphoreLock()
    {
        var ec = System.Threading.Thread.CurrentThread.ExecutionContext;
        AsyncLocalInstance.Value = 2;

        var semaphore = new SemaphoreLock();

        semaphore.Enter();
        Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId}, {Task.CurrentId}, {TaskId.Get()}");
        Console.WriteLine($"Lock");

        await Task.Run(async () =>
        {
            AsyncLocalInstance.Value = 3;
            var a = Task.Delay(100);
            var b = Task.Delay(100);
            var c = Task.Delay(100);
            await Task.WhenAll(new Task[] { a, b, c, });
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId}, {Task.CurrentId}, {TaskId.Get()}");
            semaphore.Exit();
            Console.WriteLine($"Unlock");
        });

        Console.WriteLine();
        await semaphore.EnterAsync(1000);
        Console.WriteLine($"Entered");

        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            semaphore.Exit();
        });

        var result = await semaphore.EnterAsync(1000);
        Console.WriteLine($"Try enter {result.ToString()}");

        semaphore.Exit();
        Console.WriteLine($"Exited");

        Console.WriteLine();

        result = await semaphore.EnterAsync(1000);
        Console.WriteLine($"Try enter {result.ToString()}");

        result = await semaphore.EnterAsync(1000);
        Console.WriteLine($"Try enter {result.ToString()}");

        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            cts.Cancel();
        });

        result = await semaphore.EnterAsync(TimeSpan.FromMilliseconds(1000), cts.Token);
        Console.WriteLine($"Try enter {result.ToString()}");

        semaphore.Exit();
        Console.WriteLine($"Exited");

        /*Monitor.Enter(semaphore);
        Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId}");
        Console.WriteLine($"Lock");

        await Task.Run(async () =>
        {
            var a = Task.Delay(100);
            var b = Task.Delay(100);
            var c = Task.Delay(100);
            await Task.WhenAll(new Task[] {a, b, c,});
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId}");
            Monitor.Exit(semaphore);
            Console.WriteLine($"Unlock");
        });*/
    }

    private static async Task TestUniqueCore()
    {
        var unique = new UniqueWork(async Task () =>
        {
            Console.WriteLine("Unique work start");
            await Task.Delay(1000);
            Console.WriteLine("Unique work end");
        });

        for (var i = 0; i < 100; i++)
        {
            _ = unique.Run();
        }

        await unique.Run();
    }

    private static void TestThreadCore()
    {
        // ThreadPool.SetMaxThreads(20, 20);

        var c1 = new ThreadCore(ThreadCore.Root, parameter =>
        {
            var core = (ThreadCore)parameter!; // Get ThreadCore from the parameter.
            Console.WriteLine("ThreadCore 1: Start");

            try
            {
                Task.Delay(3000).Wait(); // No CancellationToken
                // Task.Delay(3000, core.CancellationToken).Wait();
            }
            catch
            {
                Console.WriteLine("ThreadCore 1: Canceled");
                return;
            }

            Console.WriteLine("ThreadCore 1: End");
        }, false);

        var c2 = new ThreadCoreGroup(ThreadCore.Root);

        c1.ChangeParent(c2);
        c2.Start(true);
        c2.Terminate();
        // c2.Start(true);

        var cc = new CustomCore(ThreadCore.Root);
        cc.Start();
    }

    internal class TestWork : ThreadWork
    {
        public int Id { get; }

        public string Name { get; } = string.Empty;

        public TestWork(int id, string name)
        {
            this.Id = id;
            this.Name = name;
        }

        public override string ToString() => $"Id: {this.Id}, Name: {this.Name}, State: {this.State}";
    }

    private static void TestThreadWorker()
    {
        // Create ThreadWorker by specifying a type of work and delegate.
        var worker = new ThreadWorker<TestWork>(ThreadCore.Root, (worker, work) =>
        {
            if (!worker.Sleep(100))
            {
                return AbortOrComplete.Abort;
            }

            Console.WriteLine($"Complete: {work.Id}, {work.Name}");
            return AbortOrComplete.Complete;
        });

        var c = new TestWork(1, "A"); // New work
        worker.Add(c); // Add a work to the worker.
        Console.WriteLine(c); // Added work is on standby.

        worker.Add(new(2, "B"));

        c.Wait(-1);
        Console.WriteLine(c); // Work is complete.

        worker.Terminate();
    }

    internal class TestTaskWork : IEquatable<TestTaskWork>
    {
        public int Id { get; }

        public string Name { get; } = string.Empty;

        public string Result { get; set; } = string.Empty;

        public TestTaskWork(int id, string name)
        {
            this.Id = id;
            this.Name = name;
        }

        public override string ToString() => $"Id: {this.Id}, Name: {this.Name}, Result: {this.Result}";

        public override int GetHashCode() => HashCode.Combine(this.Id, this.Name);

        public bool Equals(TestTaskWork? other)
        {
            if (other == null)
            {
                return false;
            }

            return this.Id == other.Id && this.Name == other.Name;
        }
    }

    private static async Task TestTaskWorker()
    {
        // Create TaskWorker by specifying a type of work and delegate.
        var worker = new TaskWorker<TestTaskWork>(ThreadCore.Root, async (worker, work) =>
        {
            if (!await worker.Delay(1000))
            {
                return;
            }

            work.Result = "complete";
            Console.WriteLine($"Complete: {work.Id}, {work.Name}");
            return;
        });

        await worker.WaitForCompletionAsync();

        var w = new TestTaskWork(1, "A"); // New work
        var wi1 = worker.AddLast(w); // Add a work to the worker.
        Console.WriteLine(wi1); // Added work is 'Standby'.

        await Task.Delay(100);
        worker.AddLast(new TestTaskWork(1, "A"));

        var w2 = new TestTaskWork(2, "B");
        var wi = worker.AddLast(w2);
        worker.AddLast(w2);
        var w3 = new TestTaskWork(2, "B");
        worker.AddLast(w3);
        wi = worker.AddFirst(new(3, "C"));

        // var b = await wi1.WaitForCompletionAsync();
        Console.WriteLine(wi1);
        await worker.WaitForCompletionAsync();
        Console.WriteLine(w); // Complete

        worker.Terminate(); // ThreadCore.Root -> ThreadCore.Dependent?
    }
}
