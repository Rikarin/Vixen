// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.Web;

/// <summary>Getting bytes back out of an asynchronous JavaScript call.</summary>
/// <remarks>
///     <para>
///         A <c>[JSImport]</c> promise can resolve with a number but not with a
///         <c>JSType.MemoryView</c>: the view is only valid for the duration of the call that
///         produced it, and a promise resolves after that call has returned. So the JavaScript side
///         parks the bytes, resolves with a <em>handle</em>, and this copies them out with a
///         synchronous read and releases the handle.
///     </para>
///     <para>
///         The release is in a <see langword="finally" />, because a handle that is not released is
///         a byte array the page holds until it is closed — and the arrays in question are content
///         bundles.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal static class WebBuffer {
    /// <summary>Copies a parked buffer out and releases it.</summary>
    /// <param name="handle">The handle a JavaScript promise resolved with.</param>
    /// <returns>The bytes, or an empty array for the zero handle.</returns>
    public static byte[] Take(int handle) {
        if (handle == 0) {
            return [];
        }

        try {
            var length = WebInterop.BufferLength(handle);

            if (length == 0) {
                return [];
            }

            var bytes = new byte[length];

            if (!WebInterop.ReadBuffer(handle, bytes)) {
                throw new IOException("The browser's buffer changed size between being measured and being read.");
            }

            return bytes;
        } finally {
            WebInterop.ReleaseBuffer(handle);
        }
    }

    /// <summary>Copies a parked buffer into a caller's span and releases it.</summary>
    /// <param name="handle">The handle.</param>
    /// <param name="destination">Where to put the bytes. Must be at least as long as the buffer.</param>
    /// <returns>How many bytes were copied.</returns>
    public static int TakeInto(int handle, Span<byte> destination) {
        if (handle == 0) {
            return 0;
        }

        try {
            var length = WebInterop.BufferLength(handle);

            if (length == 0) {
                return 0;
            }

            if (destination.Length < length) {
                throw new ArgumentException(
                    $"The buffer holds {length} bytes and the destination is {destination.Length}.",
                    nameof(destination)
                );
            }

            return WebInterop.ReadBuffer(handle, destination[..length]) ? length : 0;
        } finally {
            WebInterop.ReleaseBuffer(handle);
        }
    }

    /// <summary>Copies a parked buffer of doubles out and releases it.</summary>
    /// <param name="handle">The handle.</param>
    /// <param name="count">How many doubles to expect.</param>
    /// <returns>The values, or zeros if the buffer was short.</returns>
    /// <remarks>
    ///     The small structured answers — a <c>HEAD</c>'s length and last-modified, a storage
    ///     estimate's usage and quota — come back this way rather than as a JSON string, because
    ///     parsing two numbers out of JSON is two allocations and a parser to avoid a cast.
    /// </remarks>
    public static double[] TakeDoubles(int handle, int count) {
        var values = new double[count];

        if (handle == 0) {
            return values;
        }

        var bytes = Take(handle);

        if (bytes.Length < count * sizeof(double)) {
            return values;
        }

        for (var index = 0; index < count; index++) {
            values[index] = BitConverter.ToDouble(bytes, index * sizeof(double));
        }

        return values;
    }
}
