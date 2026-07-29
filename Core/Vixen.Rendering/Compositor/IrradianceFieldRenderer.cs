// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Lighting;

namespace Vixen.Rendering.Compositor;

/// <summary>Keeps the irradiance field filled, on the device, and named in the frame's set.</summary>
/// <remarks>
///     <para>
///         The counterpart to <see cref="GlobalDistanceFieldRenderer" />, and the same shape for the
///         same reasons: a field that knows how to hold light, a mirror that knows how to copy one up,
///         a shader that knows how to read one, and this to sequence them.
///     </para>
///     <para>
///         <b>A transfer pass, marked as having a side effect.</b> Uploading is a buffer-to-texture
///         copy and a copy cannot be recorded inside a render pass; the volumes are not graph resources
///         — they are named into a descriptor set — so a pass that writes none reads as a pass nothing
///         needs and would be culled. Both facts were learnt the hard way next door, and this is
///         written the way that ended up.
///     </para>
///     <para>
///         <b>It fills a bounded number of bricks a frame and uploads whatever that produced.</b> That
///         is doc 19 § L2's round robin, and it is why <see cref="Filler" /> is a property rather than
///         something this owns: what a probe <i>sees</i> is a question about the scene, and the answer
///         comes from somewhere that knows about geometry and light. This knows about ordering.
///     </para>
///     <para>
///         <b>Dilation and the border sync run after a fill, in that order, or the field has holes and
///         seams in it.</b> Doing them here rather than leaving them to a caller is the whole reason
///         this type exists — they are cheap, they are easy to forget, and forgetting either produces
///         an artefact that looks like a lighting bug rather than a missing call.
///     </para>
/// </remarks>
public sealed class IrradianceFieldRenderer : SceneRenderer, IDisposable {
    bool disposed;

    /// <summary>The field to keep. Null does nothing at all.</summary>
    public IrradianceField? Field { get; set; }

    /// <summary>What fills its probes, or null for a field somebody else fills.</summary>
    public TracedIrradianceFiller? Filler { get; set; }

    /// <summary>Its mirror on the device, made on the first build.</summary>
    public IrradianceFieldTexture? Texture { get; private set; }

    /// <summary>Where the names go — the frame's set 0.</summary>
    /// <remarks>Null writes nothing, which is what a node kept for its field alone wants.</remarks>
    public SceneConstants? SceneConstants { get; set; }

    /// <summary>The compose-slot prefix the field's names are written under.</summary>
    /// <remarks>
    ///     A slot's bindings are named for the <i>slot</i> rather than for the shader that declared
    ///     them, so this is <c>IndirectDiffuse.IrradianceFieldProbes</c>. Get it wrong and every
    ///     binding resolves to nothing, silently — which is why the default is the one consumer that
    ///     exists rather than a guess.
    /// </remarks>
    public string ShaderName { get; set; } = "IndirectDiffuse.IrradianceFieldProbes";

    /// <summary>How many bricks to refill each frame.</summary>
    /// <remarks>
    ///     Bricks rather than probes, because a brick is the unit of a pool slot and of a dispatch.
    ///     Zero fills nothing, which is what a field filled once at load time wants.
    /// </remarks>
    public int Budget { get; set; } = 8;

    /// <summary>How far dilation may travel into invalid probes, in probes.</summary>
    /// <remarks>One, and the reasons for that are in <see cref="IrradianceField.Dilate" />.</remarks>
    public int DilationPasses { get; set; } = 1;

    /// <summary>The device its texture is made on, or null to take the frame's.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>How many bricks have been filled since this node was made.</summary>
    /// <remarks>
    ///     What makes the round robin checkable rather than claimed. A field that looks unlit and this
    ///     number sitting still is a scheduling failure; the same field with this number climbing is a
    ///     filler that is not finding any light.
    /// </remarks>
    public int Filled { get; private set; }

    /// <summary>Declares the pass that refills the field and copies it up.</summary>
    /// <param name="compositor">The compositor.</param>
    /// <param name="frame">The frame being built.</param>
    /// <exception cref="ArgumentNullException">There is no frame.</exception>
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Field is not { } field || (Device ?? frame.Device) is not { } device) {
            return;
        }

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.Kind = PassKind.Transfer;
                pass.SideEffect();

                pass.Execute(context => Refill(field, device, context.CommandList));
            }
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Texture?.Dispose();
        Texture = null;
    }

    /// <summary>Fills what the budget allows, repairs it, and copies the whole field up.</summary>
    void Refill(IrradianceField field, IGraphicsDevice device, ICommandList commands) {
        if (Filler is { } filler && Budget > 0) {
            var filled = filler.Fill(field, Budget);

            if (filled > 0) {
                Filled += filled;

                // In this order, always. A border is a copy, so copying before the original is
                // repaired copies the hole.
                field.Dilate(DilationPasses);
                field.SyncBorders();
            }
        }

        Texture ??= new(field);
        Texture.Upload(device, commands);

        if (SceneConstants is { } scene) {
            Texture.Apply(scene.Parameters, ShaderName);
        }
    }
}
