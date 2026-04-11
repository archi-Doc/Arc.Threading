using System;
using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;

namespace Playground;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Hello World!");

        var worker = new ReusableJobWorker<ReusableThreadJob>(ThreadCore.Root, x => { Thread.Sleep(1000); });
        var job = worker.Rent();
        Console.WriteLine(job.State);
        worker.Add(job);

        Console.WriteLine(job.State);
        Console.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        await worker.WaitForCompletion(500);        
        // job.Wait();
        // worker.Return(job);

        Console.WriteLine(job.State);
        Console.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        

        ThreadCore.Root.Terminate();
    }
}
