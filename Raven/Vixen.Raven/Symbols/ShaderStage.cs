// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>
///     The pipeline stage a method is an entry point for, taken from a stage
///     attribute (<c>[VertexShader]</c>, <c>[PixelShader]</c>, …).
/// </summary>
public enum ShaderStage {
    None,
    Vertex,
    Pixel,
    Geometry,
    Compute
}

/// <summary>How a shader field maps onto a GPU binding.</summary>
public enum ResourceKind {
    /// <summary>Not a resource — a plain field or local.</summary>
    None,

    /// <summary>A scalar/vector/matrix shader field: a uniform / constant-buffer entry.</summary>
    Uniform,
    Texture,
    Sampler
}

/// <summary>
///     The descriptor set a binding belongs to, named for how often it changes.
/// </summary>
/// <remarks>
///     <para>
///         The engine's fixed four-set convention (docs/plan/05 § "Descriptor model"), and the
///         values are the set indices themselves: a binding decorated <c>PerMaterial</c> lands in
///         set 2 in SPIR-V and in <c>layout(set = 2, …)</c> in GLSL.
///     </para>
///     <para>
///         Sets are named rather than numbered in source because the number is the engine's to
///         choose, not the shader author's — and because <c>[PerDraw]</c> says why a value is
///         where it is, which <c>[Set(3)]</c> does not. A shader cannot spell set 7 by accident.
///     </para>
/// </remarks>
public enum ResourceSet {
    /// <summary>Set 0 — camera, time, lighting environment. Bound once per frame.</summary>
    PerFrame = 0,

    /// <summary>Set 1 — shadow matrices and view-dependent buffers. Bound once per view.</summary>
    PerView = 1,

    /// <summary>
    ///     Set 2 — material constants and textures. The default for a shader's own fields,
    ///     because a shader's own <c>var</c>s are its material parameters.
    /// </summary>
    PerMaterial = 2,

    /// <summary>Set 3 — transforms and instance data. Bound per draw.</summary>
    PerDraw = 3
}
