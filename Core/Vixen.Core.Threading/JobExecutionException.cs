// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Threading;

/// <summary>Thrown by <see cref="JobScheduler.Complete(JobHandle)" /> when the job threw.</summary>
/// <remarks>
///     A job runs on a worker thread that has no business dying because user code threw. The
///     exception is captured where it happened — with its original stack trace intact, as the inner
///     exception — and re-thrown on the thread that waits for the job, which is the thread that can
///     actually do something about it.
/// </remarks>
public sealed class JobExecutionException : Exception {
    /// <summary>Creates an exception with the default message.</summary>
    public JobExecutionException() : base("A job threw an exception.") { }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">The message.</param>
    public JobExecutionException(string message) : base(message) { }

    /// <summary>Creates an exception with a message and the exception the job threw.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">What the job threw.</param>
    public JobExecutionException(string message, Exception innerException) : base(message, innerException) { }
}
