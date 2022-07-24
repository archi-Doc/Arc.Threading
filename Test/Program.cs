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


        await TestThreadCore_Termination();
        // await TestAsyncPulseEvent();

        await ThreadCore.Root.WaitForTerminationAsync(-1); // Wait for the termination infinitely.
        ThreadCore.Root.TerminationEvent.Set(); // The termination process is complete (#1).
    }

    private static async Task TestThreadCore_Termination()
    {
        var c1 = new TaskCore(ThreadCore.Root, async parameter =>
        {
            var core = (TaskCore)parameter!; // Get ThreadCore from the parameter.
            Console.WriteLine("TaskCore 1: Start");

            try
            {
                // Task.Delay(2000).Wait(); // No CancellationToken
                await Task.Delay(3000, ThreadCore.Root.CancellationToken);
            }
            catch
            {
                core.LockTreeSync();
                Console.WriteLine("TaskCore 1: Canceled");
                return;
            }

            Console.WriteLine("TaskCore 1: End");
        }, false);

        c1.Start();
        var c2 = new ThreadCoreGroup(ThreadCore.Root);

        try
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                ThreadCore.Root.Terminate();
            });

            try
            {
                await Task.Delay(2000, ThreadCore.Root.CancellationToken);
            }
            catch
            {
                throw new Exception();
            }
        }
        catch
        {
            ThreadCore.Root.Terminate();
            ThreadCore.Root.WaitForTermination(-1);
            c1.LockTreeSync();
        }

        // c1.Start();

        // c1.ChangeParent(c2);
        // c2.Start(true);
        // c2.Terminate();
    }

    private class WaitPulseTask : TaskCore
    {
        public WaitPulseTask(ThreadCoreBase parent, AsyncPulseEvent pulseEvent, int index)
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

        var c2 = new TaskCore(ThreadCore.Root, async parameter =>
        {
            var core = (TaskCore)parameter!; // Get TaskCore from the parameter.

            await Task.Delay(1000);
            Console.WriteLine("Set");
            pulseEvent.Pulse();
        });

        for (var i = 0; i < 20; i++)
        {
            new WaitPulseTask(ThreadCore.Root, pulseEvent, i);
        }
    }
}
