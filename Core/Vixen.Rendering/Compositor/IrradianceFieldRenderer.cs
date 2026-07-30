// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Materials;
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
///     <para>
///         <b>Two fillers, and at most one of them at a time.</b> <see cref="Filler" /> traces on the
///         CPU and this copies the answer up; <see cref="DeviceFiller" /> dispatches and this copies
///         nothing but the index volume. Both live would be an upload of the CPU pool over whatever
///         the dispatch wrote, one frame after it wrote it — lighting that flickers between two
///         answers rather than a mode somebody chose — so asking for both is refused rather than
///         resolved.
///     </para>
/// </remarks>
public sealed class IrradianceFieldRenderer : SceneRenderer, IDisposable {
    bool disposed;

    /// <summary>The field to keep. Null does nothing at all.</summary>
    public IrradianceField? Field { get; set; }

    /// <summary>What fills its probes on the CPU, or null for a field somebody else fills.</summary>
    public TracedIrradianceFiller? Filler { get; set; }

    /// <summary>What fills its probes on the device — doc 19 § L2's filler A.</summary>
    /// <remarks>
    ///     <para>
    ///         Settable rather than owned, and symmetrically with <see cref="Filler" />: what a probe
    ///         sees is a question about the scene, so the sky colour, the sun and the shader behind
    ///         the field slot belong to whoever knows about those. This knows about ordering.
    ///     </para>
    ///     <para>
    ///         ⚠ Setting it changes what the mirror is. The pool becomes a storage image the dispatch
    ///         owns and this stops copying it up, which means the CPU field's probes are no longer
    ///         what anything reads — and <see cref="IrradianceField.Dilate" /> and
    ///         <see cref="IrradianceField.SyncBorders" /> stop being run, because they repair an array
    ///         nothing uploads. A device-filled field has seams until those exist as dispatches too.
    ///     </para>
    /// </remarks>
    public IrradianceFieldFill? DeviceFiller { get; set; }

    /// <summary>Its mirror on the device, made on the first build.</summary>
    public IrradianceFieldTexture? Texture { get; private set; }

    /// <summary>Where the names go — the frame's set 0.</summary>
    /// <remarks>Null writes nothing, which is what a node kept for its field alone wants.</remarks>
    public SceneConstants? SceneConstants { get; set; }

    /// <summary>Which passes read this field.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A list, because one field has more than one consumer.</b> The screen-space
    ///         <c>IndirectDiffuse</c> pass reads it and so do <c>ForwardPlus</c>'s materials, and the
    ///         two need the same handles under different names — a composed slot's bindings are named
    ///         for the pass and the shader filling it, so one field bound once is two sets of names.
    ///     </para>
    ///     <para>
    ///         Pass names only: the filler is <see cref="Source" /> and this qualifies each entry with
    ///         it, which is one fewer place to misspell the half that resolves to nothing silently.
    ///     </para>
    ///     <para>
    ///         <c>IndirectDiffuse</c> alone by default, which is the consumer that needs no material
    ///         change. Adding <c>ForwardPlus</c> is a project deciding its materials read the field —
    ///         and that decision is also a composition, so the two go together.
    ///     </para>
    /// </remarks>
    public IList<string> Passes { get; } = ["IndirectDiffuse"];

    /// <summary>The shader filling the slot, which is the other half of every binding's name.</summary>
    public string Source { get; set; } = MaterialCompiler.IrradianceFieldShader;

    /// <summary>Where the field should be fine, or null to leave its allocation alone.</summary>
    /// <remarks>
    ///     <para>
    ///         Doc 19 § 3's "bricks subdivide near geometry, from renderer bounds", and this is the
    ///         half that knows what a renderer is. The policy itself takes boxes and knows nothing
    ///         about a scene, which is what keeps it device-free and checkable; this reads
    ///         <see cref="RenderObject.Bounds" /> off the compositor's own store and hands them over.
    ///     </para>
    ///     <para>
    ///         <b>Every object, not the visible ones.</b> Indirect light comes from geometry the camera
    ///         cannot see — that is the whole reason a field exists rather than a screen-space gather —
    ///         so refining only what survived the frustum would coarsen exactly the wall that is
    ///         bouncing light onto what the camera <i>can</i> see, one frame after it left the view.
    ///     </para>
    ///     <para>
    ///         Refinement alone only ever adds detail; a streamed scene sets
    ///         <see cref="IrradianceRefinementPolicy.CoarsenTo" /> so regions the geometry leaves
    ///         merge back and the pool gets its slots returned.
    ///     </para>
    /// </remarks>
    public IrradianceRefinementPolicy? Refinement { get; set; }

    /// <summary>How many bricks the refinement policy has produced since this node was made.</summary>
    /// <remarks>
    ///     Settles once the scene stops moving, which is what makes it worth counting: a number that
    ///     keeps climbing is a policy asking for more than the pool holds, and that is a stutter
    ///     nobody could otherwise attribute.
    /// </remarks>
    public int Refined { get; private set; }

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

        if (Filler is not null && DeviceFiller is not null) {
            throw new InvalidOperationException(
                $"'{this}' has both a CPU filler and a device filler. The pool can be authored by one "
                + "or the other: with both, every upload copies the CPU's probes over the dispatch's, "
                + "one frame after it wrote them. Clear whichever is not wanted."
            );
        }

        // Before the pass is declared, not inside it: refinement changes which bricks exist, and the
        // mirror's textures are sized from the pool rather than from the field's shape — so a split
        // during execution would be a fill into slots the frame has already decided about.
        Refine(compositor, field);

        frame.Graph.AddPass(
            ToString(),
            pass => {
                // A dispatch belongs on the compute queue and a copy on the transfer one, and this
                // pass is whichever of the two it turns out to be doing.
                pass.Kind = DeviceFiller is null ? PassKind.Transfer : PassKind.Compute;
                pass.SideEffect();

                pass.Execute(context => Refill(field, device, context.CommandList));
            }
        );
    }

    /// <summary>Asks the policy where the field should be fine, from what the scene holds.</summary>
    /// <remarks>
    ///     Every object's bounds, allocated into a list per build. That is a list per frame for a
    ///     scene that is not changing, which is worth removing once something measures it — and worth
    ///     leaving until then, because the alternative is a cache keyed on "has anything moved" and
    ///     that question has no cheap answer here.
    /// </remarks>
    void Refine(GraphicsCompositor compositor, IrradianceField field) {
        if (Refinement is not { } policy || compositor is null) {
            return;
        }

        var objects = compositor.System.Objects.All;

        if (objects.Length == 0) {
            return;
        }

        var bounds = new List<BoundingBox>(objects.Length);

        foreach (ref readonly var candidate in objects) {
            bounds.Add(BoundingBox.FromSphere(candidate.Bounds));
        }

        Refined += policy.Apply(field, bounds);
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

    /// <summary>Fills what the budget allows, repairs it, and copies up whatever the CPU still owns.</summary>
    /// <remarks>
    ///     The upload comes before the dispatch and after the CPU fill, which is not a preference
    ///     either way: the mirror creates its textures on its first upload, so a dispatch before one
    ///     has nothing to write into — and an upload after a CPU fill is what carries that fill.
    /// </remarks>
    void Refill(IrradianceField field, IGraphicsDevice device, ICommandList commands) {
        if (DeviceFiller is null && Filler is { } filler && Budget > 0) {
            var filled = filler.Fill(field, Budget);

            if (filled > 0) {
                Filled += filled;

                // In this order, always. A border is a copy, so copying before the original is
                // repaired copies the hole.
                field.Dilate(DilationPasses);
                field.SyncBorders();
            }
        }

        Texture ??= new(field) { PoolIsWritten = DeviceFiller is not null };
        Texture.Upload(device, commands);

        if (DeviceFiller is { } dispatch && Budget > 0) {
            Filled += dispatch.Record(commands, field, Texture, Budget);
        }

        if (SceneConstants is { } scene) {
            foreach (var pass in Passes) {
                Texture.Apply(scene.Parameters, $"{pass}.{Source}");
            }
        }
    }
}
