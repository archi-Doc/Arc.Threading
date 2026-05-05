// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;
using Arc;
using Arc.Threading;

namespace QuickStart;

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

        Console.WriteLine("QuickStart.");

        await TestThreadCore();

        await Root.WaitForTermination(); // Wait for the termination infinitely.
    }

    private static async Task TestThreadCore()
    {
        // Create ThreadCore object.
        // ThreadCore.Root is the root object of all ThreadCoreBase classes.
        var c1 = new ThreadCore(Root, parameter =>
        {// Core 1 (ThreadCore): Shows a message every 1 second, and terminates after 5 second.
            var core = (ThreadCore)parameter!; // Get ThreadCore from the parameter.
            Console.WriteLine("ThreadCore 1: Start");

            for (var n = 0; n < 5; n++)
            {
                Console.WriteLine($"ThreadCore 1: {n}");

                for (var m = 0; m < 10; m++)
                {
                    Thread.Sleep(100);
                    if (!core.IsActive)
                    {
                        Console.WriteLine("ThreadCore 1: Canceled");
                        return;
                    }
                }
            }

            Console.WriteLine("ThreadCore 1: End");
        });

        var group = new ExecutionGroup(Root); // ThreadCoreGroup is a collection of ThreadCore objects and it's not associated with Thread/Task.
        var c2 = new TaskCore(group, async core =>
        {// Core 2 (TaskCore): Shows a message, wait for 3 seconds, and terminates.
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
            await Task.Delay(1500, Root.CancellationToken);
        }
        catch
        {
        }

        c2.RequestTermination(); // Send a termination signal to the TaskCore2.
        // group.Dispose(); // Same as above
    }

    private class WaitPulseCore : TaskCore
    {
        public WaitPulseCore(ExecutionGroup parent, AsyncPulseEvent pulseEvent, int index)
            : base(parent, Process)
        {
            this.pulseEvent = pulseEvent;
            this.index = index;
        }

        private static async Task Process(TaskCore taskCore)
        {
            var core = (WaitPulseCore)taskCore;

            Console.WriteLine($"Wait start {core.index}");
            await core.pulseEvent.WaitAsync();
            Console.WriteLine($"Wait end {core.index}");
        }

        private AsyncPulseEvent pulseEvent;
        private int index;
    }
}
