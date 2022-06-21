// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1009 // Closing parenthesis should be spaced correctly

namespace Benchmark.Obsolete;

public class AsyncManualResetEvent
{
    private volatile TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task AsTask => this.tcs.Task;

    public void Set() => this.tcs.TrySetResult();

    public void Reset()
    {
        while (true)
        {
            var tcs = this.tcs;
            if (!tcs.Task.IsCompleted ||
                Interlocked.CompareExchange(ref this.tcs, new TaskCompletionSource(), tcs) == tcs)
            {
                return;
            }
        }
    }
}

/*public class AsyncManualResetEvent
{// I know this code is nasty...
    private static Func<object?, TaskCreationOptions, Task<bool>> createTask;
    private static Action<Task<bool>, bool> trySetResult;

    private volatile Task<bool> task;

    static AsyncManualResetEvent()
    {
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var argsType = new[] { typeof(object), typeof(TaskCreationOptions), };
        var args = argsType.Select(Expression.Parameter).ToList();
        var constructor = typeof(Task<bool>).GetConstructor(flags, argsType);
        createTask = Expression.Lambda<Func<object?, TaskCreationOptions, Task<bool>>>(Expression.New(constructor!, args), args).Compile();

        argsType = new[] { typeof(bool), };
        var method = typeof(Task<bool>).GetMethod("TrySetResult", flags, argsType)!;
        // args = argsType.Select(Expression.Parameter).ToList();
        var argInstance = Expression.Parameter(typeof(Task<bool>));
        var arg1 = Expression.Parameter(typeof(bool));
        trySetResult = Expression.Lambda<Action<Task<bool>, bool>>(Expression.Call(argInstance, method, arg1), argInstance, arg1).Compile();
    }

    public AsyncManualResetEvent()
    {
        this.task = createTask(null, TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task AsTask => this.task;

    public void Set()
    {
        trySetResult(this.task, true);
    }

    public void Reset()
    {
        while (true)
        {
            var t = this.task;
            if (!t.IsCompleted ||
                Interlocked.CompareExchange(ref this.task, createTask(null, TaskCreationOptions.RunContinuationsAsynchronously), t) == t)
            {
                return;
            }
        }
    }
}*/

/*public class AsyncManualResetEvent
{// I know this code is nasty...
    private static Func<Task> createTask;
    private static Action<Task> trySetResult;

    private volatile Task task;

    static AsyncManualResetEvent()
    {
        createTask = Expression.Lambda<Func<Task>>(Expression.New(typeof(Task))).Compile();

        var method = typeof(Task).GetMethod("TrySetResult", BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!;
        var arg = Expression.Parameter(typeof(Task));
        trySetResult = Expression.Lambda<Action<Task>>(Expression.Call(arg, method), arg).Compile();
    }

    public AsyncManualResetEvent()
    {
        this.task = createTask();
    }

    public Task AsTask => this.task;

    public void Set() => trySetResult(this.task);

    public void Reset()
    {
        while (true)
        {
            var t = this.task;
            if (!t.IsCompleted ||
                Interlocked.CompareExchange(ref this.task, createTask(), t) == t)
            {
                return;
            }
        }
    }
}*/
