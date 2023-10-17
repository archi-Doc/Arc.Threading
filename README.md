## Arc.Threading
![Nuget](https://img.shields.io/nuget/v/Arc.Threading) ![Build and Test](https://github.com/archi-Doc/Arc.Threading/workflows/Build%20and%20Test/badge.svg)

**Arc.Threading** is a support library for Task/Thread.



## Quick Start

First, install Arc.Threading using Package Manager Console.

```
Install-Package Arc.Threading
```



## ThreadCore

`ThreadCore` is a wrapper class for `Thread` `Task` `Task<TResult>`.

The main purpose of `ThreadCore` is

1. Manage Thread/Task in a tree structure.
2. Terminate or pause Thread/Task from outside Thread/Task.
3. Unify the format of the method by passing `ThreadCore` as a parameter.

`ThreadCore` is intended for long-running processes such as `Thread`, but it can also be used for `Task`.

```csharp
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

        await ThreadCore.Root.WaitForTerminationAsync(-1); // Wait for the termination infinitely.
        ThreadCore.Root.TerminationEvent.Set(); // The termination process is complete (#1).
    }
}
```



```csharp
/// <summary>
/// Customized thread core class.
/// </summary>
internal class CustomCore : ThreadCore
{
    public static void Process(object? parameter)
    {
        var core = (CustomCore)parameter!;
    }

    public CustomCore(ThreadCoreBase parent)
            : base(parent, Process, false)
    {
    }

    public int CustomPropertyIfYouNeed { get; set; }
}
```



```csharp
private class ExampleTask : TaskCore
{
    public ExampleTask(object parent)
        : base(null, Process)
    {
        this.parent = parent;
    }

    private static async Task Process(object? parameter)
    {
        var core = (ExampleTask)parameter!;

        while (await core.Delay(1000).ConfigureAwait(false))
        {
        }
    }

    private readonly object parent;
}
```





## ThreadWorker

`ThreadWorker` is a `ThreadCore` class which receives and processes `ThreadWork`.

```csharp
private static void TestThreadWorker()
{
    // Create ThreadWorker by specifying a type of work and delegate.
    var worker = new ThreadWorker<TestWork>(ThreadCore.Root, (worker, work) =>
    {
        if (!worker.Sleep(100))
        {
            return false; // Aborted
        }

        Console.WriteLine($"Complete: {work.Id}, {work.Name}");
        return true; // Complete
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
```



## TaskWorker

`TaskWorker` is a `TaskCore` class which receives and processes `TWork`.

```csharp
private static async Task TestTaskWorker()
{
    // Create a TaskWorker by specifying the type of work and delegate.
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

    worker.NumberOfConcurrentTasks = 2;
    worker.SetCanStartConcurrentlyDelegate((workInterface, workingList) =>
    {
        Console.WriteLine("Start work concurrently: false");
        return false;
    });

    var w = new TestTaskWork(1, "A"); // New work
    Console.WriteLine(w); // Added work is 'Created'.
    var wi1 = worker.AddLast(w); // Add a work to the worker.
    Console.WriteLine(wi1); // Added work is 'Standby'.

    await Task.Delay(10);
    worker.AddLast(new TestTaskWork(1, "A"));

    var w2 = new TestTaskWork(2, "B");
    var wi = worker.AddLast(w2);
    worker.AddLast(w2);
    var w3 = new TestTaskWork(2, "B");
    worker.AddLast(w3);
    wi = worker.AddFirst(new(3, "C"));
    wi = worker.AddLast(new(4, "D"));

    var b = await wi1.WaitForCompletionAsync();
    Console.WriteLine(wi1);
    await worker.WaitForCompletionAsync();
    Console.WriteLine(w); // Complete

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
```



## AsyncPulseEvent

`AsyncPulseEvent` is a thread synchronization event that other threads wait until a pulse (signal) is received.

```csharp
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
```

