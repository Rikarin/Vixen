// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.OpenXR;

namespace Vixen.Xr.OpenXR;

/// <summary>A call into the runtime that failed.</summary>
public sealed class OpenXrException : Exception {
    /// <summary>Creates one for a failed call.</summary>
    /// <param name="call">Which call.</param>
    /// <param name="result">What it returned.</param>
    public OpenXrException(string call, Result result)
        : base($"{call} returned {result}.") => Result = result;

    /// <summary>Creates one with a message.</summary>
    /// <param name="message">The message.</param>
    public OpenXrException(string message) : base(message) { }

    /// <summary>Creates one.</summary>
    public OpenXrException() { }

    /// <summary>Creates one wrapping another.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">What went wrong underneath.</param>
    public OpenXrException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>What the runtime returned, or <see cref="Result.Success" /> if this is not about a call.</summary>
    public Result Result { get; }
}

/// <summary>Turning a <see cref="Result" /> into either nothing or an exception.</summary>
/// <remarks>
///     <para>
///         Every OpenXR entry point returns a status and most of them cannot fail in a way a game can
///         handle — a swapchain that will not create is not a situation with a fallback. So the
///         default is to throw with the name of the call in the message, because a bare
///         <c>ErrorValidationFailure</c> with no call site is the least useful diagnostic there is.
///     </para>
///     <para>
///         <b>The successful results are not all <see cref="Result.Success" />.</b>
///         <c>XR_SESSION_LOSS_PENDING</c> and <c>XR_EVENT_UNAVAILABLE</c> are successes that mean
///         something, which is why <see cref="Succeeded" /> tests the sign rather than the value —
///         negative is a failure and everything else is a qualified success.
///     </para>
/// </remarks>
static class OpenXrResult {
    /// <summary>Whether a result is any kind of success.</summary>
    public static bool Succeeded(Result result) => (int)result >= 0;

    /// <summary>Throws unless a call succeeded.</summary>
    /// <param name="result">What it returned.</param>
    /// <param name="call">Its name, for the message.</param>
    /// <exception cref="OpenXrException">It failed.</exception>
    public static void Check(Result result, string call) {
        if (!Succeeded(result)) {
            throw new OpenXrException(call, result);
        }
    }
}
