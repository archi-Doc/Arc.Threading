using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Arc.Threading;

namespace Sandbox;

internal static class BlastLogger
{
    internal const int Max = 500;

    static BlastLogger()
    {
        syncObject = new();
        sb = new();
        stopwatch = Stopwatch.StartNew();
    }

    internal static void Log(int id)
    {
        lock (syncObject)
        {
            if (count > Max)
            {
                return;
            }

            count++;
            var time = stopwatch.ElapsedTicks / (double)Stopwatch.Frequency * 1000d;
            sb.AppendLine($"{time:F6} : Id {id}");
        }
    }

    internal static void WriteFile()
    {
        File.WriteAllText("blast.log", sb.ToString());
    }

    private static object syncObject;
    private static int count;
    private static Stopwatch stopwatch;
    private static StringBuilder sb;
}

internal class BlastCore : ThreadCore
{
    public struct Timespec : IEquatable<Timespec>
    {
        public long tv_sec;   // Seconds.
        public long tv_nsec;  // Nanoseconds.

        public override int GetHashCode()
        {
            return tv_sec.GetHashCode() ^ tv_nsec.GetHashCode();
        }

        public override bool Equals(object? obj)
        {
            if (obj == null || obj.GetType() != GetType())
            {
                return false;
            }

            var value = (Timespec)obj;
            return value.tv_sec == tv_sec && value.tv_nsec == tv_nsec;
        }

        public bool Equals(Timespec value)
        {
            return value.tv_sec == tv_sec && value.tv_nsec == tv_nsec;
        }

        public static bool operator ==(Timespec lhs, Timespec rhs)
        {
            return lhs.Equals(rhs);
        }

        public static bool operator !=(Timespec lhs, Timespec rhs)
        {
            return !lhs.Equals(rhs);
        }
    }

    [DllImport("libc")]
    public static extern int nanosleep(ref Timespec req, ref Timespec rem);

    public static void Process(object? parameter)
    {
        var core = (BlastCore)parameter!;
        Console.WriteLine($"TestBlastCore {core.Id}: Start");

        var available = true;
        try
        {
            Timespec req = default;
            Timespec rem = default;
            nanosleep(ref req, ref rem);
        }
        catch
        {
            available = false;
            Console.WriteLine($"TestBlastCore {core.Id}: x");
        }

        for (var i = 0; i < 100; i++)
        {
            BlastLogger.Log(core.Id);

            Timespec req = default;
            Timespec rem = default;
            req.tv_nsec = 1_000_000;
            if (available)
            {
                try
                {
                    nanosleep(ref req, ref rem);
                }
                catch
                {
                }
            }

            try
            {
                // throw new Exception();
            }
            catch
            {
            }

            // System.Threading.Thread.Sleep(16 + core.Id);
        }
    }

    public BlastCore(ThreadCoreBase parent, int id)
        : base(parent, Process, false)
    {
        this.Id = id;
    }

    public int Id { get; private set; }
}

internal class BlastTask : TaskCore
{
    public static async Task Process(object? parameter)
    {
        var core = (BlastTask)parameter!;
        Console.WriteLine($"TestBlastTask {core.Id}: Start");

        for (var i = 0; i < 100; i++)
        {
            BlastLogger.Log(core.Id);
            await Task.Delay(15 + core.Id);
        }
    }

    public BlastTask(ThreadCoreBase parent, int id)
        : base(parent, Process, false)
    {
        this.Id = id;
    }

    public int Id { get; private set; }
}
