// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.MacOS;

/// <summary>What macOS knows that SDL does not.</summary>
/// <remarks>
///     <para>
///         The three that are here are the pickers, the pasteboard beyond text, and the thermal
///         state — which is the only desktop that reports one, and the reason
///         <see cref="ThermalState" /> exists at all.
///     </para>
///     <para>
///         The fourth, thread affinity, is <em>not</em> an improvement here: macOS does not offer
///         it and offers quality-of-service classes instead. The topology is still replaced, because
///         the counts it gives — how many performance cores, how many efficiency cores — are what
///         sizing a worker pool on an Apple silicon machine needs, and SDL reports one number for
///         both kinds.
///     </para>
///     <para>
///         Everything degrades to the portable implementation when the frameworks cannot be loaded,
///         which is what a Mac with a broken system installation looks like and is not a case worth
///         throwing over.
///     </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacOSPlatformSupplement : IPlatformSupplement {
    /// <inheritdoc />
    public string Name => "macOS";

    /// <inheritdoc />
    public PlatformServices Augment(in PlatformServices baseline) {
        if (!ObjC.Load()) {
            return baseline with { Processors = new MacOSProcessorTopology() };
        }

        return baseline with {
            Clipboard = new MacOSClipboard(baseline.Clipboard),
            Dialogs = new MacOSDialogs(baseline.Dialogs),
            Power = new MacOSPowerInfo(baseline.Power),
            Processors = new MacOSProcessorTopology(),
            Capabilities = baseline.Capabilities | PlatformCapabilities.NativeDialogs
        };
    }

    /// <inheritdoc />
    public void Dispose() { }
}
