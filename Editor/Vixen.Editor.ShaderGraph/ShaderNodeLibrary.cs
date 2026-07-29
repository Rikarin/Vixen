// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.ShaderGraph;

/// <summary>The node types a shader graph is edited against, as one call.</summary>
/// <remarks>
///     <para>
///         <b>The generator emits <c>NodeTypes.Register</c>; this is the name a caller outside this
///         assembly can find.</b> The same pair of lines <c>VfxNodeLibrary</c> exists for — a fresh
///         registry and then the generated registration — and the same failure when a caller gets it
///         wrong: a library that is silently empty, which shows up as every node in a saved file
///         being reported as unknown rather than as an error where the mistake was made.
///     </para>
///     <para>
///         ⚠ <b>One registry per host, not one per document.</b> A node library is a property of the
///         build rather than of a file, so <c>ShaderGraphEditorFactory</c> builds one and hands it to
///         every graph it opens. Two open graphs disagreeing about what <c>Math/Lerp</c> is would be
///         two compilers over one authored file.
///     </para>
/// </remarks>
public static class ShaderNodeLibrary {
    /// <summary>Builds a registry holding every node type this assembly ships.</summary>
    /// <returns>The registry.</returns>
    public static NodeTypeRegistry Create() {
        var registry = new NodeTypeRegistry();

        NodeTypes.Register(registry);
        return registry;
    }
}
