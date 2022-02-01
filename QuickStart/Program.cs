// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;

namespace QuickStart;

internal class Program
{
    public static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {// Closing the console window or terminating the process.
            ThreadCore.Root.Terminate(); // Send a termination signal to the root.
            ThreadCore.Root.TerminationEvent.WaitOne(2000); // Wait until the termination process is complete (#1).
        };

        Console.CancelKeyPress += (s, e) =>
        {// Ctrl+C pressed.
            e.Cancel = true;
            ThreadCore.Root.Terminate(); // Send a termination signal to the root.
        };

        Console.WriteLine("ThreadCore Sample.");

        // await TestThreadCore();
        TestThreadWorker();

        await ThreadCore.Root.WaitForTerminationAsync(-1); // Wait for the termination infinitely.
        ThreadCore.Root.TerminationEvent.Set(); // The termination process is complete (#1).
    }

    private static async Task TestThreadCore()
    {
        // Create ThreadCore object.
        // ThreadCore.Root is the root object of all ThreadCoreBase classes.
        var c1 = new ThreadCore(ThreadCore.Root, parameter =>
        {// Core 1 (ThreadCore): Shows a message every 1 second, and terminates after 5 second.
            var core = (ThreadCore)parameter!; // Get ThreadCore from the parameter.
            Console.WriteLine("ThreadCore 1: Start");

            for (var n = 0; n < 5; n++)
            {
                Console.WriteLine($"ThreadCore 1: {n}");

                for (var m = 0; m < 10; m++)
                {
                    Thread.Sleep(100);
                    if (core.IsTerminated)
                    {
                        Console.WriteLine("ThreadCore 1: Canceled");
                        return;
                    }
                }
            }

            Console.WriteLine("ThreadCore 1: End");
        });

        var group = new ThreadCoreGroup(ThreadCore.Root); // ThreadCoreGroup is a collection of ThreadCore objects and it's not associated with Thread/Task.
        var c2 = new TaskCore(group, async parameter =>
        {// Core 2 (TaskCore): Shows a message, wait for 3 seconds, and terminates.
            var core = (TaskCore)parameter!; // Get TaskCore from the parameter.
            Console.WriteLine("TaskCore 2: Start");
            Console.WriteLine("TaskCore 2: Delay 3 seconds");

            try
            {
                await Task.Delay(3000, core.CancellationToken);
            }
            catch
            {
                Console.WriteLine("TaskCore 2: Canceled");
            }

            Console.WriteLine("TaskCore 2: End");
            core.Dispose(); // You can dispose the object if you want (automatically disposed anyway).
        });

        try
        {
            await Task.Delay(1500, ThreadCore.Root.CancellationToken);
        }
        catch
        {
        }

        c2.Terminate(); // Send a termination signal to the TaskCore2.
        // group.Dispose(); // Same as above
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

        var w = new TestWork(1, "A"); // New work
        worker.Add(w); // Add a work to the worker.
        Console.WriteLine(w); // Added work is on standby.

        worker.Add(new(2, "B"));

        w.Wait(200);
        Console.WriteLine(w); // Work is complete.

        worker.Terminate();
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
}
