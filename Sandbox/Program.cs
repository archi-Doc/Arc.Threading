// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;

namespace ConsoleApp1
{
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

            Console.WriteLine("Sandbox.");

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
            });

            var cc = new CustomCore(ThreadCore.Root);
            cc.Start();

            await ThreadCore.Root.WaitForTermination(-1); // Wait for the termination infinitely.
            ThreadCore.Root.TerminationEvent.Set(); // The termination process is complete (#1).
        }
    }
}
