// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Provides methods to estimate the memory size of structs and classes.
/// </summary>
public static class EstimateSize
{
    private static object? sink; // Prevents the JIT from eliminating the allocations (escape analysis).

    /// <summary>
    /// Estimates the size in bytes of a struct type.
    /// </summary>
    /// <typeparam name="TStruct">The struct type to estimate the size of.</typeparam>
    /// <returns>The size in bytes of the struct.</returns>
    public static int Struct<TStruct>()
        where TStruct : allows ref struct
    {
        return Unsafe.SizeOf<TStruct>();
    }

    /// <summary>
    /// Estimates the size in bytes of a class instance by allocating multiple instances and averaging the allocated memory.
    /// </summary>
    /// <typeparam name="TClass">The class type to estimate the size of. Must have a parameterless constructor.</typeparam>
    /// <returns>The average allocation per constructor call, including objects allocated by the constructor.</returns>
    public static int Class<TClass>()
        where TClass : class, new()
    {
        const int N = 1000;
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < N; i++)
        {
            Volatile.Write(ref sink, new TClass());
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Volatile.Write(ref sink, null);

        return (int)((after - before) / N);
    }

    /// <summary>
    /// Estimates the size in bytes of an object created by a specified factory delegate by allocating multiple instances and averaging the allocated memory.
    /// </summary>
    /// <param name="factory">A delegate that creates an object instance.</param>
    /// <returns>The estimated size in bytes of the created object.</returns>
    public static int Constructor(Func<object> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        const int N = 1000;
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < N; i++)
        {
            Volatile.Write(ref sink, factory());
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Volatile.Write(ref sink, null);

        return (int)((after - before) / N);
    }
}
