// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Runtime.CompilerServices;

namespace Arc.Threading;

/// <summary>
/// Provides methods to estimate the memory size of structs and classes.
/// </summary>
public static class EstimateSize
{
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
    /// <returns>The estimated size in bytes of a class instance.</returns>
    public static int Class<TClass>()
        where TClass : class, new()
    {
        const int N = 1000;
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < N; i++)
        {
            var obj = new TClass();
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        return (int)((after - before) / N);
    }

    /// <summary>
    /// Estimates the size in bytes of an object created by a specified constructor delegate by allocating multiple instances and averaging the allocated memory.
    /// </summary>
    /// <param name="constructor">A delegate that constructs an object instance.</param>
    /// <returns>The estimated size in bytes of the constructed object.</returns>
    public static int Constructor(Func<object> constructor)
    {
        const int N = 1000;
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < N; i++)
        {
            var obj = constructor();
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        return (int)((after - before) / N);
    }
}
