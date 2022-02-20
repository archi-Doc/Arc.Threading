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
        // TestThreadWorker();
        // await TestTaskWorker();
        await TestTaskWorker2();
        // await TestAsyncPulseEvent();

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
        var c2 = new TaskCore<bool>(group, async parameter =>
        {// Core 2 (TaskCore): Shows a message, wait for 3 seconds, and terminates.
            var core = (TaskCore<bool>)parameter!; // Get TaskCore from the parameter.
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
            return true;
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

    private static async Task TestTaskWorker2()
    {
        // Create ThreadWorker by specifying a type of work and delegate.
        var worker = new TaskWorker2<TestTaskWork2>(ThreadCore.Root, async (worker, work) =>
        {
            // if (!await worker.Delay(1000))
            if (!worker.Sleep(1000))
            {
                return AbortOrComplete.Abort;
            }

            Console.WriteLine($"Complete: {work.Id}, {work.Name}");
            return AbortOrComplete.Complete;
        });

        var w = new TestTaskWork2(1, "A"); // New work
        Console.WriteLine(w); // Added work is 'Created'.
        worker.AddLast(w); // Add a work to the worker.
        Console.WriteLine(w); // Added work is 'Standby'.

        var w2 = new TestTaskWork2(2, "B");
        worker.AddLast(w2);
        var w3 = new TestTaskWork2(2, "B");
        worker.AddLast(w3);
        worker.AddFirst(new(3, "C"));

        await w3.WaitForCompletionAsync();
        await worker.WaitForCompletionAsync();
        Console.WriteLine(w); // Complete

        worker.Terminate();
    }

    internal class TestTaskWork2 : TaskWork2, IEquatable<TestTaskWork2>
    {
        public int Id { get; }

        public string Name { get; } = string.Empty;

        public TestTaskWork2(int id, string name)
        {
            this.Id = id;
            this.Name = name;
        }

        public override string ToString() => $"Id: {this.Id}, Name: {this.Name}, State: {this.State}";

        public override int GetHashCode() => HashCode.Combine(this.Id, this.Name);

        public bool Equals(TestTaskWork2? other)
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
        // Create ThreadWorker by specifying a type of work and delegate.
        var worker = new TaskWorker<TestTaskWork>(ThreadCore.Root, async (worker, work) =>
        {
            // if (!await worker.Delay(1000))
            if (!worker.Sleep(1000))
            {
                return AbortOrComplete.Abort;
            }

            Console.WriteLine($"Complete: {work.Id}, {work.Name}");
            return AbortOrComplete.Complete;
        });

        var w = new TestTaskWork(1, "A"); // New work
        Console.WriteLine(w); // Added work is 'Created'.
        worker.Add(w); // Add a work to the worker.
        Console.WriteLine(w); // Added work is 'Standby'.

        worker.Add(new(2, "B"));
        worker.Add(new(3, "C"));

        await worker.WaitForCompletionAsync();
        Console.WriteLine(w); // Complete

        worker.Terminate();
    }

    internal class TestTaskWork : TaskWork
    {
        public int Id { get; }

        public string Name { get; } = string.Empty;

        public TestTaskWork(int id, string name)
        {
            this.Id = id;
            this.Name = name;
        }

        public override string ToString() => $"Id: {this.Id}, Name: {this.Name}, State: {this.State}";
    }

    private static void TestThreadWorker()
    {
        // Create ThreadWorker by specifying a type of work and delegate.
        var worker = new ThreadWorker<TestThreadWork>(ThreadCore.Root, (worker, work) =>
        {
            if (!worker.Sleep(1000))
            {
                return AbortOrComplete.Abort;
            }

            Console.WriteLine($"Complete: {work.Id}, {work.Name}");
            return AbortOrComplete.Complete;
        });

        var w = new TestThreadWork(1, "A"); // New work
        Console.WriteLine(w); // Added work is Created.
        worker.Add(w); // Add a work to the worker.
        Console.WriteLine(w); // Added work is on standby.

        worker.Add(new(2, "B"));

        w.Wait(-1);
        Console.WriteLine(w); // Work is complete.

        worker.Terminate();
    }

    internal class TestThreadWork : ThreadWork
    {
        public int Id { get; }

        public string Name { get; } = string.Empty;

        public TestThreadWork(int id, string name)
        {
            this.Id = id;
            this.Name = name;
        }

        public override string ToString() => $"Id: {this.Id}, Name: {this.Name}, State: {this.State}";
    }

    private static async Task TestAsyncPulseEvent()
    {
        Console.WriteLine("AsyncPulseEvent.");

        var pulseEvent = new AsyncPulseEvent(); // Create AsyncPulseEvent.
        var c = new TaskCore(ThreadCore.Root, async parameter =>
        { // Create TaskCore that will send a pulse after 1 second.
            var core = (TaskCore)parameter!; // Get TaskCore from the parameter.

            await Task.Delay(1000);
            Console.WriteLine("Pulse");
            pulseEvent.Pulse();
        });

        for (var i = 0; i < 4; i++)
        {// Create TaskCore that will wait until a pulse is received.
            new WaitPulseCore(ThreadCore.Root, pulseEvent, i);
        }
    }

    private class WaitPulseCore : TaskCore
    {
        public WaitPulseCore(ThreadCoreBase parent, AsyncPulseEvent pulseEvent, int index)
            : base(parent, Process)
        {
            this.pulseEvent = pulseEvent;
            this.index = index;
        }

        private static async Task Process(object? parameter)
        {
            var core = (WaitPulseCore)parameter!;

            Console.WriteLine($"Wait start {core.index}");
            await core.pulseEvent.WaitAsync();
            Console.WriteLine($"Wait end {core.index}");
        }

        private AsyncPulseEvent pulseEvent;
        private int index;
    }
}
