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

        var pulseEvent = new AsyncPulseEvent4();
        var tcs = new CancellationTokenSource();

        Console.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));


        try
        {
            await pulseEvent.WaitAsync(TimeSpan.FromSeconds(1), tcs.Token);
        }
        catch (TimeoutException)
        {
        }

        Console.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        ThreadCore.Root.Terminate();
    }
}
