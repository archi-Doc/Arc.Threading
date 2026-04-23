// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using Arc.Collections;

namespace Arc.Threading;

public static class TaskHelper
{
    public const int CtsPoolCapacity = 256;

    public static readonly ObjectPool<CancellationTokenSource> CtsPool = new(() => new CancellationTokenSource(), CtsPoolCapacity);
    public static readonly ObjectPool<Linked2CancellationTokenSource> CtsPool2 = new(() => new Linked2CancellationTokenSource(), CtsPoolCapacity);

    public sealed class Linked2CancellationTokenSource : CancellationTokenSource
    {
        private static readonly Action<object?> LinkedTokenCancelDelegate =
           x => ((CancellationTokenSource)x!).Cancel();

        private CancellationTokenRegistration reg1;
        private CancellationTokenRegistration reg2;

        internal Linked2CancellationTokenSource()
        {
        }

        public void Register(CancellationToken token1, CancellationToken token2)
        {
            this.reg1 = token1.UnsafeRegister(LinkedTokenCancelDelegate, this);
            this.reg2 = token2.UnsafeRegister(LinkedTokenCancelDelegate, this);
        }

        public void Unregister()
        {
            this.reg1.Dispose();
            this.reg1 = default;
            this.reg2.Dispose();
            this.reg2 = default;
        }
    }
}
