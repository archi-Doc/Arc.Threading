// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;

namespace ConsoleApp1
{
    internal class Program
    {
        public static async Task Main(string[] args)
        {
            AppDomain.CurrentDomain.ProcessExit += async (s, e) =>
            {// Console window closing or process terminated.
                ThreadCore.Root.Terminate(); // Send a termination signal to the root.
                ThreadCore.Root.TerminationEvent.WaitOne(2000); // Wait until the termination process is complete (#1).
            };

            Console.CancelKeyPress += (s, e) =>
            {// Ctrl+C pressed
                e.Cancel = true;
                ThreadCore.Root.Terminate(); // Send a termination signal to the root.
            };

            Console.WriteLine("ThreadCore Sample.");

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
                            return;
                        }
                    }
                }

                Console.WriteLine("ThreadCore 1: End");
            });

            var group = new ThreadCoreGroup(ThreadCore.Root); // ThreadCoreGroup is not associated with Thread/Task.
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

            await ThreadCore.Root.WaitForTermination(-1); // Wait for the termination infinitely.
            ThreadCore.Root.TerminationEvent.Set(); // The termination process is complete (#1).
        }
    }
}
