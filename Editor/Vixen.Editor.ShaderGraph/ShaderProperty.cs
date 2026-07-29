// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.ShaderGraph;

/// <summary>Something the generated shader asks a material to supply.</summary>
/// <param name="Name">What it is declared as.</param>
/// <param name="Type">Its Raven type — <c>float4</c>, <c>Texture2D</c>, and so on.</param>
/// <remarks>
///     The list of these is what a material has to set for a graph to draw anything, so it is on the
///     compilation rather than recovered by reading the emitted text back — which is what a panel
///     that wanted to show it would otherwise have to do.
/// </remarks>
public readonly record struct ShaderGraphProperty(string Name, string Type);

/// <summary>A node that names a material property rather than computing a value.</summary>
/// <remarks>
///     <para>
///         <b>The name is authored, and it is stored as a graph text rather than on the node.</b> A
///         node's own C# property is scaffolding the compiler builds and throws away — nothing writes
///         it back to the file — so a name kept there is a name every graph in the project shares.
///         <see cref="ShaderProperties.Key" /> is where it actually lives, which is
///         <c>GraphNode.Texts</c>: the same place the graphics compositor keeps a target's name, for
///         the same reason, and it round-trips and undoes like any other edit.
///     </para>
///     <para>
///         ⚠ <b>Two nodes under one name are one binding, deliberately.</b>
///         <see cref="RavenEmitter.Uniform" /> declares once per name, so two texture nodes reading
///         <c>albedo</c> read one texture — which is what an author sampling the same map twice
///         means. Two nodes under one name and <i>different types</i> is the error that emitter
///         raises, and it is why renaming is a thing the editor has to offer.
///     </para>
/// </remarks>
public interface IShaderPropertyNode {
    /// <summary>The Raven type the property is declared as.</summary>
    string PropertyType { get; }

    /// <summary>What it is called when the author has not renamed it.</summary>
    string DefaultProperty { get; }

    /// <summary>What it is called on this instance.</summary>
    string PropertyName { get; }
}

/// <summary>Where a property node's name is kept.</summary>
public static class ShaderProperties {
    /// <summary>The key the name is stored under, in <c>GraphNode.Texts</c>.</summary>
    /// <remarks>
    ///     Spelled once, here, because it is written by an editor and read by a node — two assemblies,
    ///     and a string literal in each is the pair that drifts the day one of them is renamed.
    /// </remarks>
    public const string Key = "Property";

    /// <summary>The name a bound node should use: the author's, or the type's default.</summary>
    /// <param name="node">The node, as the compiler bound it.</param>
    /// <param name="fallback">What it is called when nothing was typed.</param>
    /// <returns>The name to declare.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node" /> is null.</exception>
    public static string NameOf(Node node, string fallback) {
        ArgumentNullException.ThrowIfNull(node);

        // An unbound instance answers with the fallback rather than throwing, which is what lets a
        // panel create one to ask what kind of property it is before anything has compiled.
        return node.Binding.Text(Key) is { Length: > 0 } named ? named : fallback;
    }
}
