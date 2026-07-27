// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Features;

/// <summary>Which LOD group an object belongs to, and which level of it it is.</summary>
/// <param name="Group">The group's index, or 0 for an object with no LOD.</param>
/// <param name="Level">Which level this object is — 0 is the most detailed.</param>
public readonly record struct LodMembership(int Group, int Level);

/// <summary>
///     Picks one level of detail per group per view, and hides the rest.
/// </summary>
/// <remarks>
///     <para>
///         <strong>A LOD group is several render objects, not one object with several meshes.</strong>
///         Each level is an ordinary renderable with its own mesh, material and sort key, and this
///         feature does nothing but decide which of them a view gets to see. The alternative — one
///         object that swaps its mesh — would have to pick one sort key for meshes that resolve to
///         different pipelines, which is the same argument that makes a three-material mesh three
///         render objects.
///     </para>
///     <para>
///         <strong>Selection happens after culling and before sorting</strong>, in preparation. It
///         cannot happen earlier: an object outside the frustum has no screen size to measure, and
///         asking for one would mean measuring every object in the scene rather than every visible
///         one. It cannot happen later, because sorting is what builds the list a level would have
///         to be absent from. Doc 06's frame structure puts LOD selection in exactly this gap.
///     </para>
///     <para>
///         <strong>Per view, because screen size is.</strong> The same tree is level 0 to the camera
///         and level 3 to a distant reflection probe, so the decision is a bit cleared in one view's
///         set rather than a field on the object — which is also what makes it free for the views
///         that do not care: a shadow cascade leaves
///         <see cref="RenderView.ScreenHeightScale" /> at zero and every level stays visible for it,
///         because a shadow drawn from a different mesh than its caster stops matching it.
///     </para>
/// </remarks>
public sealed class LodRenderFeature : SubRenderFeature {
    readonly List<LodGroup> groups = [];
    readonly List<int> current = [];
    int viewStride;

    /// <inheritdoc />
    public override string Name => "Lod";

    /// <summary>Which group and level each object is.</summary>
    public RenderDataKey<LodMembership> Membership { get; private set; }

    /// <summary>The groups registered so far.</summary>
    public IReadOnlyList<LodGroup> Groups => groups;

    /// <summary>
    ///     How much a threshold must be crossed by before the level changes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The difference between LOD that works and LOD that flickers. An object drifting at
    ///         exactly a threshold — a tree at the edge of its switch distance, a camera breathing —
    ///         would otherwise change level every frame, and a level change is a different mesh and
    ///         usually a different silhouette, so the flicker is far more visible than the detail the
    ///         switch was protecting.
    ///     </para>
    ///     <para>
    ///         A fraction of the threshold rather than an absolute size, so it means the same thing
    ///         at the near boundary as at the far one.
    ///     </para>
    /// </remarks>
    public float Hysteresis { get; set; } = 0.1f;

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);

        Membership = system.Objects.Data.Register<LodMembership>();

        // Group 0 is "no LOD", so an object nobody put in a group keeps the default and is never
        // hidden — the same sentinel-at-zero reasoning as the material feature's.
        groups.Add(new([]));
    }

    /// <summary>
    ///     Registers a group and returns its index.
    /// </summary>
    /// <param name="thresholds">
    ///     The screen-height fraction below which each level gives way to the next, descending. Four
    ///     levels take three thresholds; the last level has none, because there is nothing past it.
    /// </param>
    public int Add(ReadOnlySpan<float> thresholds) {
        for (var i = 1; i < thresholds.Length; i++) {
            if (thresholds[i] >= thresholds[i - 1]) {
                throw new ArgumentException(
                    "LOD thresholds must descend: each level gives way at a smaller screen size than "
                    + $"the one before it, and threshold {i} ({thresholds[i]}) is not below "
                    + $"threshold {i - 1} ({thresholds[i - 1]}).",
                    nameof(thresholds)
                );
            }
        }

        groups.Add(new(thresholds.ToArray()));
        return groups.Count - 1;
    }

    /// <summary>Puts an object in a group as one of its levels.</summary>
    public void Assign(RenderSystem system, RenderObjectId id, int group, int level) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(group);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(group, groups.Count);

        system.Objects.Data.Data(Membership)[id.Index] = new(group, level);
    }

    /// <summary>Which level a group is showing in a view, or -1 if it has not been decided.</summary>
    public int LevelOf(int group, int viewIndex) {
        var slot = (group * viewStride) + viewIndex;
        return viewStride > 0 && slot >= 0 && slot < current.Count ? current[slot] : -1;
    }

    /// <inheritdoc />
    protected internal override void Prepare(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);

        if (Parent is null || groups.Count <= 1 || system.Views.Count == 0) {
            return;
        }

        Resize(system.Views.Count);

        var membership = system.Objects.Data.Data(Membership);
        var objects = system.Objects.All;

        foreach (var view in system.Views) {
            if (view.ScreenHeightScale <= 0f) {
                continue;
            }

            Select(system, view, objects, membership);
        }
    }

    /// <summary>Decides each group's level for one view, then hides every other level.</summary>
    /// <remarks>
    ///     Two passes over the view's visible objects rather than one, because a level cannot be
    ///     hidden until the group's choice is known and the choice is made from whichever member
    ///     happens to be seen first. The alternative — an index of groups to their members — is a
    ///     structure rebuilt every frame to save a second walk of a list that culling already
    ///     shortened.
    /// </remarks>
    void Select(
        RenderSystem system,
        RenderView view,
        ReadOnlySpan<RenderObject> objects,
        ReadOnlySpan<LodMembership> membership
    ) {
        for (var index = 0; index < objects.Length; index++) {
            ref readonly var candidate = ref objects[index];

            if (!candidate.IsAlive || candidate.FeatureIndex != Parent!.Index) {
                continue;
            }

            var member = membership[index];

            if (member.Group <= 0 || member.Group >= groups.Count) {
                continue;
            }

            if (!system.Visibility.IsVisible(view.Index, new(index))) {
                continue;
            }

            var slot = (member.Group * viewStride) + view.Index;
            current[slot] = Choose(groups[member.Group], current[slot], Height(candidate.Bounds, view));
        }

        for (var index = 0; index < objects.Length; index++) {
            ref readonly var candidate = ref objects[index];

            if (!candidate.IsAlive || candidate.FeatureIndex != Parent!.Index) {
                continue;
            }

            var member = membership[index];

            if (member.Group <= 0 || member.Group >= groups.Count) {
                continue;
            }

            if (current[(member.Group * viewStride) + view.Index] != member.Level) {
                system.Visibility.Hide(view.Index, new(index));
            }
        }
    }

    /// <summary>The fraction of the viewport's height an object covers.</summary>
    /// <remarks>
    ///     Measured to the sphere's centre, not to its near point: a level's threshold is about how
    ///     big the object <em>is</em> on screen, and subtracting the radius the way the depth sort
    ///     does would make a large object switch level while its far side is still filling the frame.
    /// </remarks>
    static float Height(in BoundingSphere bounds, RenderView view) {
        var distance = Vector3.Distance(bounds.Center, view.Position);
        return distance <= 0f ? float.MaxValue : bounds.Radius * view.ScreenHeightScale / distance;
    }

    /// <summary>The level for a screen height, keeping the current one inside the hysteresis band.</summary>
    int Choose(LodGroup group, int currentLevel, float height) {
        var chosen = group.Thresholds.Length;

        for (var level = 0; level < group.Thresholds.Length; level++) {
            if (height >= group.Thresholds[level]) {
                chosen = level;
                break;
            }
        }

        if (currentLevel < 0 || currentLevel == chosen || Hysteresis <= 0f) {
            return chosen;
        }

        // Staying put unless the object has moved a clear margin past the boundary. Without this, an
        // object sitting exactly on a threshold changes mesh every frame — and a level change is a
        // different silhouette, so the flicker is far more visible than the detail it protects.
        var boundary = Math.Min(currentLevel, chosen);

        if (boundary >= group.Thresholds.Length) {
            return chosen;
        }

        var threshold = group.Thresholds[boundary];
        var margin = threshold * Hysteresis;

        if (height > threshold - margin && height < threshold + margin) {
            return currentLevel;
        }

        return chosen;
    }

    void Resize(int viewCount) {
        var wanted = groups.Count * viewCount;

        if (viewStride == viewCount && current.Count >= wanted) {
            return;
        }

        // Rebuilt rather than remapped when the shape changes, and every entry starts undecided so
        // that the first frame after a resize picks a level from the screen size rather than from a
        // stale one that belonged to a different view.
        viewStride = viewCount;
        current.Clear();

        for (var i = 0; i < wanted; i++) {
            current.Add(-1);
        }
    }
}

/// <summary>One LOD group's switch points.</summary>
/// <param name="Thresholds">
///     The screen-height fraction below which each level gives way to the next, descending.
/// </param>
public readonly record struct LodGroup(float[] Thresholds) {
    /// <summary>How many levels the group has — one more than it has thresholds.</summary>
    public int LevelCount => (Thresholds?.Length ?? 0) + 1;
}
