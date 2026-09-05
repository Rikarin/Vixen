// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>An input a kernel requires and a node does not.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The evaluator binds an op's images positionally over the textures its kernel declares
///         and refuses a count mismatch, so at the <em>plan's</em> level there is no such thing as an
///         optional input.</b> A splatter declares five and reads five. What
///         <c>TexturePlacement</c>'s short overloads already do about that is bind the pattern into
///         every slot the graph does not fill, with the matching <c>…Amount</c> at zero so the slot
///         drops out of the arithmetic — a coverage is never below a threshold of zero, and a map
///         amount of zero removes the map's value from the expression entirely. This is that trick,
///         moved to where a node can decide it per port.
///     </para>
///     <para>
///         <b>It reads <see cref="Node.Binding" /> rather than asking the emitter</b>, because
///         <c>NodeGraphCompiler</c> assigns the binding to the instance before it visits it and
///         <see cref="NodeBinding.IsConnected" /> is exactly the question. Calling
///         <see cref="TextureEmitter.Read" /> on an unwired port is not an alternative: it reports
///         <c>TG0002</c> against the port and returns −1, which is right for a port the node needs
///         and wrong for one it is willing to do without.
///     </para>
///     <para>
///         ⚠ <b>The fallback is an image index and never a fresh one.</b> Allocating a white 1×1 per
///         unfilled slot would be a texture per port in the pool for something no kernel reads — the
///         shape <c>Blend.rvn</c> refused for its own mask, and it would be worse here, where one
///         node has four of them.
///     </para>
/// </remarks>
static class TexturePorts {
    /// <summary>The image arriving at one input, or a stand-in when nothing is wired to it.</summary>
    /// <param name="node">The node being compiled, whose binding says what is connected.</param>
    /// <param name="emitter">Where to read from.</param>
    /// <param name="port">The port's name.</param>
    /// <param name="fallback">
    ///     The image to bind instead — an index this node has already obtained, normally the one the
    ///     kernel's required input carries.
    /// </param>
    /// <returns>The image's index in the plan's image table, or −1 when a wired port produced none.</returns>
    public static int Optional(Node node, TextureEmitter emitter, string port, int fallback) {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(emitter);

        return node.Binding.IsConnected(port) ? emitter.Read(port) : fallback;
    }
}
