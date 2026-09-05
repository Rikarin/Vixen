// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.Texturing;

/// <summary>The node types a <c>.vxtexgraph</c> may contain.</summary>
/// <remarks>
///     <para>
///         <b>One line, because the generator already wrote the list.</b>
///         <c>Vixen.Editor.TextureGraph.NodeTypes</c> is emitted by
///         <c>Vixen.Editor.NodeGraph.Generator</c> over that assembly's own <c>[Node]</c> classes, so
///         the library here cannot drift from the library the compiler walks: adding a node there
///         puts it in this menu with no edit anywhere.
///     </para>
///     <para>
///         ⚠ <b>And it is the only part of that assembly a plugin can reach.</b> The eight node
///         classes are <c>internal</c>, <c>TextureNode</c> is <c>internal</c>, and
///         <c>TextureGraphCompiler</c> is <c>internal</c> — the generated registration is
///         <c>public</c> only because the generator emits it that way. So a plugin can offer an
///         author every node and cannot compile what they wire; see this project's README.
///     </para>
/// </remarks>
static class TextureNodeLibrary {
    /// <summary>A registry holding this build's texture nodes.</summary>
    /// <returns>The registry.</returns>
    /// <remarks>
    ///     A fresh one per call, for <c>NodeTypeRegistry</c>'s own reason: nothing here is global, and
    ///     two panels wanting different libraries is a thing that happens once a compound library
    ///     lands.
    /// </remarks>
    public static NodeTypeRegistry Create() {
        var registry = new NodeTypeRegistry();

        TextureGraph.NodeTypes.Register(registry);

        return registry;
    }
}
