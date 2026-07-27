// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>One node of a model's hierarchy.</summary>
/// <remarks>
///     A flat array with parent indices rather than a tree of references. Parents precede children,
///     so composing world transforms is one forward pass with no recursion and no visited set — and
///     the array serialises without a graph walk.
/// </remarks>
[DataContract("ModelNode")]
public sealed record ModelNode {
    /// <summary>What it is called in the authoring tool.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Its parent's index, or −1 for the root.</summary>
    public int Parent { get; set; } = -1;

    /// <summary>Its transform relative to its parent.</summary>
    public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;
}

/// <summary>One drawable piece of a model: a mesh, at a node, with a material.</summary>
/// <remarks>
///     The mesh is named rather than pointed at. An import writes each mesh as its own chunk under a
///     sub-asset, and the ids of those chunks are assigned by the pipeline <em>after</em> the
///     importer has finished — so nothing the importer writes can hold one. The name is what the
///     build turns into an address (<c>characters/hero#Hero_Mesh</c>), and it is also the thing a
///     person reads in a debugger.
/// </remarks>
[DataContract("ModelPart")]
public sealed record ModelPart {
    /// <summary>The mesh's sub-asset name.</summary>
    public string Mesh { get; set; } = string.Empty;

    /// <summary>Which node it hangs off.</summary>
    public int Node { get; set; }

    /// <summary>Which of the model's materials it is drawn with.</summary>
    public int Material { get; set; }
}

/// <summary>A model as it comes out of an authoring tool: a hierarchy, and what hangs off it.</summary>
/// <remarks>
///     <para>
///         <b>Editor-domain, not runtime-domain</b>, in
///         [doc 08](../../docs/plan/08-asset-pipeline-and-addressables.md)'s split. What a player
///         loads is a packed vertex buffer with a resolved pipeline; what an import produces is
///         this — named parts, per-vertex arrays, and enough structure for a compiler to make those
///         decisions with the whole model in hand. The compiler is what does meshlets, LODs, index
///         reordering and vertex-layout packing, and none of them can be decided one mesh at a time.
///     </para>
///     <para>
///         <b>Right-handed, Y-up, −Z forward</b>, which is
///         <see cref="Vixen.Core.Mathematics.Vector3" />'s convention and also glTF's and Assimp's
///         own. No axis conversion happens on import, which is why none is written down as
///         happening: a file authored Z-up arrives Z-up, and correcting it is a transform on the
///         root node that an artist can see rather than a silent rotation in a build step.
///     </para>
/// </remarks>
[DataContract("Model")]
public sealed record ModelData {
    /// <summary>What the model is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The hierarchy, parents before children.</summary>
    public ModelNode[] Nodes { get; set; } = [];

    /// <summary>What is drawn, and where.</summary>
    public ModelPart[] Parts { get; set; } = [];

    /// <summary>The materials the parts index into, by name.</summary>
    /// <remarks>
    ///     Names and not references, because an import cannot invent a material asset — that is an
    ///     authored thing with a GUID of its own. What the importer knows is what the file called
    ///     the material, which is what an artist matches against when they wire the real one up.
    /// </remarks>
    public string[] Materials { get; set; } = [];

    /// <summary>The skeleton's sub-asset name, or empty if nothing here is skinned.</summary>
    public string Skeleton { get; set; } = string.Empty;

    /// <summary>The animation clips' sub-asset names.</summary>
    public string[] Animations { get; set; } = [];

    /// <summary>Everything the model occupies, in its own space.</summary>
    public BoundingBox Bounds { get; set; }
}
