// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
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

internal class Program
{
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

        // TestThreadCore();
        // TestThreadWorker();
        // await TestTaskWorker();
        // await TestTaskWorker2();

        await ThreadCore.Root.WaitForTerminationAsync(-1); // Wait for the termination infinitely.
        ThreadCore.Root.TerminationEvent.Set(); // The termination process is complete (#1).
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

        worker.Terminate();
    }
}
