// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading.Tasks;

namespace Arc.Threading;

public interface IAbortOrCompleteTask
{
    Task<AbortOrComplete> AbortOrCompleteTask(TimeSpan timeToWait);

    Task<AbortOrComplete> AbortOrCompleteTask() => this.AbortOrCompleteTask(TimeSpan.MinValue);
}
