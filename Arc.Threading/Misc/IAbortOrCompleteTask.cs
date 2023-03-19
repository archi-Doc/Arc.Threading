// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading.Tasks;

namespace Arc.Threading;

public interface IAbortOrCompleteTask
{
    Task<AbortOrComplete> GetTask(TimeSpan timeToWait);

    Task<AbortOrComplete> GetTask() => this.GetTask(TimeSpan.MinValue);
}
