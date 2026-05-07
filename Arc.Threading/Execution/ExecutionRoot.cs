// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

public class ExecutionRoot : ExecutionGroup
{
#pragma warning disable SA1401 // Fields should be private
#pragma warning disable SA1304 // Non-private readonly fields should begin with upper-case letter

    internal readonly Lock SyncObject = new();
    // internal readonly Dictionary<long, ExecutionCore> IdToCore = new(); // SyncObject

#pragma warning restore SA1304 // Non-private readonly fields should begin with upper-case letter
#pragma warning restore SA1401 // Fields should be private

    public ExecutionGroup BaseGroup { get; }

    public ExecutionGroup IndependentGroup { get; }

    public ExecutionRoot()
        : base()
    {
        this.BaseGroup = new(this, true, "Base");
        this.IndependentGroup = new(this, true, "Independent");
    }

    public override Task<bool> WaitForTermination(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (this.BaseGroup.CanContinue)
        {
            this.BaseGroup.RequestTermination(RequestTerminationOptions.IncludeIndependent);
        }

        return base.WaitForTermination(timeout, cancellationToken);
    }

    /*public ExecutionCore? Find(long id)
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
    }*/
}
