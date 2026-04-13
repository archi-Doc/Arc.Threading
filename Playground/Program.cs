using System;
using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;

namespace Playground;

public class TestWorker : ReusableJobWorker<ReusableThreadJob>
{
    public TestWorker(ThreadCoreBase? parent, Action<ReusableThreadJob>? processJob = null)
        : base(parent, processJob)
    {
        var job = this.Rent();
        this.Add(job);
        job.Wait();
    }
}

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Hello World!");

        var worker = new TestWorker(ThreadCore.Root, x => { Thread.Sleep(1000); });
        var job = worker.Rent();
        Console.WriteLine(job.State);
        worker.Add(job);

        Console.WriteLine(job.State);
        Console.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        await worker.WaitForCompletion(500);        

        Console.WriteLine(job.State);
        Console.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        await worker.WaitForCompletion(500);
        Console.WriteLine(job.State);
        Console.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        ThreadCore.Root.Terminate();
    }
}
