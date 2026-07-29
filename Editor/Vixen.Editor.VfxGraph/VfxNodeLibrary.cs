// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.VfxGraph;

/// <summary>The node types a VFX graph is edited against, as one call.</summary>
/// <remarks>
///     <para>
///         <b>The generator emits <c>NodeTypes.Register</c>; this is the name a caller outside this
///         assembly can find.</b> Every test and every host was writing the same two lines — a fresh
///         registry, then the generated registration — and a second caller getting that pair wrong
///         is a graph whose node library is silently empty, which shows up as every node in a saved
///         file being reported as unknown rather than as an error at the point of the mistake.
///     </para>
///     <para>
///         ⚠ <b>One registry per host, not one per document.</b> A node library is a property of the
///         build rather than of a file — the same argument <c>CompositorEditorFactory</c> makes — and
///         two open effects disagreeing about what <c>Vfx/Update/Gravity</c> is would be two
///         compilers over one authored graph.
///     </para>
/// </remarks>
public static class VfxNodeLibrary {
    /// <summary>Builds a registry holding every node type this assembly ships.</summary>
    /// <returns>The registry.</returns>
    public static NodeTypeRegistry Create() {
        var registry = new NodeTypeRegistry();

        NodeTypes.Register(registry);
        return registry;
    }
}
