// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Benchmark;

#pragma warning disable SA1313 // Parameter names should begin with lower-case letter

/// <summary>
/// Provides a set of methods that you can use to measure elapsed time for benchmark.<br/>
/// { Start() -> Stop() } x repetition -> GetResult().
/// </summary>
public class BenchTimer
{
    public BenchTimer()
    {
        this.frequencyR = 1.0d / (double)Stopwatch.Frequency;
    }

    /// <summary>
    /// Starts measuring elapsed time.
    /// </summary>
    public void Start()
    {
        this.lastTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Removes all elapsed time records and starts measuring elapsed time.
    /// </summary>
    public void Restart()
    {
        this.Clear();
        this.Start();
    }

    /// <summary>
    /// Stops measuring elapsed time.
    /// </summary>
    public void Stop()
    {
        var timestamp = Stopwatch.GetTimestamp();
        if (this.lastTimestamp == 0)
        {
            this.lastTimestamp = timestamp;
        }

        this.records.Add(timestamp - this.lastTimestamp);
        this.lastTimestamp = timestamp;
    }

    /// <summary>
    /// Stops measuring elapsed time.
    /// </summary>
    /// <param name="caption">A caption to be added to the elapsed time record.</param>
    /// <returns>A string with the caption and the elapsed time in milliseconds ("caption: 123 ms").</returns>
    public string StopAndGetText(string? caption = null)
    {
        this.Stop();
        var timestamp = this.records.LastOrDefault();
        return this.GetText(timestamp, caption);
    }

    public string GetResult(string? caption = null)
    {
        if (this.records.Count == 0)
        {
            return string.Empty;
        }
        else if (this.records.Count == 1)
        {
            var timestamp = this.records.LastOrDefault();
            return this.GetText(timestamp, caption);
        }

        var min = (int)((double)this.records.Min() * this.frequencyR * 1000);
        var max = (int)((double)this.records.Max() * this.frequencyR * 1000);
        var average = (int)((double)this.records.Average() * this.frequencyR * 1000);

        if (caption == null)
        {// 123 ms [4] (Min 100 ms, Max 150 ms)
            return $"{average} ms [{this.records.Count}] (Min {min} ms, Max {max} ms)";
        }
        else
        {// caption: 123 ms [4] (Min 100 ms, Max 150 ms)
            return $"{caption}: {average} ms [{this.records.Count}] (Min {min} ms, Max {max} ms)";
        }
    }

    /// <summary>
    /// Removes all elapsed time records.
    /// </summary>
    public void Clear()
    {
        this.records.Clear();
    }

    private readonly double frequencyR;
    private long lastTimestamp;
    private List<long> records = new();

    private string GetText(long timestamp, string? caption)
    {
        var ms = (int)((double)timestamp * this.frequencyR * 1000);

        if (caption == null)
        {// 123 ms
            return $"{ms} ms";
        }
        else
        {// caption: 123 ms
            return $"{caption}: {ms} ms";
        }
    }

    // private record struct Record(long Ticks, string? Caption);
}
