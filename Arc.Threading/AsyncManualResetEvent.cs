// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1009 // Closing parenthesis should be spaced correctly

namespace Arc.Threading;

public class AsyncManualResetEvent
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
}

/*public class AsyncManualResetEvent
{
    private volatile TaskCompletionSource tcs = new();

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
}*/
