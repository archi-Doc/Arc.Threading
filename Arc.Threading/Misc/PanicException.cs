// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;

namespace Arc.Threading;

/// <summary>
/// Represents an exception that is thrown when a fatal error occurs and the application must be aborted.
/// </summary>
public class PanicException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PanicException"/> class.
    /// </summary>
    public PanicException()
        : base()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PanicException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public PanicException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PanicException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of this exception.</param>
    public PanicException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
