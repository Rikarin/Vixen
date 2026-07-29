// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.Windows;

/// <summary>What Windows knows that SDL does not.</summary>
/// <remarks>
///     <para>
///         Four services, replaced for four different reasons: the shell has file pickers and SDL 2
///         has none; the clipboard carries images and application-defined formats and SDL carries
///         text; the scheduler will pin a thread and tell us which cores are the fast ones; and the
///         power status has a battery-saver flag nobody else reports.
///     </para>
///     <para>
///         Only the dialogs are disposed, and only for their gate. Nothing here holds an
///         operating-system handle beyond the call that uses it — the clipboard opens and closes
///         within each operation, and a file dialog's apartment ends with the thread that ran it.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformSupplement : IPlatformSupplement {
    WindowsDialogs? dialogs;

    /// <inheritdoc />
    public string Name => "Windows";

    /// <inheritdoc />
    public PlatformServices Augment(in PlatformServices baseline) {
        dialogs?.Dispose();
        dialogs = new(baseline.Dialogs);

        return baseline with {
            Clipboard = new WindowsClipboard(baseline.Clipboard),
            Dialogs = dialogs,
            Power = new WindowsPowerInfo(baseline.Power),
            Processors = new WindowsProcessorTopology(),

            // Earned by the pickers. The capability covers pickers and message boxes together, and
            // the portable implementation withholds it because it only had the second half.
            Capabilities = baseline.Capabilities | PlatformCapabilities.NativeDialogs
        };
    }

    /// <inheritdoc />
    public void Dispose() {
        dialogs?.Dispose();
        dialogs = null;
    }
}
