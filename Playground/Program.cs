using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Arc;
using Arc.Threading;

namespace Playground;

public class TestWorker : ReusableJobWorker<ReusableThreadJob>
{
    public TestWorker(ExecutionGroup parent)
        : base(parent)
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

public class CustomCore : TaskCore<CustomCore>
{
    public CustomCore(ExecutionGroup parent)
        : base(parent, Process)
    {
    }

    private static async Task Process(CustomCore core)
    {
        Console.WriteLine("5");

        try
        {
            await core.Delay(2000);
        }
        catch
        {
        }

        Console.WriteLine("6");
    }
}

class Program
{
    public static ExecutionRoot Root { get; } = new();

    static async Task Main(string[] args)
    {
        AppCloseHandler.Set(() =>
        {// Closing the console window or terminating the process.
            Root.RequestTermination(); // Send a termination signal to the root.
            Root.WaitForTermination(TimeSpan.FromSeconds(2)).Wait();
        });

        Console.CancelKeyPress += (s, e) =>
        {// Ctrl+C pressed.
            e.Cancel = true;
            Root.RequestTermination(); // Send a termination signal to the root.
        };

        Console.WriteLine("Hello World!");
        Console.WriteLine();

        var c1 = new TaskCore(Root, async core =>
        {
            Console.WriteLine("1");

            try
            {
                await core.Delay(1000);
            }
            catch
            {
            }

            Console.WriteLine("2");
        });

        var g = new ExecutionGroup(Root);
        var c2 = new TaskCore(g, async core =>
        {
            Console.WriteLine("3");
            try
            {
                await core.Delay(1500);
            }
            catch
            {
            }

            Console.WriteLine("4");
        }
        );

        var tc = new ThreadCore(Root, async core =>
        {
            Console.WriteLine("A");

            try
            {
                Thread.Sleep(500);
            }
            catch
            {
            }

            Console.WriteLine("B");
        });
        tc.Name = "ThreadCore";

        var cc = new CustomCore(Root);

        c1.SendSignal(ExecutionSignal.Start);
        Root.SendSignal(ExecutionSignal.Start);
        await cc.Task;
        // var c1 = new ExecutionCore(Root.Base);

        await c2.WaitForTermination();
        // var token = ((CancellationTokenSource)c2).Token;

        await Test2(Root);

        Root.RequestTermination();
        await Root.WaitForTermination();
    }

    static async Task Test2(ExecutionGroup root)
    {
        var worker = new TestWorker(root);
        var job1 = worker.Rent();
        worker.Add(job1);
        Console.WriteLine(job1.State);
        await Task.Delay(1);
        await worker.WaitForCompletion();
        // job1.Wait();
        Console.WriteLine(job1.State);

        worker.Dispose();
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
