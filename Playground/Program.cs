using System;
using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;

namespace Playground;

public class TestWorker : ReusableJobWorker<ReusableThreadJob>
{
    public TestWorker()
        : base(ThreadCore.Root, default)
    {
    }

    /*protected override void OnAfterProcessJob()
    {
        Console.WriteLine("OnAfterProcessJob");
    }*/

    protected override async Task OnJobProcessing(ReusableThreadJob job, CancellationToken cancellationToken)
    {
        Console.WriteLine("Process");
        await Task.Delay(1000);
        Console.WriteLine("Done");
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
    public static ExecutionRoot? Root { get; private set; }

    static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {// Closing the console window or terminating the process.
            Root?.RequestTermination(); // Send a termination signal to the root.
            Root?.WaitForTermination().Wait();
        };

        Console.CancelKeyPress += (s, e) =>
        {// Ctrl+C pressed.
            e.Cancel = true;//
            Root?.RequestTermination(); // Send a termination signal to the root.
        };

        Console.WriteLine("Hello World!");
        Console.WriteLine();

        Root = new ExecutionRoot();
        var c1 = new TaskCore2(Root.Independent, async obj =>
        {
            var core = (TaskCore2)obj!;
            Console.WriteLine("1");

            try
            {
                await core.Delay(2000);
            }
            catch
            {
            }

            Console.WriteLine("2");
        }, false);

        c1.SendSignal(ExecutionSignal.Start);
        // var c1 = new ExecutionCore(Root.Base);

        // await Test2();

        Root.RequestTermination();
        await Root.WaitForTermination();
    }

    static async Task Test2()
    {
        var worker = new TestWorker();
        var job1 = worker.Rent();
        worker.Add(job1);
        Console.WriteLine(job1.State);
        await Task.Delay(1);
        await worker.WaitForCompletion();
        // job1.Wait();
        Console.WriteLine(job1.State);
    }

    static async Task Test1()
    {
        var pulseEvent = new AsyncPulseEvent();
        var tcs = new CancellationTokenSource();

        Console.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            // tcs.Cancel();
        });

        try
        {
            await pulseEvent.WaitAsync(TimeSpan.FromSeconds(1), tcs.Token);
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("Canceled");
        }
        catch (TimeoutException)
        {
            Console.WriteLine("Timeout");
        }

        Console.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
    }
}
