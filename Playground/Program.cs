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
    }

    protected override void OnAfterProcessJob()
    {
        Console.WriteLine("OnAfterProcessJob");
    }
}

public class TestCore : TaskCore
{
    private static async Task Method(object? core)
    {
        Console.WriteLine("1");
        await Task.Delay(300);
        Console.WriteLine("2");
        await Task.Delay(300);
        Console.WriteLine("3");
    }

    public TestCore(ThreadCoreBase? parent, bool startImmediately = true)
        : base(parent, Method, startImmediately)
    {
    }
}

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Hello World!");

        var core = new TestCore(ThreadCore.Root);

        // Thread.Sleep(2000);

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
