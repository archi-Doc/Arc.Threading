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
    /// Returns the size of the managed representation of a type, in bytes.
    /// </summary>
    /// <typeparam name="TStruct">The type to measure, including ref structs.</typeparam>
    /// <returns>The value size for value types, or the reference size for reference types.</returns>
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

        try
        {
            for (int i = 0; i < N; i++)
            {
                Volatile.Write(ref sink, new TClass());
            }

            return (int)((GC.GetAllocatedBytesForCurrentThread() - before) / N);
        }
        finally
        {
            Volatile.Write(ref sink, null);
        }
    }

    /// <summary>
    /// Estimates the average allocation per factory call on the current thread.
    /// </summary>
    /// <param name="factory">A delegate that creates an object instance.</param>
    /// <returns>The average bytes allocated, including allocations made inside the factory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    public static int Constructor(Func<object> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        const int N = 1000;
        long before = GC.GetAllocatedBytesForCurrentThread();

        try
        {
            for (int i = 0; i < N; i++)
            {
                Volatile.Write(ref sink, factory());
            }

            return (int)((GC.GetAllocatedBytesForCurrentThread() - before) / N);
        }
        finally
        {
            Volatile.Write(ref sink, null);
        }
    }
}
