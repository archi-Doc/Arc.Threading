// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;

namespace Benchmark.Test
{
    internal class TestWork : ThreadWork
    {
        public TestWork(int id)
        {
            this.Id = id;
        }

        public int Id { get; }

        public long Result { get; set; }
    }

    internal class TestWork2 : ThreadWork2
    {
        public TestWork2(int id)
        {
            this.Id = id;
        }

        public int Id { get; }

        public long Result { get; set; }
    }

    internal class ThreadWorkerBenchmark
    {
        internal const int Repeat = 5;
        internal const int N = 1000_000;
        internal const int N2 = 100_000;

        internal static void Benchmark()
        {
            Console.WriteLine($"ThreadWorker");
            var worker = new ThreadWorker<TestWork>(ThreadCore.Root, EmptyMethod);
            BenchWorker(N, worker);
            worker.Dispose();
            Console.WriteLine();

            Console.WriteLine($"ThreadWorker heavy");
            var heavyWorker = new ThreadWorker<TestWork>(ThreadCore.Root, HeavyMethod);
            BenchWorker(N2, heavyWorker);
            heavyWorker.Dispose();
            Console.WriteLine();

            Console.WriteLine($"ThreadWorker2");
            var worker2 = new ThreadWorker2<TestWork2>(ThreadCore.Root, EmptyMethod2);
            BenchWorker2(N, worker2);
            worker2.Dispose();
            Console.WriteLine();

            Console.WriteLine($"ThreadWorker2 heavy");
            var heavyWorker2 = new ThreadWorker2<TestWork2>(ThreadCore.Root, HeavyMethod2);
            BenchWorker2(N2, heavyWorker2);
            heavyWorker2.Dispose();
            Console.WriteLine();
        }

        private static void BenchWorker(int count, ThreadWorker<TestWork> worker)
        {
            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                Start();
                for (var n = 0; n < count; n++)
                {
                    worker.Add(new(n));
                }

                Stop("Sequence Add");
                worker.WaitForCompletion(-1);
            }

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                Start();
                for (var n = 0; n < count; n++)
                {
                    var w = new TestWork(n);
                    worker.Add(w);
                    w.Wait(-1);
                }

                worker.WaitForCompletion(-1);
                Stop("Sequence Add and Wait");
            }

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                Start();
                Parallel.For(0, 10, x =>
                {
                    for (var n = 0; n < (count / 10); n++)
                    {
                        worker.Add(new(n));
                    }
                });

                worker.WaitForCompletion(-1);
                Stop("Parallel 10 Add");
            }

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                Start();
                Parallel.For(0, 10, x =>
                {
                    for (var n = 0; n < (count / 10); n++)
                    {
                        var w = new TestWork(n);
                        worker.Add(w);
                        w.Wait(-1);
                    }
                });

                worker.WaitForCompletion(-1);
                Stop("Parallel 10 Add and Wait");
            }

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                Start();
                Parallel.For(0, 100, x =>
                {
                    for (var n = 0; n < (count / 100); n++)
                    {
                        var w = new TestWork(n);
                        worker.Add(w);
                        w.Wait(-1);
                    }
                });

                worker.WaitForCompletion(-1);
                Stop("Parallel 100 Add and Wait");
            }
        }

        private static void BenchWorker2(int count, ThreadWorker2<TestWork2> worker)
        {
            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                Start();
                for (var n = 0; n < count; n++)
                {
                    worker.Add(new(n));
                }

                Stop("Sequence Add");
                worker.WaitForCompletion(-1);
            }

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                Start();
                for (var n = 0; n < count; n++)
                {
                    var w = new TestWork2(n);
                    worker.Add(w);
                    w.Wait(-1);
                }

                worker.WaitForCompletion(-1);
                Stop("Sequence Add and Wait");
            }

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                Start();
                Parallel.For(0, 10, x =>
                {
                    for (var n = 0; n < (count / 10); n++)
                    {
                        worker.Add(new(n));
                    }
                });

                worker.WaitForCompletion(-1);
                Stop("Parallel 10 Add");
            }

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                Start();
                Parallel.For(0, 10, x =>
                {
                    for (var n = 0; n < (count / 10); n++)
                    {
                        var w = new TestWork2(n);
                        worker.Add(w);
                        w.Wait(-1);
                    }
                });

                worker.WaitForCompletion(-1);
                Stop("Parallel 10 Add and Wait");
            }

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                Start();
                Parallel.For(0, 100, x =>
                {
                    for (var n = 0; n < (count / 100); n++)
                    {
                        var w = new TestWork2(n);
                        worker.Add(w);
                        w.Wait(-1);
                    }
                });

                worker.WaitForCompletion(-1);
                Stop("Parallel 100 Add and Wait");
            }
        }

        private static bool EmptyMethod(ThreadWorker<TestWork> worker, TestWork work)
        {
            return true;
        }

        private static bool HeavyMethod(ThreadWorker<TestWork> worker, TestWork work)
        {
            unchecked
            {
                long x = 0;
                for (var i = 0; i < (work.Id & 0xFFFF); i++)
                {
                    x += i;
                }

                work.Result = x;
            }

            return true;
        }

        private static bool EmptyMethod2(ThreadWorker2<TestWork2> worker, TestWork2 work)
        {
            return true;
        }

        private static bool HeavyMethod2(ThreadWorker2<TestWork2> worker, TestWork2 work)
        {
            unchecked
            {
                long x = 0;
                for (var i = 0; i < (work.Id & 0xFFFF); i++)
                {
                    x += i;
                }

                work.Result = x;
            }

            return true;
        }

        private static void Start(string? text = null)
        {
            if (text != null)
            {
                Console.WriteLine(text);
            }

            sw.Restart();
        }

        private static void Stop(string? text = null)
        {
            sw.Stop();
            Console.WriteLine($"{text ?? "time", -25}: {sw.ElapsedMilliseconds} ms");
        }

        private static Stopwatch sw = new();
    }
}
