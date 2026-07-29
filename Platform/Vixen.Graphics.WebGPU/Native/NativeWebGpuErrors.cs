// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace Vixen.Graphics.WebGPU.Native;

/// <summary>What the implementation says when nothing asked it.</summary>
/// <remarks>
///     <para>
///         WebGPU has no return codes. Almost every call returns <see langword="void" /> or a handle,
///         and everything that went wrong arrives later through <c>uncapturedError</c> — so without
///         this callback a backend that is silently doing nothing looks exactly like one that is
///         working. That is the same lesson <c>VulkanDiagnostics</c> records: validation output that
///         goes nowhere is not validation.
///     </para>
///     <para>
///         <b>The logger is static, and that is a real limitation.</b> The callback is
///         <c>[UnmanagedCallersOnly]</c> — no captured state, by construction — and threading a
///         <c>GCHandle</c> through <c>userdata</c> would keep a logger alive for the life of a
///         device with nothing to release it. One process realistically has one WebGPU device; if it
///         has two, the second one's logger receives both devices' messages. Stated rather than
///         hidden.
///     </para>
/// </remarks>
static unsafe class Errors {
    static ILogger? sink;

    /// <summary>Routes a device's uncaptured errors into a logger.</summary>
    /// <param name="api">The loaded API.</param>
    /// <param name="device">The device.</param>
    /// <param name="logger">Where to send them.</param>
    public static void Attach(Silk.NET.WebGPU.WebGPU api, Device* device, ILogger logger) {
        sink = logger;

        api.DeviceSetUncapturedErrorCallback(
            device,
            (PfnErrorCallback)(nint)(delegate* unmanaged[Cdecl]<ErrorType, byte*, void*, void>)&OnError,
            null
        );
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnError(ErrorType type, byte* message, void* userdata) {
        if (sink is not { } logger) {
            return;
        }

        var text = SilkMarshal.PtrToString((nint)message, NativeStringEncoding.UTF8) ?? "(no message)";
        WebGpuLog.UncapturedError(logger, $"{type}: {text}");
    }
}
