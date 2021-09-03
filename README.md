## Arc.Threading
![Nuget](https://img.shields.io/nuget/v/Arc.Threading) ![Build and Test](https://github.com/archi-Doc/Arc.Threading/workflows/Build%20and%20Test/badge.svg)

Arc.Threading is a support library for Task/Thread.

This document may be inaccurate. It would be greatly appreciated if anyone could make additions and corrections.



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
using Arc.Threading;

namespace ConsoleApp1;

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

        await ThreadCore.Root.WaitAsyncForTermination(-1); // Wait for the termination infinitely.
        ThreadCore.Root.TerminationEvent.Set(); // The termination process is complete (#1).
    }
}
```

