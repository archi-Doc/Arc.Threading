// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;

namespace ConsoleApp1;

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

        TestThreadCore();
        // TestThreadWorker();

        await ThreadCore.Root.WaitForTerminationAsync(-1); // Wait for the termination infinitely.
        ThreadCore.Root.TerminationEvent.Set(); // The termination process is complete (#1).
    }

    private static void TestThreadCore()
    {
        var c1 = new ThreadCore(ThreadCore.Root, parameter =>
        {
            var core = (ThreadCore)parameter!; // Get ThreadCore from the parameter.
            Console.WriteLine("ThreadCore 1: Start");

            try
            {
                Task.Delay(3000, core.CancellationToken).Wait();
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
        // c2.Start(true);
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
                return false;
            }

            Console.WriteLine($"Complete: {work.Id}, {work.Name}");
            return true;
        });

        var c = new TestWork(1, "A"); // New work
        worker.Add(c); // Add a work to the worker.
        Console.WriteLine(c); // Added work is on standby.

        worker.Add(new(2, "B"));

        c.Wait(-1);
        Console.WriteLine(c); // Work is complete.

        worker.Terminate();
    }
}
