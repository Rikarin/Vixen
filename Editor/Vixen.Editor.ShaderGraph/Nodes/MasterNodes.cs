// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.ShaderGraph.Nodes;

/// <summary>A colour written straight out. No lighting.</summary>
/// <remarks>
///     The master every other one is a variation of, and the one that proves the pipeline: a graph
///     with this node compiles to a shader that does exactly what the graph says and nothing the
///     master added.
/// </remarks>
[Node("Master/Unlit", Summary = "Writes a colour, unlit.")]
public sealed partial class UnlitMasterNode : ShaderMasterNode {
    /// <summary>The colour.</summary>
    [Input(Name = "Colour", Default = [1f, 1f, 1f])]
    public Float3 Colour;

    /// <summary>How opaque it is.</summary>
    [Input(Name = "Alpha")]
    public Scalar Alpha = 1f;

    string result = "";

    /// <inheritdoc />
    protected internal override string Result => result;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) {
        result = "surface";
        emitter.Assign(result, $"float4({Colour}, {Alpha})");
    }
}

/// <summary>An unlit colour, premultiplied, for sprites and UI.</summary>
/// <remarks>
///     Premultiplied because that is the only form that survives a filtered downsample and a nested
///     transparency group without dark fringes — the same convention <c>UiQuad.rvn</c> keeps, and the
///     reason it is a master rather than a node an author remembers to add.
/// </remarks>
[Node("Master/Sprite", Summary = "Writes a premultiplied colour, unlit.")]
public sealed partial class SpriteMasterNode : ShaderMasterNode {
    /// <summary>The colour.</summary>
    [Input(Name = "Colour", Default = [1f, 1f, 1f])]
    public Float3 Colour;

    /// <summary>How opaque it is.</summary>
    [Input(Name = "Alpha")]
    public Scalar Alpha = 1f;

    string result = "";

    /// <inheritdoc />
    protected internal override string Result => result;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) {
        result = "surface";
        emitter.Assign(result, $"float4({Colour} * {Alpha}, {Alpha})");
    }
}

/// <summary>A metal-rough surface a material composes, lit by the scene.</summary>
/// <remarks>
///     <para>
///         <b>The master that makes a graph draw, and the only one that does.</b> The other three
///         emit a whole shader with its own stages and its own transforms — readable, previewable,
///         and impossible for anything in the engine to put on a mesh, because a draw binds a
///         transform record, a light cluster, a shadow atlas and a bindless table by names such a
///         shader does not declare. This one emits an <c>IMaterialSurface</c> instead:
///         <c>MaterialCompiler</c> composes it into <c>CompositeSurface</c> beside
///         <c>MetalRoughnessSurface</c> and the rest, so a graph-authored material is lit, shadowed
///         and drawn by exactly the path every hand-authored one already takes. No new render
///         feature, no new pass, no new binding convention.
///     </para>
///     <para>
///         ⚠ <b>It contributes rather than replaces</b>, which is what <c>inout</c> means in
///         <c>MaterialSurface.rvn</c> and what makes the chain a chain. A channel this master does
///         not write keeps whatever the features before it left — so a graph wired only to
///         <c>BaseColour</c> is a base-colour contribution, not a surface with its roughness reset
///         to whatever a default happened to be.
///     </para>
///     <para>
///         <b>The lighting is the engine's, and that is the difference from <c>Master/PBR</c>.</b>
///         That master carries an inline Lambert-plus-GGX with one directional light out of
///         uniforms, and its own remarks call that the first half of the job; this one writes
///         channels and lets <c>StandardShading</c> and the clustered pass do the shading, which is
///         the "later step" those remarks were describing.
///     </para>
/// </remarks>
[Node("Master/Surface", Summary = "A metal-rough surface a material composes. The one that draws.")]
public sealed partial class SurfaceMasterNode : ShaderMasterNode {
    /// <summary>Albedo for a dielectric, specular tint for a conductor. Linear.</summary>
    [Input(Name = "BaseColour", Default = [1f, 1f, 1f])]
    public Float3 BaseColour;

    /// <summary>How much of a conductor it is, 0..1.</summary>
    [Input(Name = "Metallic")]
    public Scalar Metallic = 0f;

    /// <summary>Perceptual roughness, as authored.</summary>
    [Input(Name = "Roughness")]
    public Scalar Roughness = 0.5f;

    /// <summary>A tangent-space normal, wired only when the graph has one.</summary>
    [Input(Name = "Normal", Default = [0f, 0f, 1f])]
    public Float3 Normal;

    /// <summary>What it emits on its own, in the frame's photometric units.</summary>
    [Input(Name = "Emission", Default = [0f, 0f, 0f])]
    public Float3 Emission;

    /// <summary>How much ambient light reaches it, 0..1.</summary>
    [Input(Name = "Occlusion")]
    public Scalar Occlusion = 1f;

    /// <summary>How opaque it is.</summary>
    [Input(Name = "Alpha")]
    public Scalar Alpha = 1f;

    /// <inheritdoc />
    protected internal override ShaderGraphKind Kind => ShaderGraphKind.Surface;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        emitter.Assign("surfaceBase", BaseColour.ToString());
        emitter.Assign("surfaceMetal", $"saturate({Metallic})");

        // The two halves of the metalness workflow, spelled the way `MetalRoughnessSurface` spells
        // them rather than reinvented — a conductor has no diffuse response and a tinted specular
        // one, and `Brdf.F0FromMetalness` is where that conversion lives exactly once.
        emitter.Emit("d.diffuseColor = surfaceBase * (1f - surfaceMetal)");
        emitter.Emit("d.f0 = Brdf.F0FromMetalness(surfaceBase, surfaceMetal, MaterialDefaults.DielectricF0)");
        emitter.Emit($"d.perceptualRoughness = saturate({Roughness})");
        emitter.Emit($"d.emissive = {Emission}");
        emitter.Emit($"d.occlusion = saturate({Occlusion})");
        emitter.Emit($"d.alpha = {Alpha}");

        // ⚠ Only when it is wired, and the reason is the same one `Texture/Sample 2D` has for its UV
        // port: an unconnected input carries the literal its default made. Here that literal is
        // (0, 0, 1), which `ToWorld` maps back to the geometric normal — so emitting it anyway would
        // be correct and would cost a rotation on every pixel of every graph material that has no
        // normal map. It would also overwrite a normal a *previous* feature in the chain wrote,
        // which is the same value being wrong rather than merely redundant.
        if (Binding.IsConnected("Normal")) {
            emitter.Emit($"d.normalWS = Normals.ToWorld(d.tangentFrame, Math.SafeNormalize({Normal}))");
        }
    }
}

/// <summary>A physically based surface, lit by one directional light.</summary>
/// <remarks>
///     <para>
///         <b>The lighting is inline and deliberately small.</b> A master that called into
///         <c>Vixen.Shaders.Shading</c> would be the right long-term arrangement and would mean a
///         graph's output could not be compiled without the library on the include path — which is
///         exactly what the golden tests do compile it without. So this emits a self-contained
///         Lambert plus GGX with one directional light from uniforms: enough to be a real shader,
///         checkable on its own, and honest about being the first half of the job.
///     </para>
///     <para>
///         Wiring it to the engine's real clustered lighting is a later step, and it is a change to
///         this one method rather than to the graph, the compiler or any other node.
///     </para>
/// </remarks>
[Node("Master/PBR", Summary = "A metal-rough surface, lit.")]
public sealed partial class PbrMasterNode : ShaderMasterNode {
    /// <summary>The surface colour.</summary>
    [Input(Name = "BaseColour", Default = [0.8f, 0.8f, 0.8f])]
    public Float3 BaseColour;

    /// <summary>How metallic it is.</summary>
    [Input(Name = "Metallic")]
    public Scalar Metallic = 0f;

    /// <summary>How rough it is.</summary>
    [Input(Name = "Roughness")]
    public Scalar Roughness = 0.5f;

    /// <summary>What it emits on its own.</summary>
    [Input(Name = "Emission", Default = [0f, 0f, 0f])]
    public Float3 Emission;

    /// <summary>How opaque it is.</summary>
    [Input(Name = "Alpha")]
    public Scalar Alpha = 1f;

    string result = "";

    /// <inheritdoc />
    protected internal override string Result => result;

    /// <inheritdoc />
    protected internal override void Emit(RavenEmitter emitter) {
        var lightDirection = emitter.Uniform("lightDirection", "float3");
        var lightColour = emitter.Uniform("lightColour", "float3");
        var eye = emitter.Uniform("eyePosition", "float3");

        var normal = emitter.Stage(ShaderStageInput.WorldNormal);
        var position = emitter.Stage(ShaderStageInput.WorldPosition);

        emitter.Assign("shadeN", $"normalize({normal})");
        emitter.Assign("shadeL", $"normalize(-{lightDirection})");
        emitter.Assign("shadeV", $"normalize({eye} - {position})");
        emitter.Assign("shadeH", "normalize(shadeL + shadeV)");

        emitter.Assign("shadeNL", "max(dot(shadeN, shadeL), 0f)");
        emitter.Assign("shadeNH", "max(dot(shadeN, shadeH), 0f)");
        emitter.Assign("shadeNV", "max(dot(shadeN, shadeV), 0.0001f)");
        emitter.Assign("shadeVH", "max(dot(shadeV, shadeH), 0f)");

        emitter.Assign("shadeA", $"max({Roughness} * {Roughness}, 0.0001f)");
        emitter.Assign("shadeA2", "shadeA * shadeA");

        // GGX, Smith and Schlick, each in one line. Written out rather than factored into functions
        // because every one of them is an expression and a function per term would be six more names
        // in a generated file nobody reads for pleasure.
        emitter.Assign("shadeD", "shadeA2 / max(3.14159265f * pow(shadeNH * shadeNH * (shadeA2 - 1f) + 1f, 2f), 0.0001f)");
        emitter.Assign("shadeK", "shadeA * 0.5f");
        emitter.Assign("shadeG", "(shadeNL / (shadeNL * (1f - shadeK) + shadeK)) * (shadeNV / (shadeNV * (1f - shadeK) + shadeK))");
        emitter.Assign("shadeF0", $"lerp(float3(0.04f, 0.04f, 0.04f), {BaseColour}, {Metallic})");
        emitter.Assign("shadeF", "shadeF0 + (float3(1f, 1f, 1f) - shadeF0) * pow(1f - shadeVH, 5f)");

        emitter.Assign("shadeSpecular", "shadeD * shadeG * shadeF / max(4f * shadeNL * shadeNV, 0.0001f)");
        emitter.Assign("shadeKd", $"(float3(1f, 1f, 1f) - shadeF) * (1f - {Metallic})");
        emitter.Assign("shadeDiffuse", $"shadeKd * {BaseColour} / 3.14159265f");
        emitter.Assign("shadeLit", $"(shadeDiffuse + shadeSpecular) * {lightColour} * shadeNL + {Emission}");

        result = "surface";
        emitter.Assign(result, $"float4(shadeLit, {Alpha})");
    }
}
