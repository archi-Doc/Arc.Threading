// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Collections.Generic;
using System.Threading;

namespace Arc.Threading;

public class ExecutionRoot : ExecutionCore
{
#pragma warning disable SA1401 // Fields should be private
#pragma warning disable SA1304 // Non-private readonly fields should begin with upper-case letter

    internal readonly Lock SyncObject = new();
    internal readonly Dictionary<long, ExecutionCore> IdToCore = new(); // SyncObject

#pragma warning restore SA1304 // Non-private readonly fields should begin with upper-case letter
#pragma warning restore SA1401 // Fields should be private

    public ExecutionRoot()
        : base()
    {
    }

    public ExecutionCore? Find(long id)
    {
        using (this.SyncObject.EnterScope())
        {
            this.IdToCore.TryGetValue(id, out var core);
            return core;
        }
    }

    public bool FindCancellationToken(long id, out CancellationToken cancellationToken)
    {
        if (this.Find(id) is { } core)
        {
            cancellationToken = core.CancellationToken;
            return true;
        }
        else
        {
            cancellationToken = default;
            return false;
        }
    }
}
