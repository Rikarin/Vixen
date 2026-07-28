// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     Builds a pipeline from the four things that decide one: an effect, a stage, an output and a
///     vertex layout.
/// </summary>
/// <remarks>
///     <para>
///         The default <see cref="IPipelineDescriber" />, and the reason a render feature no longer
///         takes a pipeline-description callback from its host. Each of the four contributes exactly
///         what it knows and nothing else:
///     </para>
///     <list type="bullet">
///         <item><description>the <see cref="Effect" /> — the shader modules and the pipeline layout;</description></item>
///         <item><description>the <see cref="RenderStage" /> — how its objects are drawn: blend, depth, raster;</description></item>
///         <item><description>the <see cref="RenderOutput" /> — what they are drawn into: formats and samples;</description></item>
///         <item><description>the vertex layout — how a vertex is read.</description></item>
///     </list>
///     <para>
///         Which is also, exactly, <see cref="PipelineKey" />. Two draws agreeing on all four get the
///         same pipeline; disagreeing on any one of them gets a different pipeline, because each one
///         changes what the driver compiles.
///     </para>
/// </remarks>
public sealed class EffectPipelineDescriber(IGraphicsDevice device) : IPipelineDescriber {
    readonly Dictionary<(Effect Effect, ShaderStage Stage), ShaderHandle> modules = [];

    /// <summary>The vertex buffer layouts, by the index a draw names.</summary>
    /// <remarks>
    ///     A table rather than a layout per mesh, because the layout is a property of the vertex
    ///     <em>format</em> and a project has a handful: static, skinned, UI, particle. The index is
    ///     what <see cref="PipelineKey.VertexLayout" /> carries, so two meshes of the same format
    ///     share a pipeline rather than each compiling their own.
    /// </remarks>
    public IList<VertexBufferLayout[]> VertexLayouts { get; } = [];

    /// <inheritdoc />
    public GraphicsPipelineDescription Describe(
        Effect effect,
        RenderStage stage,
        in RenderOutput output,
        int vertexLayout
    ) {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(stage);

        var targets = new ColourTargetState[output.ColourCount];

        for (var i = 0; i < targets.Length; i++) {
            targets[i] = new(output.ColourFormats[i], stage.Blend);
        }

        // A pass with no depth attachment gets no depth test, whatever the stage asked for. The
        // alternative is a description that fails validation with "tests depth but declares no depth
        // attachment" — which is true and unhelpful, because the stage is reusable and the pass is
        // what varies. A shadow-caster stage drawn into a colour-only debug pass should draw, not
        // throw.
        var depth = output.DepthFormat == PixelFormat.Undefined ? DepthStencilState.Disabled : stage.DepthStencil;

        return new(
            Module(effect, ShaderStage.Vertex),
            Module(effect, ShaderStage.Fragment),
            effect.Layout,
            targets,
            vertexLayout >= 0 && vertexLayout < VertexLayouts.Count ? VertexLayouts[vertexLayout] : null,
            PrimitiveTopology.TriangleList,
            stage.Rasterizer,
            depth,
            output.DepthFormat,
            output.SampleCount,
            $"{effect.Key.ShaderName}/{stage.Name}"
        );
    }

    /// <summary>Forgets every shader module, for a device loss or a shader reload.</summary>
    public void Clear() => modules.Clear();

    /// <summary>The device module for one of an effect's stages, created once.</summary>
    /// <remarks>
    ///     <para>
    ///         Cached because an effect drawn into three passes is three pipelines and one pair of
    ///         modules. Creating them per pipeline would hand the driver the same SPIR-V three times
    ///         and make every pipeline in the frame pay for the parse.
    ///     </para>
    ///     <para>
    ///         Public because not everything that needs a module needs
    ///         <see cref="Describe" />. A full-screen pass has no stage and no vertex layout, so it
    ///         assembles its own description — but it should share this cache rather than hand the
    ///         driver the same bytecode again.
    ///     </para>
    /// </remarks>
    public ShaderHandle ModuleOf(Effect effect, ShaderStage stage) {
        ArgumentNullException.ThrowIfNull(effect);
        return Module(effect, stage);
    }

    ShaderHandle Module(Effect effect, ShaderStage stage) {
        if (modules.TryGetValue((effect, stage), out var existing)) {
            return existing;
        }

        var handle = ShaderHandle.Null;

        foreach (var compiled in effect.Stages) {
            if (compiled.Stage != stage) {
                continue;
            }

            handle = device.CreateShader(
                stage,
                compiled.Bytecode.AsSpan(),
                $"{effect.Key.ShaderName}.{stage}"
            );

            break;
        }

        modules[(effect, stage)] = handle;
        return handle;
    }
}
