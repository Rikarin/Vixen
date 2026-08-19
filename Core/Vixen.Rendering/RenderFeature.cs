// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering;

/// <summary>
///     One kind of renderable: meshes, sprites, particles, UI.
/// </summary>
/// <remarks>
///     <para>
///         A root feature owns extraction and drawing for its kind of object. There is one per kind
///         and they do not know about each other — a scene with meshes and particles runs two, over
///         one shared <see cref="RenderObjectStore" />, and each walks only the objects whose
///         <see cref="RenderObject.FeatureIndex" /> is its own.
///     </para>
///     <para>
///         <strong>What varies between kinds is not what varies between features of a kind.</strong>
///         A mesh and a sprite are drawn differently, so they are different root features. A skinned
///         mesh and a static one are drawn the same way with different data, so skinning is a
///         <see cref="SubRenderFeature" /> of the mesh feature rather than a second root — which is
///         the distinction that keeps the root feature count in single digits.
///     </para>
/// </remarks>
public abstract class RootRenderFeature {
    readonly List<SubRenderFeature> subFeatures = [];

    /// <summary>The feature's name, for logging and profiling.</summary>
    public abstract string Name { get; }

    /// <summary>The index objects of this kind carry, assigned when the feature is added.</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>The system this feature belongs to, once it has been added to one.</summary>
    public RenderSystem? System { get; internal set; }

    /// <summary>The sub-features contributing data to this one.</summary>
    public IReadOnlyList<SubRenderFeature> SubFeatures => subFeatures;

    /// <summary>
    ///     Why this feature drew something other than what its objects asked for, this frame — or
    ///     null, meaning every input it needed was there.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><see cref="Compositor.SceneRenderer.Degraded" />, one layer down.</b> A compositor
    ///         node degrades because a <em>frame</em> input was missing; a feature degrades because an
    ///         <em>object's</em> was — an effect authored as a light that nothing is collecting lights
    ///         from, a mesh particle with no mesh attached. Both are silent substitutions that leave
    ///         every counter healthy, and both are only visible in the picture.
    ///     </para>
    ///     <para>
    ///         Surfaced through the same channel: <see cref="Compositor.SingleStageRenderer" /> reads
    ///         this off the features that drew into its stage, so
    ///         <see cref="Compositor.GraphicsCompositor.Degradations" /> collects a feature's reason
    ///         beside a node's without knowing either type.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This frame, not the worst frame there has ever been.</b> A feature must set it on
    ///         <em>both</em> paths each time it decides, so recovering is as visible as degrading.
    ///     </para>
    /// </remarks>
    public string? Degraded { get; private set; }

    /// <summary>Records why this feature is drawing something other than what it was asked for.</summary>
    /// <param name="reason">
    ///     What was missing and what happened instead, as a sentence a developer who did not write
    ///     the feature can act on — or null, for "everything I needed was there".
    /// </param>
    /// <remarks>
    ///     Called from <see cref="Prepare" />, which is the phase that reads the frame and runs before
    ///     any node builds. <see cref="Draw" /> is too late — the compositor has already asked.
    /// </remarks>
    protected void Degrade(string? reason) => Degraded = reason;

    /// <summary>Adds a sub-feature, which registers its own per-object data.</summary>
    public void Add(SubRenderFeature subFeature) {
        ArgumentNullException.ThrowIfNull(subFeature);

        if (subFeature.Parent is not null) {
            throw new InvalidOperationException(
                $"Sub-feature '{subFeature.Name}' already belongs to '{subFeature.Parent.Name}'."
            );
        }

        subFeature.Parent = this;
        subFeatures.Add(subFeature);

        if (System is not null) {
            subFeature.Initialize(System);
        }
    }

    /// <summary>Registers this feature's own per-object data. Called once, when it is added.</summary>
    protected internal virtual void Initialize(RenderSystem system) { }

    /// <summary>Pulls this frame's state out of the scene and into the store.</summary>
    /// <remarks>
    ///     The one place a feature is allowed to touch a scene graph, an ECS world or anything else
    ///     with references in it. Everything downstream reads the flat arrays, which is what lets
    ///     culling, sorting and recording be jobs.
    /// </remarks>
    protected internal virtual void Extract(RenderSystem system) { }

    /// <summary>
    ///     Fills per-object data for the objects that survived culling, before anything is sorted.
    /// </summary>
    /// <remarks>
    ///     Split from <see cref="Extract" /> because the two have different inputs and different
    ///     costs: extraction touches every object that changed, preparation touches every object
    ///     that is <em>visible</em>, and in a well-culled scene the second set is far smaller. Work
    ///     that does not depend on the view — decompressing a transform, resolving a material —
    ///     belongs in extraction; work that does belongs here.
    /// </remarks>
    protected internal virtual void Prepare(RenderSystem system) { }

    /// <summary>
    ///     Records this feature's share of one stage's work into a command list.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The nodes arrive already ordered and already filtered to this feature, so what is left
    ///         is the part only the feature knows: which pipeline, which descriptor sets, which draw
    ///         call. That split is what lets the renderer own sorting without owning materials.
    ///     </para>
    ///     <para>
    ///         Contiguous runs, not one call per node. A stage's list is sorted by a key whose high
    ///         bits are the sort group, so nodes that share a pipeline are already adjacent — handing
    ///         a feature the whole run lets it bind once and draw many, which is the entire point of
    ///         having sorted by group in the first place. Handing it one node at a time would throw
    ///         that away at the last step.
    ///     </para>
    /// </remarks>
    protected internal virtual void Draw(
        RenderSystem system,
        RenderDrawContext context,
        ReadOnlySpan<RenderNode> nodes
    ) { }

    /// <summary>The sort group for one object, which the key puts above depth.</summary>
    /// <remarks>
    ///     Defaults to <see cref="RenderObject.SortGroup" />, so a feature that has nothing better to
    ///     say does not have to say anything. A feature with a pipeline handle should return
    ///     something derived from it — that is what turns a depth sort into a state-change-minimising
    ///     one.
    /// </remarks>
    protected internal virtual uint SortGroupOf(RenderSystem system, RenderObjectId id, RenderStage stage) =>
        system.Objects[id].SortGroup;

    internal void InitializeAll(RenderSystem system) {
        Initialize(system);

        foreach (var subFeature in subFeatures) {
            subFeature.Initialize(system);
        }
    }

    internal void ExtractAll(RenderSystem system) {
        Extract(system);

        foreach (var subFeature in subFeatures) {
            subFeature.Extract(system);
        }
    }

    internal void PrepareAll(RenderSystem system) {
        Prepare(system);

        foreach (var subFeature in subFeatures) {
            subFeature.Prepare(system);
        }
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
///     One aspect of a root feature's objects: transforms, skinning, instancing, materials.
/// </summary>
/// <remarks>
///     <para>
///         The structural point of the whole arrangement. A sub-feature registers its own
///         <see cref="RenderDataHolder" /> array and fills it, so adding skinning does not modify
///         the mesh feature, does not add a field to <see cref="RenderObject" />, and costs an
///         object that is not skinned one unused slot rather than a branch.
///     </para>
///     <para>
///         This is extension by composition where most renderers use inheritance, and the difference
///         shows when two aspects are wanted at once: a skinned, instanced, shadow-casting mesh is
///         three sub-features over one object, where an inheritance hierarchy would need a class for
///         the combination.
///     </para>
/// </remarks>
public abstract class SubRenderFeature {
    /// <summary>The sub-feature's name, for logging and profiling.</summary>
    public abstract string Name { get; }

    /// <summary>The root feature this contributes to.</summary>
    public RootRenderFeature? Parent { get; internal set; }

    /// <summary>Registers this sub-feature's per-object data. Called once.</summary>
    protected internal virtual void Initialize(RenderSystem system) { }

    /// <summary>Pulls this sub-feature's share of the scene into its arrays.</summary>
    protected internal virtual void Extract(RenderSystem system) { }

    /// <summary>Fills this sub-feature's data for the visible objects.</summary>
    protected internal virtual void Prepare(RenderSystem system) { }

    /// <inheritdoc />
    public override string ToString() => Name;
}
