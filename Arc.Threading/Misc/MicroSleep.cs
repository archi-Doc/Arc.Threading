// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

#pragma warning disable SA1124
#pragma warning disable SA1300 // Element should begin with upper-case letter

namespace Arc.Threading;

/// <summary>
/// Represents a class for performing microsecond-level sleep operations.<br/>
/// NOT thread-safe.
/// </summary>
public class MicroSleep : IDisposable
{
    /// <summary>
    /// Mode of MicroSleep.
    /// </summary>
    public enum Mode
    {
        /// <summary>
        /// MicroSleep instance has been disposed.
        /// </summary>
        Disposed,

        /// <summary>
        /// MicroSleep instance uses the nanosleep method for sleep operations.
        /// </summary>
        Nanosleep,

        /// <summary>
        /// MicroSleep instance uses the WaitableTimerEx method for sleep operations.
        /// </summary>
        WaitableTimerEx,

        /// <summary>
        /// MicroSleep instance uses the timeBeginPeriod method for sleep operations.
        /// </summary>
        TimeBeginPeriod,
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint timeEndPeriod(uint uMilliseconds);

    [DllImport("libc")]
    private static extern int nanosleep(ref Timespec req, ref Timespec rem);

    private struct Timespec
    {
        public Timespec(long seconds, long nanoseconds)
        {
            this.tv_sec = seconds;
            this.tv_nsec = nanoseconds;
        }

        public Timespec(TimeSpan timeSpan)
        {
            this.tv_sec = (long)timeSpan.Seconds;
            this.tv_nsec = (long)((timeSpan.TotalSeconds - this.tv_sec) * 1_000_000_000d);
        }

#pragma warning disable SA1310 // Field names should not contain underscore
        private long tv_sec; // Seconds.
        private long tv_nsec; // Nanoseconds.
#pragma warning restore SA1310 // Field names should not contain underscore
    }

    /// <summary>
    /// Represents a WaitableTimerEx class used for sleep operations.
    /// </summary>
    private class WaitableTimerEx : WaitHandle
    {
        [DllImport("kernel32.dll")]
        private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(SafeWaitHandle hTimer, [In] ref long pDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool fResume);

        public WaitableTimerEx()
        {
            this.SafeWaitHandle = CreateWaitableTimerEx(IntPtr.Zero, default, 2, 0x1F0003); // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS
        }

        /// <summary>
        /// Waits for the specified due time.
        /// </summary>
        /// <param name="dueTime">The due time in microseconds.</param>
        public void Wait(long dueTime)
        {
            try
            {
                SetWaitableTimer(this.SafeWaitHandle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false);
                this.WaitOne();
            }
            catch
            {
            }
        }
    }

    #region FieldAndProperty

    /// <summary>
    /// Gets the current mode of the MicroSleep instance.
    /// </summary>
    public Mode CurrentMode { get; private set; }

    private WaitableTimerEx? waitableTimerEx;

    #endregion

    public MicroSleep()
    {
        try
        {
            var request = default(Timespec);
            var remaining = default(Timespec);
            nanosleep(ref request, ref remaining);
            this.CurrentMode = Mode.Nanosleep;
            return;
        }
        catch
        {
        }

        try
        {
            this.waitableTimerEx = new();
            this.CurrentMode = Mode.WaitableTimerEx;
            return;
        }
        catch
        {
        }

        this.CurrentMode = Mode.TimeBeginPeriod;
        timeBeginPeriod(1);
    }

    /// <summary>
    /// Sleeps for the specified number of microseconds.
    /// </summary>
    /// <param name="microSeconds">The number of microseconds to sleep.</param>
    public void Sleep(int microSeconds)
    {
        if (this.CurrentMode == Mode.Nanosleep)
        {
            try
            {
                var seconds = microSeconds / 1_000_000;
                var request = new Timespec(seconds, (microSeconds * 1_000) - (seconds * 1_000_000_000));
                var remaining = default(Timespec);
                nanosleep(ref request, ref remaining);
            }
            catch
            {
            }
        }
        else if (this.waitableTimerEx is { } waitableTimer)
        {
            waitableTimer.Wait(microSeconds * -10);
        }
        else
        {
            Thread.Sleep(microSeconds / 1000);
        }
    }

    /// <summary>
    /// Disposes the MicroSleep instance.
    /// </summary>
    public void Dispose()
    {
        if (this.CurrentMode == Mode.TimeBeginPeriod)
        {
            timeEndPeriod(1);
        }

        this.waitableTimerEx?.Dispose();
        this.waitableTimerEx = default;

        this.CurrentMode = Mode.Disposed;
    }
}
