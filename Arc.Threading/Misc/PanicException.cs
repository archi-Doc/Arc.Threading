// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;

namespace Arc.Threading;

/// <summary>
/// Represents an exception that is thrown when a fatal error occurs and the application must be aborted.
/// </summary>
public class PanicException : Exception
{
    public PanicException()
        : base()
    {
    }

    public PanicException(string message)
        : base(message)
    {
    }

    public PanicException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
