// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.Lighting;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     Keeps the clipmap over the camera, on the device, and named in the frame's set.
/// </summary>
/// <remarks>
///     <para>
///         The node that joins the three halves that already existed and never met: a
///         <see cref="GlobalDistanceField" /> that knows how to composite itself, a
///         <see cref="GlobalDistanceFieldTexture" /> that knows how to copy one up, and a shader that
///         knows how to read one. Everything here is sequencing.
///     </para>
///     <para>
///         <b>It recomposites when the answer would change, not every frame.</b> A composite is every
///         cell of every level against every instance — the most expensive thing in the frame by a
///         wide margin — and a camera that has not crossed a cell boundary would get the same numbers
///         back. The levels are snapped to their own grids precisely so that "has anything moved" is
///         a comparison rather than a guess, and this is what cashes that in: a still camera
///         composites once and then never again.
///     </para>
///     <para>
///         <b>The names are written every frame regardless.</b> They are cheap, they are the frame's
///         answer to "where is the clipmap now", and a set rebuilt for another reason would otherwise
///         bind whatever the last frame left. Re-asserting a value that has not changed does not
///         bump <c>ParameterCollection.Version</c>, so it does not cost an upload either.
///     </para>
///     <para>
///         <b>The view position is the host's, not the camera's.</b> It is usually the camera and does
///         not have to be: a clipmap centred slightly ahead of a fast-moving camera has the geometry
///         it is about to need rather than the geometry behind it. Taking it from
///         <see cref="RenderView" /> would make that impossible to express, so the default is to
///         follow the view and the property is there to override it.
///     </para>
/// </remarks>
public sealed class GlobalDistanceFieldRenderer : SceneRenderer, IDisposable {
    readonly List<DistanceFieldInstance> instances = [];

    Vector3 lastCentre;
    int lastVersion;
    bool composited;
    bool disposed;

    /// <summary>The clipmap to keep. Null does nothing at all.</summary>
    public GlobalDistanceField? Field { get; set; }

    /// <summary>Its mirror on the device, made on the first record.</summary>
    public GlobalDistanceFieldTexture? Texture { get; private set; }

    /// <summary>Where the names go — the frame's set 0.</summary>
    /// <remarks>Null writes nothing, which is what a node kept for its clipmap alone wants.</remarks>
    public SceneConstants? SceneConstants { get; set; }

    /// <summary>The compose-slot prefix the clipmap's names are written under.</summary>
    /// <remarks>
    ///     A slot's bindings are named for the <i>slot</i> rather than for the shader that declared
    ///     them, so this is <c>DistanceFieldAo.GlobalDistanceField</c> and not <c>ForwardPlus</c> —
    ///     the pass, then the slot, then the name. Get it wrong and every binding resolves to nothing,
    ///     silently, which is why the default is the one consumer that exists rather than a guess.
    /// </remarks>
    public string ShaderName { get; set; } = "DistanceFieldAo.GlobalDistanceField";

    /// <summary>What the clipmap covers.</summary>
    /// <remarks>
    ///     Changing the contents does not itself trigger a recomposite — see
    ///     <see cref="InstancesVersion" />, which is what says the list means something different
    ///     now. Comparing the instances themselves every frame would cost more than the comparison
    ///     saves.
    /// </remarks>
    public IList<DistanceFieldInstance> Instances => instances;

    /// <summary>
    ///     Bumped by whoever changes <see cref="Instances" />, to say the composite is out of date.
    /// </summary>
    public int InstancesVersion { get; set; }

    /// <summary>Where to centre the clipmap, or null to follow the view.</summary>
    public Vector3? ViewPosition { get; set; }

    /// <summary>How many cells the last recomposite kept rather than recomputed.</summary>
    /// <remarks>
    ///     Zero on the first frame and after anything moves; nearly the whole clipmap for a camera
    ///     that crossed one cell. What says the scroll is happening rather than merely available.
    /// </remarks>
    public long Reused => Field?.Reused ?? 0;

    /// <summary>How many times the clipmap has actually been recomposited.</summary>
    /// <remarks>
    ///     What makes "it does not recomposite a still frame" checkable rather than claimed. A frame
    ///     that moved the camera a centimetre and this number going up is the defect.
    /// </remarks>
    public int Composites { get; private set; }

    /// <summary>Whether the composite may use more than one thread.</summary>
    public bool Parallel { get; set; } = true;

    /// <inheritdoc />
    protected internal override void Record(GraphicsCompositor compositor, RenderDrawContext context) {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Field is not { } field || context.Device is not { } device) {
            return;
        }

        var centre = ViewPosition ?? context.View?.Position ?? Vector3.Zero;

        if (ShouldComposite(field, centre, out var moveOnly)) {
            // Scrolling only where the camera moved and nothing else did. This node is the one thing
            // that knows the difference — it is what watches the instance version — and the clipmap
            // cannot work it out for itself without comparing every instance every frame.
            field.Update(centre, CollectionsMarshal.AsSpan(instances), Parallel, moveOnly);

            // The snapped centre of the finest level, which is what decides whether the next frame
            // has anything to redo. Recorded after the composite, not before, so a composite that
            // threw does not leave the node believing it happened.
            lastCentre = GlobalDistanceField.Snap(centre, field.CellSizeOf(0));
            composited = true;
            Composites++;

            Texture ??= new(field);
            Texture.Upload(device, context.CommandList);
        }

        if (SceneConstants is { } scene && Texture is not null) {
            Texture.Apply(scene.Parameters, ShaderName);
        }
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

    /// <summary>Whether anything about the clipmap would come out different this frame.</summary>
    /// <param name="field">The clipmap.</param>
    /// <param name="centre">Where it would be centred.</param>
    /// <param name="moveOnly">Whether the camera is the only thing that changed, so a scroll is safe.</param>
    /// <returns>Whether to redo it.</returns>
    /// <remarks>
    ///     The finest level's snap is the test, because it is the one with the smallest cell: a
    ///     movement too small to move level zero is too small to move any of them.
    /// </remarks>
    bool ShouldComposite(GlobalDistanceField field, Vector3 centre, out bool moveOnly) {
        moveOnly = false;

        if (!composited || !field.HasContent) {
            return true;
        }

        if (InstancesVersion != lastVersion) {
            lastVersion = InstancesVersion;

            return true;
        }

        moveOnly = true;

        return GlobalDistanceField.Snap(centre, field.CellSizeOf(0)) != lastCentre;
    }
}
