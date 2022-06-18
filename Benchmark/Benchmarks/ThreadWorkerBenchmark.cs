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
    internal class TestWorkObsolete : ThreadWorkObsolete
    {
        public TestWorkObsolete(int id)
        {
            this.Id = id;
        }

        public int Id { get; }

        public long Result { get; set; }
    }

    internal class TestWork : ThreadWork
    {
        public TestWork(int id)
        {
            this.Id = id;
        }

        public int Id { get; }

        public long Result { get; set; }
    }

    internal class TestTaskWork
    {
        public TestTaskWork(int id)
        {
            this.Id = id;
        }

        public int Id { get; }

        public long Result { get; set; }
    }

    internal class TestTaskWorkSlim : TaskWorkSlim
    {
        public TestTaskWorkSlim(int id)
        {
            this.Id = id;
        }

        public int Id { get; }

        public long Result { get; set; }
    }

    internal class ThreadWorkerBenchmark
    {
        internal const int Repeat = 3;
        internal const int N = 1_000_000;
        internal const int N2 = 100_000;

        internal static void Benchmark()
        {
            Console.WriteLine($"ThreadWorker");
            var worker2 = new ThreadWorker<TestWork>(ThreadCore.Root, EmptyMethod2);
            BenchWorker2(N, worker2);
            worker2.Dispose();
            Console.WriteLine();

            Console.WriteLine($"TaskWorker");
            var taskWorker = new TaskWorker<TestTaskWork>(ThreadCore.Root, EmptyMethodTask);
            BenchWorkerTask(N, taskWorker);
            taskWorker.Dispose();
            Console.WriteLine();

            Console.WriteLine($"TaskWorker2");
            var taskWorker2 = new TaskWorker2<TestTaskWork>(ThreadCore.Root, EmptyMethodTask2);
            BenchWorkerTask2(N, taskWorker2);
            taskWorker.Dispose();
            Console.WriteLine();

            Console.WriteLine($"TaskWorkerSlim");
            var taskWorkerSlim = new TaskWorkerSlim<TestTaskWorkSlim>(ThreadCore.Root, EmptyMethodTaskSlim);
            BenchWorkerTaskSlim(N, taskWorkerSlim);
            taskWorkerSlim.Dispose();
            Console.WriteLine();

            Console.WriteLine($"ThreadWorker heavy");
            var heavyWorker2 = new ThreadWorker<TestWork>(ThreadCore.Root, HeavyMethod2);
            BenchWorker2(N2, heavyWorker2);
            heavyWorker2.Dispose();
            Console.WriteLine();

            Console.WriteLine($"TaskWorker heavy");
            var taskWorkerHeavy = new TaskWorker<TestTaskWork>(ThreadCore.Root, HeavyMethodTask);
            BenchWorkerTask(N2, taskWorkerHeavy);
            taskWorker2.Dispose();
            Console.WriteLine();

            Console.WriteLine($"TaskWorkerSlim heavy");
            var taskWorkerSlim2 = new TaskWorkerSlim<TestTaskWorkSlim>(ThreadCore.Root, HeavyMethodTaskSlim);
            BenchWorkerTaskSlim(N2, taskWorkerSlim2);
            taskWorkerSlim2.Dispose();
            Console.WriteLine();

            /*Console.WriteLine($"ThreadWorker(Obsolete)");
            var worker = new ThreadWorkerObsolete<TestWorkObsolete>(ThreadCore.Root, EmptyMethod);
            BenchWorker(N, worker);
            worker.Dispose();
            Console.WriteLine();

            Console.WriteLine($"ThreadWorker(Obsolete) heavy");
            var heavyWorker = new ThreadWorkerObsolete<TestWorkObsolete>(ThreadCore.Root, HeavyMethod);
            BenchWorker(N2, heavyWorker);
            heavyWorker.Dispose();
            Console.WriteLine();*/
        }

        private static void BenchWorker(int count, ThreadWorkerObsolete<TestWorkObsolete> worker)
        {
            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                for (var n = 0; n < count; n++)
                {
                    worker.Add(new(n));
                }

                benchTimer.Stop();
                worker.WaitForCompletion(-1);
            }

            Console.WriteLine(benchTimer.GetResult("Sequence Add"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                for (var n = 0; n < count; n++)
                {
                    var w = new TestWorkObsolete(n);
                    worker.Add(w);
                    w.Wait(-1);
                }

                worker.WaitForCompletion(-1);
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Sequence Add and Wait"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                Parallel.For(0, 10, x =>
                {
                    for (var n = 0; n < (count / 10); n++)
                    {
                        worker.Add(new(n));
                    }
                });

                worker.WaitForCompletion(-1);
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 10 Add"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                Parallel.For(0, 10, x =>
                {
                    for (var n = 0; n < (count / 10); n++)
                    {
                        var w = new TestWorkObsolete(n);
                        worker.Add(w);
                        w.Wait(-1);
                    }
                });

                worker.WaitForCompletion(-1);
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 10 Add and Wait"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                Parallel.For(0, 100, x =>
                {
                    for (var n = 0; n < (count / 100); n++)
                    {
                        var w = new TestWorkObsolete(n);
                        worker.Add(w);
                        w.Wait(-1);
                    }
                });

                worker.WaitForCompletion(-1);
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 100 Add and Wait"));
            benchTimer.Clear();
        }

        private static void BenchWorker2(int count, ThreadWorker<TestWork> worker)
        {
            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                for (var n = 0; n < count; n++)
                {
                    worker.Add(new(n));
                }

                benchTimer.Stop();
                worker.WaitForCompletion(-1);
            }

            Console.WriteLine(benchTimer.GetResult("Sequence Add"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                Parallel.For(0, 10, x =>
                {
                    for (var n = 0; n < (count / 10); n++)
                    {
                        worker.Add(new(n));
                    }
                });

                worker.WaitForCompletion(-1);
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 10 Add"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                for (var n = 0; n < count; n++)
                {
                    var w = new TestWork(n);
                    worker.Add(w);
                    w.Wait(-1);
                }

                worker.WaitForCompletion(-1);
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Sequence Add and Wait"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
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
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 10 Add and Wait"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
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
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 100 Add and Wait"));
            benchTimer.Clear();
        }

        private static void BenchWorkerTask(int count, TaskWorker<TestTaskWork> worker)
        {
            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                for (var n = 0; n < count; n++)
                {
                    worker.AddLast(new(n));
                }

                benchTimer.Stop();
                worker.WaitForCompletionAsync().Wait();
            }

            Console.WriteLine(benchTimer.GetResult("Sequence Add"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                Parallel.For(0, 10, x =>
                {
                    for (var n = 0; n < (count / 10); n++)
                    {
                        worker.AddLast(new(n));
                    }
                });

                worker.WaitForCompletionAsync().Wait();
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 10 Add"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                for (var n = 0; n < count; n++)
                {
                    var w = new TestTaskWork(n);
                    worker.AddLast(w).WaitForCompletionAsync().Wait();
                }

                worker.WaitForCompletionAsync().Wait();
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Sequence Add and Wait"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                Parallel.For(0, 10, x =>
                {
                    for (var n = 0; n < (count / 10); n++)
                    {
                        var w = new TestTaskWork(n);
                        worker.AddLast(w).WaitForCompletionAsync().Wait();
                    }
                });

                worker.WaitForCompletionAsync().Wait();
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 10 Add and Wait"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                Parallel.For(0, 100, x =>
                {
                    for (var n = 0; n < (count / 100); n++)
                    {
                        var w = new TestTaskWork(n);
                        worker.AddLast(w).WaitForCompletionAsync().Wait();
                    }
                });

                worker.WaitForCompletionAsync().Wait();
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 100 Add and Wait"));
            benchTimer.Clear();
        }

        private static void BenchWorkerTask2(int count, TaskWorker2<TestTaskWork> worker)
        {
            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                for (var n = 0; n < count; n++)
                {
                    worker.AddLast(new(n));
                }

                benchTimer.Stop();
                worker.WaitForCompletionAsync().Wait();
            }

            Console.WriteLine(benchTimer.GetResult("Sequence Add"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                Parallel.For(0, 10, x =>
                {
                    for (var n = 0; n < (count / 10); n++)
                    {
                        worker.AddLast(new(n));
                    }
                });

                worker.WaitForCompletionAsync().Wait();
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 10 Add"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                for (var n = 0; n < count; n++)
                {
                    var w = new TestTaskWork(n);
                    worker.AddLast(w).WaitForCompletionAsync().Wait();
                }

                worker.WaitForCompletionAsync().Wait();
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Sequence Add and Wait"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                Parallel.For(0, 10, x =>
                {
                    for (var n = 0; n < (count / 10); n++)
                    {
                        var w = new TestTaskWork(n);
                        worker.AddLast(w).WaitForCompletionAsync().Wait();
                    }
                });

                worker.WaitForCompletionAsync().Wait();
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 10 Add and Wait"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                Parallel.For(0, 100, x =>
                {
                    for (var n = 0; n < (count / 100); n++)
                    {
                        var w = new TestTaskWork(n);
                        worker.AddLast(w).WaitForCompletionAsync().Wait();
                    }
                });

                worker.WaitForCompletionAsync().Wait();
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 100 Add and Wait"));
            benchTimer.Clear();
        }

        private static void BenchWorkerTaskSlim(int count, TaskWorkerSlim<TestTaskWorkSlim> worker)
        {
            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                for (var n = 0; n < count; n++)
                {
                    worker.Add(new(n));
                }

                benchTimer.Stop();
                worker.WaitForCompletionAsync().Wait();
            }

            Console.WriteLine(benchTimer.GetResult("Sequence Add"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                Parallel.For(0, 10, x =>
                {
                    for (var n = 0; n < (count / 10); n++)
                    {
                        worker.Add(new(n));
                    }
                });

                worker.WaitForCompletionAsync().Wait();
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 10 Add"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                for (var n = 0; n < count; n++)
                {
                    var w = new TestTaskWorkSlim(n);
                    worker.Add(w);
                    w.WaitForCompletionAsync().Wait();
                }

                worker.WaitForCompletionAsync().Wait();
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Sequence Add and Wait"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                Parallel.For(0, 10, x =>
                {
                    for (var n = 0; n < (count / 10); n++)
                    {
                        var w = new TestTaskWorkSlim(n);
                        worker.Add(w);
                        w.WaitForCompletionAsync().Wait();
                    }
                });

                worker.WaitForCompletionAsync().Wait();
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 10 Add and Wait"));
            benchTimer.Clear();

            for (var repeat = 0; repeat < Repeat; repeat++)
            {
                benchTimer.Start();
                Parallel.For(0, 100, x =>
                {
                    for (var n = 0; n < (count / 100); n++)
                    {
                        var w = new TestTaskWorkSlim(n);
                        worker.Add(w);
                        w.WaitForCompletionAsync().Wait();
                    }
                });

                worker.WaitForCompletionAsync().Wait();
                benchTimer.Stop();
            }

            Console.WriteLine(benchTimer.GetResult("Parallel 100 Add and Wait"));
            benchTimer.Clear();
        }

        private static bool EmptyMethod(ThreadWorkerObsolete<TestWorkObsolete> worker, TestWorkObsolete work)
        {
            return true;
        }

        private static bool HeavyMethod(ThreadWorkerObsolete<TestWorkObsolete> worker, TestWorkObsolete work)
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

        private static AbortOrComplete EmptyMethod2(ThreadWorker<TestWork> worker, TestWork work)
        {
            return AbortOrComplete.Complete;
        }

        private static AbortOrComplete HeavyMethod2(ThreadWorker<TestWork> worker, TestWork work)
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

            return AbortOrComplete.Complete;
        }

        private static async Task<AbortOrComplete> EmptyMethodTask(TaskWorker<TestTaskWork> worker, TestTaskWork work)
        {
            return AbortOrComplete.Complete;
        }

        private static async Task<AbortOrComplete> HeavyMethodTask(TaskWorker<TestTaskWork> worker, TestTaskWork work)
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

            return AbortOrComplete.Complete;
        }

        private static async Task<AbortOrComplete> EmptyMethodTask2(TaskWorker2<TestTaskWork> worker, TestTaskWork work)
        {
            return AbortOrComplete.Complete;
        }

        private static async Task<AbortOrComplete> HeavyMethodTask2(TaskWorker2<TestTaskWork> worker, TestTaskWork work)
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

            return AbortOrComplete.Complete;
        }

        private static async Task<AbortOrComplete> EmptyMethodTaskSlim(TaskWorkerSlim<TestTaskWorkSlim> worker, TestTaskWorkSlim work)
        {
            return AbortOrComplete.Complete;
        }

        private static async Task<AbortOrComplete> HeavyMethodTaskSlim(TaskWorkerSlim<TestTaskWorkSlim> worker, TestTaskWorkSlim work)
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

            return AbortOrComplete.Complete;
        }

        private static BenchTimer benchTimer = new();
    }
}
