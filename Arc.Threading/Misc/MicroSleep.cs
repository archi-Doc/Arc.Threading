// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

#pragma warning disable SA1124
#pragma warning disable SA1300 // Element should begin with upper-case letter

namespace Arc.Threading;

/// <summary>
/// Sleeps for a duration specified in microseconds using platform timers. Not thread-safe.
/// </summary>
/// <remarks>
/// Dispose the instance when it is no longer needed.
/// In <see cref="MicroSleepMode.TimeBeginPeriod"/> mode, it raises the system-wide timer resolution to 1 ms until it is disposed or finalized.
/// </remarks>
public class MicroSleep : IDisposable
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint timeEndPeriod(uint uMilliseconds);

    [DllImport("libc", SetLastError = true)]
    private static extern int nanosleep(ref Timespec req, ref Timespec rem);

    private struct Timespec
    {// struct timespec { time_t tv_sec; long tv_nsec; }: both are pointer-sized (nint), not always 64-bit.
        public Timespec(nint seconds, nint nanoseconds)
        {
            this.tv_sec = seconds;
            this.tv_nsec = nanoseconds;
        }

#pragma warning disable SA1310 // Field names should not contain underscore
        private nint tv_sec; // Seconds.
        private nint tv_nsec; // Nanoseconds.
#pragma warning restore SA1310 // Field names should not contain underscore
    }

    /// <summary>
    /// Represents a WaitableTimerEx class used for sleep operations.
    /// </summary>
    private class WaitableTimerEx : WaitHandle
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(SafeWaitHandle hTimer, [In] ref long pDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool fResume);

        public WaitableTimerEx()
        {
            this.SafeWaitHandle = CreateWaitableTimerEx(IntPtr.Zero, default, 2, 0x1F0003); // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS
            if (this.SafeWaitHandle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                this.Dispose();
                throw new Win32Exception(error);
            }
        }

        /// <summary>
        /// Waits for the specified due time.
        /// </summary>
        /// <param name="dueTime">A negative relative due time in 100-nanosecond units.</param>
        public void Wait(long dueTime)
        {
            if (!SetWaitableTimer(this.SafeWaitHandle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            this.WaitOne();
        }
    }

    /// <summary>
    /// Owns a 1 ms timer-period request, and releases it on disposal or finalization.
    /// </summary>
    private sealed class TimerPeriod : IDisposable
    {
        private bool released;

        public TimerPeriod()
        {
            timeBeginPeriod(1);
        }

        ~TimerPeriod()
        {
            this.Release();
        }

        public void Dispose()
        {
            this.Release();
            GC.SuppressFinalize(this);
        }

        private void Release()
        {
            if (!this.released)
            {
                this.released = true;
                timeEndPeriod(1);
            }
        }
    }

    #region FieldAndProperty

    /// <summary>
    /// Gets the sleep method used by this instance, or <see cref="MicroSleepMode.Disposed"/> after disposal.
    /// </summary>
    public MicroSleepMode CurrentMode { get; private set; }

    private WaitableTimerEx? waitableTimerEx;
    private TimerPeriod? timerPeriod; // Fallback mode only; finalizable so an undisposed instance does not keep the system timer period.

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="MicroSleep"/> class.<br/>
    /// Uses nanosleep on non-Windows platforms, or a high-resolution waitable timer on Windows.
    /// If the timer cannot be created, falls back to <see cref="MicroSleepMode.TimeBeginPeriod"/>.
    /// </summary>
    public MicroSleep()
    {
        if (!OperatingSystem.IsWindows())
        {
            var request = default(Timespec);
            SleepUnix(request);
            this.CurrentMode = MicroSleepMode.Nanosleep;
            return;
        }

        try
        {
            this.waitableTimerEx = new();
            this.CurrentMode = MicroSleepMode.WaitableTimerEx;
            return;
        }
        catch (Win32Exception)
        {
        }

        this.CurrentMode = MicroSleepMode.TimeBeginPeriod;
        this.timerPeriod = new();
    }

    /// <summary>
    /// Blocks the calling thread for the specified number of microseconds. Actual scheduling precision depends on the platform.
    /// </summary>
    /// <param name="microseconds">The number of microseconds to sleep. 0 returns immediately.</param>
    /// <remarks>In <see cref="MicroSleepMode.TimeBeginPeriod"/> mode, the duration is rounded up to whole milliseconds.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="microseconds"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
    /// <exception cref="Win32Exception">The native sleep operation fails.</exception>
    public void Sleep(int microseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(microseconds);
        ObjectDisposedException.ThrowIf(this.CurrentMode == MicroSleepMode.Disposed, this);
        if (microseconds == 0)
        {
            return;
        }

        if (this.CurrentMode == MicroSleepMode.Nanosleep)
        {
            var seconds = microseconds / 1_000_000;
            var request = new Timespec(seconds, (microseconds - (seconds * 1_000_000)) * 1_000); // < 1e9 fits in 32-bit.
            SleepUnix(request);
        }
        else if (this.waitableTimerEx is { } waitableTimer)
        {
            waitableTimer.Wait((long)microseconds * -10); // Negative value: relative time in 100-nanosecond units.
        }
        else
        {
            Thread.Sleep((int)((microseconds + 999L) / 1000));
            GC.KeepAlive(this); // Keep the timer-period owner alive during the sleep.
        }
    }

    /// <summary>
    /// Releases the waitable timer or the timer-resolution request. Further calls to <see cref="Sleep(int)"/> throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        this.timerPeriod?.Dispose();
        this.timerPeriod = default;

        this.waitableTimerEx?.Dispose();
        this.waitableTimerEx = default;

        this.CurrentMode = MicroSleepMode.Disposed;
    }

    private static void SleepUnix(Timespec request)
    {
        var remaining = default(Timespec);
        while (nanosleep(ref request, ref remaining) != 0)
        {
            const int Interrupted = 4; // EINTR on supported Unix platforms.
            var error = Marshal.GetLastPInvokeError();
            if (error != Interrupted)
            {
                throw new Win32Exception(error);
            }

            request = remaining;
        }
    }
}
