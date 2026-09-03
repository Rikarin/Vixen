// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Raven.Transpile;

/// <summary>
///     A texture and a sampler that were separate in SPIR-V and are one uniform in GLSL.
/// </summary>
/// <param name="Name">The name of the emitted <c>sampler2D</c> (or cube, or array) uniform.</param>
/// <param name="Image">The Raven texture the pair came from.</param>
/// <param name="Sampler">
///     The Raven sampler, or the empty string where SPIR-V used a plain <c>OpTypeImage</c> with no
///     sampler of its own — a fetch rather than a sample.
/// </param>
/// <remarks>
///     ⚠ <b>This is the part a host cannot recover from the source text.</b> Vulkan GLSL and
///     SPIR-V keep a texture and a sampler apart; GL has only the combined object, so SPIRV-Cross
///     invents one uniform per <em>pair</em> that is actually used. A shader that samples one
///     texture through two samplers therefore emits two uniforms, and a host that assumed a uniform
///     per texture binds one of them and leaves the other on texture unit zero — which reads as
///     "one material is wrong" rather than as a binding bug.
///     <para>
///         Named for what it is rather than <c>CombinedImageSampler</c>, which is Silk.NET's struct
///         for the same idea: this file and <see cref="SpirvCrossTranspiler" /> both see that one,
///         and two types of one name in scope makes every mention of it ambiguous.
///     </para>
/// </remarks>
readonly record struct CombinedSampler(string Name, string Image, string Sampler);

/// <summary>One entry point after cross-compilation.</summary>
/// <param name="Source">The GLSL, complete with its <c>#version</c> line.</param>
/// <param name="CombinedSamplers">
///     The pairs <see cref="SpirvCrossTranspiler" /> created, named after the texture they sample.
/// </param>
readonly record struct TranspiledShader(string Source, IReadOnlyList<CombinedSampler> CombinedSamplers);
