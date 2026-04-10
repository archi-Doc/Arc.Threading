using System;
using System.Threading.Tasks;
using Arc.Threading;

namespace Playground;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Hello World!");

        var worker = new ReusableJobWorker<ReusableWork>(10, x => { });
        var job = worker.Rent();
        worker.Add(job);
        job.Wait();
        worker.Return(job);
    }
}
