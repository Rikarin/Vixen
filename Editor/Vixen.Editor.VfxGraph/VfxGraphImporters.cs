// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets;

namespace Vixen.Editor.VfxGraph;

/// <summary>The importers this assembly brings to a host that links it.</summary>
/// <remarks>
///     <para>
///         <b>#1300's option A.</b> <see cref="VfxImporter" /> lived in <c>Vixen.Editor.Assets</c>, which
///         is why the import pipeline referenced this assembly at all. It lives beside the graph it
///         compiles now, and reaches a registry because every host that links this assembly — the
///         editor (<c>EditorModules</c>), <c>vixen</c> (<c>VixenCommand</c>) and the compiler worker
///         (<c>WorkerHost</c>) — calls <see cref="Link" /> at start-up.
///     </para>
///     <para>
///         ⚠ <b>Into <see cref="ImporterContributions.Linked" />, never <see cref="ImporterContributions.Default" /></b>:
///         a coordinator forwards the assemblies behind <c>Default</c> to its workers as plugins, and a
///         worker that also links this one would have two importers claiming <c>.vxvfx</c>.
///     </para>
/// </remarks>
public static class VfxGraphImporters {
    /// <summary>Links this assembly's importers into a set.</summary>
    /// <param name="linked">The set — <see cref="ImporterContributions.Linked" />, or a test's own.</param>
    public static void Link(ImporterContributions linked) {
        ArgumentNullException.ThrowIfNull(linked);

        linked.Link(new VfxImporter());
    }
}
