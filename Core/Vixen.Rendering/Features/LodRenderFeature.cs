// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

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
public sealed class LodRenderFeature : SubRenderFeature, IDrawSubFeature {
    readonly List<LodGroup> groups = [];
    readonly List<Transition> current = [];
    int viewStride;

    /// <inheritdoc />
    public override string Name => "Lod";

    /// <summary>
    ///     How long a level change takes, in seconds. Zero swaps instantly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A hard swap is visible however good the thresholds are, because two levels of the same
    ///         object have different silhouettes and the eye is very good at edges appearing. During
    ///         a fade <em>both</em> levels are drawn and each is given a weight, which a material
    ///         turns into a dithered discard — dither rather than blending, because two translucent
    ///         copies of one object write depth twice and sort against each other.
    ///     </para>
    ///     <para>
    ///         Zero by default, and that is not timidity: a fade doubles the draws for the objects
    ///         crossing a threshold, and a project whose LOD levels are close enough that hysteresis
    ///         already hides the switch should not pay for it.
    ///     </para>
    /// </remarks>
    public float CrossFadeDuration { get; set; }

    /// <summary>
    ///     How long the last frame took, in seconds. Set by the host before the frame.
    /// </summary>
    /// <remarks>
    ///     Supplied rather than measured, because a renderer that reads a clock is a renderer whose
    ///     frames cannot be reproduced — and a fade is exactly the thing a golden-image test would
    ///     want to step through one frame at a time.
    /// </remarks>
    public float DeltaTime { get; set; }

    /// <summary>Which stages see the fade weight.</summary>
    public ShaderStage Stages { get; set; } = ShaderStage.Fragment;

    /// <summary>
    ///     Where the fade weight goes in the push-constant block.
    /// </summary>
    /// <remarks>
    ///     Sixty-eight by default, after the transform's <c>mat4</c> and skinning's base index — 72
    ///     of Vulkan's guaranteed 128 for all three together.
    /// </remarks>
    public int Offset { get; set; } = 68;

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
    public int LevelOf(int group, int viewIndex) => At(group, viewIndex).Level;

    /// <summary>Which level a group is fading out of in a view, or -1 when it is not fading.</summary>
    public int FadingFrom(int group, int viewIndex) => At(group, viewIndex).From;

    /// <summary>
    ///     How visible one level of a group is in a view: 1 outside a transition, and the two levels
    ///     of a transition summing to 1 during one.
    /// </summary>
    public float FadeOf(int group, int viewIndex, int level) {
        var transition = At(group, viewIndex);

        if (transition.From < 0 || CrossFadeDuration <= 0f) {
            return 1f;
        }

        var t = Math.Clamp(transition.Elapsed / CrossFadeDuration, 0f, 1f);

        if (level == transition.Level) {
            return t;
        }

        return level == transition.From ? 1f - t : 0f;
    }

    Transition At(int group, int viewIndex) {
        var slot = (group * viewStride) + viewIndex;
        return viewStride > 0 && slot >= 0 && slot < current.Count ? current[slot] : new(-1, -1, 0f);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Pushed only for an object whose group is mid-transition. Everything else is fully visible
    ///     and would be pushing a constant of 1 that a shader would multiply by — which is a per-draw
    ///     cost for the frames and the objects that are not fading, which is nearly all of them.
    /// </remarks>
    public void Draw(RenderSystem system, RenderDrawContext context, in RenderNode node) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(context);

        if (CrossFadeDuration <= 0f || context.View is not { } view) {
            return;
        }

        var member = system.Objects.Data.Data(Membership)[node.Object.Index];

        if (member.Group <= 0 || FadingFrom(member.Group, view.Index) < 0) {
            return;
        }

        var weight = FadeOf(member.Group, view.Index, member.Level);

        context.CommandList.PushConstants(
            Stages,
            Offset,
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref weight, 1))
        );
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
            current[slot] = Advance(current[slot], groups[member.Group], Height(candidate.Bounds, view));
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

            var transition = current[(member.Group * viewStride) + view.Index];

            // Both ends of a transition stay visible; everything else goes. Two draws for one object
            // is exactly what a cross-fade costs, and it is why the fade is off by default.
            if (member.Level != transition.Level && member.Level != transition.From) {
                system.Visibility.Hide(view.Index, new(index));
            }
        }
    }

    /// <summary>Moves a group's transition on by one frame and starts a new one if the level changed.</summary>
    /// <remarks>
    ///     A transition that is interrupted by another takes the level it was heading for as the one
    ///     it now fades out of, rather than the one it started from. The alternative — refusing to
    ///     turn round until the first fade finishes — makes a camera swinging past a threshold pay
    ///     the whole duration twice, and shows the level it is no longer heading towards.
    /// </remarks>
    Transition Advance(Transition transition, LodGroup group, float height) {
        var chosen = Choose(group, transition.Level, height);

        if (transition.Level < 0) {
            return new(chosen, -1, 0f);
        }

        if (chosen != transition.Level) {
            return CrossFadeDuration <= 0f ? new(chosen, -1, 0f) : new(chosen, transition.Level, 0f);
        }

        if (transition.From < 0) {
            return transition;
        }

        var elapsed = transition.Elapsed + MathF.Max(DeltaTime, 0f);

        return elapsed >= CrossFadeDuration ? new(chosen, -1, 0f) : transition with { Elapsed = elapsed };
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
            current.Add(new(-1, -1, 0f));
        }
    }

    /// <summary>What one group is showing in one view, and what it is still fading out of.</summary>
    /// <param name="Level">The level it has chosen, or -1 before the first frame that saw it.</param>
    /// <param name="From">The level being faded out, or -1 when nothing is.</param>
    /// <param name="Elapsed">How far into the fade it is, in seconds.</param>
    readonly record struct Transition(int Level, int From, float Elapsed);
}

/// <summary>One LOD group's switch points.</summary>
/// <param name="Thresholds">
///     The screen-height fraction below which each level gives way to the next, descending.
/// </param>
public readonly record struct LodGroup(float[] Thresholds) {
    /// <summary>How many levels the group has — one more than it has thresholds.</summary>
    public int LevelCount => (Thresholds?.Length ?? 0) + 1;
}
