// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>A goal a game added, and the few things that are safe to change while it is live.</summary>
/// <remarks>
///     <para>
///         The whole API for adding a constraint from code: a request goes in, a handle comes back,
///         and releasing it eases the goal out rather than snapping the limb. Everything a clip tag
///         does a handle can do, and they arbitrate together with no special case.
///     </para>
/// </remarks>
public sealed class ConstraintHandle : IDisposable {
    readonly ConstraintStack owner;

    internal ConstraintHandle(ConstraintStack owner, ConstraintGoal goal) {
        this.owner = owner;
        Goal = goal;
    }

    /// <summary>What it asks for.</summary>
    public ConstraintGoal Goal { get; }

    /// <summary>How much of it is wanted, in <c>[0, 1]</c>.</summary>
    public float Weight {
        get => Goal.Weight;
        set => Goal.Weight = value;
    }

    /// <summary>Whether it is still being solved.</summary>
    /// <remarks>
    ///     A disposed handle stays alive until it has finished easing out, which is the difference
    ///     between letting go of a ledge and being deleted from it.
    /// </remarks>
    public bool IsAlive => !Released || owner.IsEasingOut(this);

    /// <summary>Whether <see cref="Dispose" /> has been called.</summary>
    public bool Released { get; private set; }

    /// <summary>How far off it ended up, as of the last solve.</summary>
    public ConstraintResidual Residual { get; internal set; }

    /// <summary>Lets the goal go. It eases out; it does not snap.</summary>
    public void Dispose() {
        if (Released) {
            return;
        }

        Released = true;
        owner.Detach(this);
    }
}

/// <summary>
///     Every goal one character is under, resolved, arbitrated and applied to the pose after the
///     layers have been mixed.
/// </summary>
/// <remarks>
///     <para>
///         <b>An <see cref="IPoseProcessor" />, because that hook already exists and already argues
///         for itself.</b> It runs after the layer mix and before skinning, with a model-space
///         scratch buffer — which is exactly where a correction to the blended pose belongs, and is
///         where the three existing IK solvers already are. They keep working beside this one, in
///         whatever order the list gives.
///     </para>
///     <para>
///         <b>Stateful, and one per character.</b> That is not an implementation detail: every
///         visible failure of a constraint system is a discontinuity, and almost all of them come
///         from a goal appearing or disappearing between frames. A goal that becomes resolvable eases
///         in from where the effector actually is; one that stops resolving — the entity unbound, the
///         detail level dropped it, the clip blended out — eases out towards the animated pose using
///         the last frame it saw, and does not vanish. <see cref="Reset" /> clears all of it, for a
///         teleport or a cut, where continuity is <em>wrong</em>.
///     </para>
///     <para>
///         ⚠ <b>Its temporal state and its published pose are reachable from outside, on purpose.</b>
///         A scheduler that solves several characters together has to be able to read what each one
///         is under and write what each one settled at, and a type whose state was private to itself
///         could not have hosted that. This is the part of the shape that had to be decided before
///         anything else depended on it.
///     </para>
/// </remarks>
public sealed class ConstraintStack : IPoseProcessor {
    readonly List<ConstraintHandle> handles = [];
    readonly Dictionary<InstanceId, Instance> instances = [];
    readonly Dictionary<Symbol, float> suppression = [];

    ResolvedGoal[] resolved = [];
    ConstraintResidual[] residuals = [];
    InstanceId[] identities = [];
    ResolvedGoal[] placement = [];
    BoneTransform[] scratch = [];
    BoneTransform[] published = [];
    BoneTransform[] animated = [];
    BoneTransform[] correction = [];

    BoneTransform solveWorld = BoneTransform.Identity;
    bool held;
    int stamp;
    int count;
    int roots;
    int ticks;

    /// <summary>Creates a stack for a character.</summary>
    /// <param name="skeleton">The skeleton it corrects.</param>
    /// <param name="arbiter">
    ///     How conflicts resolve, or <see langword="null" /> for the shipped one.
    /// </param>
    /// <param name="solver">How a chain is moved, or <see langword="null" /> for the shipped one.</param>
    public ConstraintStack(Skeleton skeleton, IConstraintArbiter? arbiter = null, IChainSolver? solver = null) {
        ArgumentNullException.ThrowIfNull(skeleton);

        Skeleton = skeleton;
        Arbiter = arbiter ?? DefaultConstraintArbiter.Shared;
        Solver = solver ?? DefaultChainSolver.Shared;
        Bindings = new();
    }

    /// <summary>The skeleton being corrected.</summary>
    public Skeleton Skeleton { get; }

    /// <summary>How conflicts resolve.</summary>
    public IConstraintArbiter Arbiter { get; }

    /// <summary>How a chain is moved.</summary>
    public IChainSolver Solver { get; }

    /// <summary>Who the other parties are.</summary>
    public ConstraintBindings Bindings { get; }

    /// <summary>Where the character is, for bringing a world-space frame into the pose's space.</summary>
    public BoneTransform WorldTransform { get; set; } = BoneTransform.Identity;

    /// <summary>Which detail level is in force. Zero is the highest.</summary>
    /// <remarks>
    ///     D22's <b>detail</b> and <b>scope</b> knobs, which are the same number read two ways: it
    ///     picks which proxy shape set answers a surface frame, and it drops every goal whose
    ///     <see cref="ConstraintGoal.Lods" /> excludes it — fingers first, then toes, then forearms.
    ///     Dropping out of range eases the goal out rather than snapping it.
    /// </remarks>
    public byte Lod { get; set; }

    /// <summary>Solve every <i>n</i>-th frame, holding the previous correction in between.</summary>
    /// <remarks>
    ///     <para>
    ///         D22's <b>rate</b> knob, and the one with the trap. Skipping a solve on a character whose
    ///         <em>pose</em> still updates means the goal is stale, not absent — so the stage holds the
    ///         previous correction and re-applies it as an offset on top of whatever the animation now
    ///         says. That is right for a few frames and wrong for many, which is why
    ///         <see cref="ConstraintGovernor" />'s ladder is bounded and why it reports hitting the
    ///         floor instead of quietly degrading further.
    ///     </para>
    /// </remarks>
    public int SolveEvery { get; set; } = 1;

    /// <summary>How much this character matters, for a governor deciding what to spend.</summary>
    /// <remarks>Higher is more. A game usually writes distance, inverted, times a designer's multiplier.</remarks>
    public float Importance { get; set; } = 1f;

    /// <summary>Roughly what a full solve costs, in goals.</summary>
    /// <remarks>
    ///     What a governor budgets against. Goals, not microseconds, because the only honest per-goal
    ///     time is one measured on the machine the game is running on — and the count is what actually
    ///     scales.
    /// </remarks>
    public int EstimatedCost => handles.Count + (Tags?.Count ?? 0);

    /// <summary>Whether the last frame held the previous correction rather than solving.</summary>
    public bool WasHeld => held;

    /// <summary>Where the character should stand, if anything asked.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A suggestion, exactly as <see cref="Animator.LastRootMotion" /> is.</b> The
    ///         controller owns the transform and decides how much of this survives a wall. What the
    ///         stage does with it meanwhile is assume it will be taken: the pose is solved against the
    ///         suggested placement, because a character reaching for a door handle twenty centimetres
    ///         too far should mostly <em>stand somewhere else</em> and only then stretch.
    ///     </para>
    ///     <para>
    ///         A controller that refuses costs one frame of a character reaching from where it is not.
    ///         The next frame's root solve sees the refusal and asks for less.
    ///     </para>
    /// </remarks>
    public BoneTransform RootSuggestion { get; private set; } = BoneTransform.Identity;

    /// <summary>Whether anything asked the character to stand somewhere else.</summary>
    public bool HasRootSuggestion { get; private set; }

    /// <summary>The body's proxy shapes, or <see langword="null" /> if it has none.</summary>
    /// <remarks>
    ///     Only <see cref="SurfaceFrame" /> needs them, so a character with no shapes is not a
    ///     character with a broken stack — it is a character whose goals are all expressed against
    ///     joints, entities and world points, which is most of them.
    /// </remarks>
    public ProxyShapes? Shapes { get; set; }

    /// <summary>The character's own attachment points.</summary>
    public AttachmentSockets Sockets { get; } = new();

    /// <summary>Where a clip's live constraints are read from, or <see langword="null" /> for none.</summary>
    /// <remarks>
    ///     Set by <see cref="Animator" /> to its own buffer. A stack driven without an animator — a
    ///     test, a tool — leaves it null and works from handles alone.
    /// </remarks>
    public ConstraintTagBuffer? Tags { get; set; }

    /// <summary>How many goals the last solve actually applied.</summary>
    public int LastAppliedCount => count;

    /// <summary>The goals the last solve resolved, in the order the arbiter saw them.</summary>
    /// <remarks>
    ///     ⚠ <b>The buffer the solve itself used, handed out rather than copied.</b> Reading it costs
    ///     nothing, which is what lets a gizmo pass and the variation harness both work from what
    ///     actually happened rather than from a recording made for them — but it is only valid until
    ///     the next solve, and a caller keeping a <see cref="Frame" /> out of it is keeping a place
    ///     the character has since walked away from. Pair it with <see cref="LastResiduals" />: the
    ///     same order, one entry each.
    /// </remarks>
    public ReadOnlySpan<ResolvedGoal> LastSolved => resolved.AsSpan(0, count);

    /// <summary>How far off each of those ended up, in the same order.</summary>
    public ReadOnlySpan<ConstraintResidual> LastResiduals => residuals.AsSpan(0, count);

    /// <summary>
    ///     Whether a scheduler has claimed this stack for a group solve, so the animator's own
    ///     processor pass leaves it alone.
    /// </summary>
    public bool Scheduled { get; internal set; }

    /// <summary>The animator this stack was found on, once a system has gathered it.</summary>
    /// <remarks>
    ///     Set by <c>AnimationSystem</c> and null for a stack driven by hand. A grouped solve happens
    ///     outside any one animator's pass and still has to reach each member's pose, which is the
    ///     only thing this is for.
    /// </remarks>
    public Animator? Owner { get; internal set; }

    /// <summary>The pose a grouped pre-evaluation solve settled on, if one ran this frame.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes a multi-character solve possible at all.</b> A member of a group publishes
    ///     here, and the other members' evaluation reads it instead of reaching for a neighbour's
    ///     live pose — which, during the evaluation pass, is a pose halfway through being built.
    /// </remarks>
    public ReadOnlySpan<BoneTransform> Published => HasPublished ? published : [];

    /// <summary>Whether anything published this frame.</summary>
    public bool HasPublished { get; private set; }

    /// <summary>Adds a goal.</summary>
    /// <param name="goal">What it asks for.</param>
    /// <returns>The handle. Dispose it to ease the goal out.</returns>
    public ConstraintHandle Add(ConstraintGoal goal) {
        ArgumentNullException.ThrowIfNull(goal);

        var handle = new ConstraintHandle(this, goal);
        handles.Add(handle);

        return handle;
    }

    /// <summary>Turns a label down, or off.</summary>
    /// <param name="label">The label.</param>
    /// <param name="weight">
    ///     How much of anything wearing it survives, in <c>[0, 1]</c>. Zero takes the body part over
    ///     entirely.
    /// </param>
    /// <remarks>
    ///     Continuous, so a gesture system taking the head does it over a few frames rather than in
    ///     one. Suppressing to zero and easing out are different things and both are wanted: this is
    ///     the head being borrowed, not the look-at being cancelled.
    /// </remarks>
    public void Suppress(Symbol label, float weight) => suppression[label] = MathUtil.Saturate(weight);

    /// <summary>Turns a label down, or off.</summary>
    /// <param name="label">The label.</param>
    /// <param name="weight">How much survives, in <c>[0, 1]</c>.</param>
    public void Suppress(string label, float weight) => Suppress(Symbol.Intern(label), weight);

    /// <summary>Gives a label back.</summary>
    /// <param name="label">The label.</param>
    public void Release(Symbol label) => suppression.Remove(label);

    /// <summary>How much of a label survives.</summary>
    /// <param name="label">The label.</param>
    /// <returns>The multiplier, in <c>[0, 1]</c>. One when nobody has suppressed it.</returns>
    public float Suppression(Symbol label) => suppression.TryGetValue(label, out var weight) ? weight : 1f;

    /// <summary>The handles wearing a label.</summary>
    /// <param name="label">
    ///     The label, or <see cref="Symbol.None" /> for everything a game added.
    /// </param>
    /// <returns>The handles. A struct enumerator; nothing is allocated to walk it.</returns>
    public ActiveHandles Active(Symbol label = default) => new(handles, label);

    /// <summary>How far off a clip's tag ended up, as of the last solve.</summary>
    /// <param name="track">The track it came from.</param>
    /// <param name="index">Its position in that track.</param>
    /// <returns>The residual, or <see cref="ConstraintResidual.None" /> if it did not run.</returns>
    public ConstraintResidual Residual(ConstraintTrack track, int index) {
        ArgumentNullException.ThrowIfNull(track);
        return instances.TryGetValue(new(track, index), out var instance) ? instance.Residual : default;
    }

    /// <summary>Forgets every goal's history. For a teleport or a cut.</summary>
    /// <remarks>
    ///     Continuity is the point of this type and it is exactly wrong across a cut: a hand easing
    ///     from where it was standing three metres away is a hand smearing across the shot.
    /// </remarks>
    public void Reset() {
        instances.Clear();
        HasPublished = false;
        count = 0;
    }

    /// <summary>Publishes a pose for the other members of a group to read.</summary>
    /// <param name="pose">The pose, in local space.</param>
    public void Publish(ReadOnlySpan<BoneTransform> pose) {
        if (published.Length < pose.Length) {
            published = new BoneTransform[pose.Length];
        }

        pose.CopyTo(published);
        HasPublished = true;
    }

    /// <summary>Forgets the published pose. Called at the end of the frame that produced it.</summary>
    public void Unpublish() => HasPublished = false;

    /// <inheritdoc />
    /// <remarks>
    ///     Does nothing when a scheduler has claimed this stack for a group: the group is solved once
    ///     every member has a pose, which is after this pass, and solving here as well would apply
    ///     every correction twice.
    /// </remarks>
    public void Process(Animator animator, Span<BoneTransform> pose, Span<BoneTransform> model) {
        ArgumentNullException.ThrowIfNull(animator);

        Tags ??= animator.Constraints;

        if (Scheduled) {
            return;
        }

        Solve(pose, model, animator.LastDeltaTime);
    }

    /// <summary>Corrects a pose, using the stack's own model-space buffer.</summary>
    /// <param name="pose">The pose, in local space, written in place.</param>
    /// <param name="deltaTime">How much time has passed, in seconds.</param>
    /// <param name="group">The characters being solved together, if any.</param>
    /// <remarks>
    ///     What a grouped solve uses. The per-animator path passes the buffer
    ///     <see cref="IPoseProcessor" /> already hands every processor, which is why that one is not
    ///     allocated here either — this overload only owns a buffer because a group solve happens
    ///     outside any one animator's pass.
    /// </remarks>
    public void Solve(Span<BoneTransform> pose, float deltaTime, ConstraintGroup group = default) {
        if (scratch.Length < Skeleton.JointCount) {
            scratch = new BoneTransform[Skeleton.JointCount];
        }

        Solve(pose, scratch, deltaTime, group);
    }

    /// <summary>Corrects a pose.</summary>
    /// <param name="pose">The pose, in local space, written in place.</param>
    /// <param name="model">A model-space buffer of at least the skeleton's joint count.</param>
    /// <param name="deltaTime">How much time has passed, in seconds.</param>
    /// <param name="group">The characters being solved together, if any.</param>
    /// <remarks>
    ///     ⚠ <b>Allocation-free once warm.</b> Every buffer here grows to a high-water mark and is
    ///     reused; the dictionaries are keyed on values and written in place. A frame that adds no
    ///     goal and drops none allocates nothing at all, which is a requirement rather than a
    ///     nicety — this runs per character per frame.
    /// </remarks>
    public void Solve(
        Span<BoneTransform> pose,
        Span<BoneTransform> model,
        float deltaTime,
        ConstraintGroup group = default
    ) {
        stamp++;
        count = 0;
        roots = 0;

        // Provided frames belong to the frame that wrote them and are gone with it, so a provider
        // that stops writing produces an unresolved goal and an ease-out rather than a hand pinned to
        // a raycast from four seconds ago.
        try {
            Run(pose, model, deltaTime, group);
        } finally {
            Bindings.ClearProvided();
        }
    }

    void Run(Span<BoneTransform> pose, Span<BoneTransform> model, float deltaTime, ConstraintGroup group) {
        Shapes?.Frame();

        held = false;
        solveWorld = WorldTransform;
        RootSuggestion = BoneTransform.Identity;
        HasRootSuggestion = false;

        if (handles.Count == 0 && (Tags is null || Tags.Count == 0) && instances.Count == 0 && Sockets.Count == 0) {
            return;
        }

        if (SolveEvery > 1 && ticks++ % SolveEvery != 0) {
            held = Hold(pose);

            if (held) {
                return;
            }
        }

        Capture(pose);
        SkeletonPose.ComputeModelSpace(Skeleton, pose, model);

        // Before the goals resolve, so one expressed against a socket gets this frame's answer. The
        // pass below does it again once the chains have moved, which is the one the game reads.
        Sockets.Solve(Context(model));

        // ⚠ The root placement is decided before anything else resolves, because it changes where
        // everything else <em>is</em>. A world-space goal twenty centimetres out of reach becomes a
        // goal in reach once the character has been asked to stand somewhere else, and solving the
        // pose first would spend the whole correction on the arm.
        Gather(model, deltaTime, root: true);
        SolveRoot();
        Gather(model, deltaTime, root: false);

        if (count == 0) {
            Prune();
            Settle(pose, model);
            Record(pose);

            return;
        }

        Arbiter.Solve(
            new() {
                Skeleton = Skeleton,
                Model = model,
                Goals = resolved.AsSpan(0, count),
                Residuals = residuals.AsSpan(0, count),
                Solver = Solver,
                Group = group,
                DeltaTime = deltaTime
            },
            pose
        );

        for (var index = 0; index < count; index++) {
            var identity = identities[index];

            if (identity.Source is ConstraintHandle handle) {
                handle.Residual = residuals[index];
            }

            if (instances.TryGetValue(identity, out var instance)) {
                instance.Residual = residuals[index];
                instances[identity] = instance;
            }
        }

        Prune();
        Settle(pose, model);
        Record(pose);
    }

    /// <summary>Turns the goals labelled <c>root</c> into a placement for the whole character.</summary>
    void SolveRoot() {
        if (roots == 0) {
            return;
        }

        var placed = RigidBodySolver.Solve(BoneTransform.Identity, placement.AsSpan(0, roots), out var moved, out var turned);

        if (moved <= 1e-5f && turned <= 1e-5f) {
            return;
        }

        RootSuggestion = placed;
        HasRootSuggestion = true;

        // Every remaining frame resolves against where the character is being asked to stand, which
        // is the whole of why this pass is first.
        solveWorld = BoneTransform.Concatenate(placed, WorldTransform);
    }

    /// <summary>Remembers the animated pose, so a held frame has something to correct.</summary>
    void Capture(ReadOnlySpan<BoneTransform> pose) {
        if (SolveEvery <= 1) {
            return;
        }

        if (animated.Length < pose.Length) {
            animated = new BoneTransform[pose.Length];
            correction = new BoneTransform[pose.Length];
        }

        pose.CopyTo(animated);
    }

    /// <summary>Works out what the solve changed, as a delta a held frame can re-apply.</summary>
    void Record(ReadOnlySpan<BoneTransform> pose) {
        if (SolveEvery <= 1 || correction.Length < pose.Length) {
            return;
        }

        for (var index = 0; index < pose.Length; index++) {
            correction[index] = new(
                pose[index].Translation - animated[index].Translation,
                Quaternion.Concatenate(pose[index].Rotation, Quaternion.Conjugate(animated[index].Rotation)),
                Vector3.One
            );
        }
    }

    /// <summary>Re-applies the last correction on top of whatever the animation now says.</summary>
    /// <returns>Whether there was one to apply.</returns>
    /// <remarks>
    ///     ⚠ <b>An offset, not a cached pose.</b> The character is still animating on a held frame, so
    ///     writing back the pose the last solve produced would freeze the limb; composing the
    ///     <em>difference</em> onto the live pose keeps it moving and keeps the correction. It goes
    ///     stale — the goal has moved and this has not noticed — which is the whole reason the rate
    ///     ladder is bounded.
    /// </remarks>
    bool Hold(Span<BoneTransform> pose) {
        if (correction.Length < pose.Length) {
            return false;
        }

        for (var index = 0; index < pose.Length; index++) {
            pose[index] = new(
                pose[index].Translation + correction[index].Translation,
                Quaternion.Concatenate(correction[index].Rotation, pose[index].Rotation),
                pose[index].Scale
            );
        }

        return true;
    }

    /// <summary>Places the attachment points once the chains have stopped moving.</summary>
    /// <remarks>
    ///     ⚠ <b>After the arbiter, because a socket is adapted to where the hand ended up.</b> A grip
    ///     resolved against the hand's proxy shape before the arm was solved is a grip resolved against
    ///     last frame's hand — which is the one-frame lag that reads as a held object jittering.
    /// </remarks>
    void Settle(Span<BoneTransform> pose, Span<BoneTransform> model) {
        if (Sockets.Count == 0) {
            return;
        }

        SkeletonPose.ComputeModelSpace(Skeleton, pose, model);
        Shapes?.Invalidate();
        Sockets.Solve(Context(model));
    }

    ConstraintContext Context(ReadOnlySpan<BoneTransform> model, float phase = 0f) =>
        new() {
            Phase = phase,
            Skeleton = Skeleton,
            Model = model,
            Bindings = Bindings,
            WorldTransform = solveWorld,
            Shapes = Shapes,
            Sockets = Sockets
        };

    internal bool IsEasingOut(ConstraintHandle handle) =>
        instances.TryGetValue(new(handle, 0), out var instance) && instance.Weight > 0f;

    internal void Detach(ConstraintHandle handle) {
        // Left in the list until it has finished easing out. Removing it here would be the snap this
        // whole type exists to avoid.
        if (!instances.ContainsKey(new(handle, 0))) {
            handles.Remove(handle);
        }
    }

    /// <summary>Resolves every goal, eases it, and writes it into the scan arrays.</summary>
    /// <param name="model">The pose, in model space.</param>
    /// <param name="deltaTime">How much time has passed.</param>
    /// <param name="root">
    ///     Whether this is the placement pass — the goals labelled <see cref="ConstraintLabels.Root" />
    ///     — or the pose pass, which is everything else.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>Two passes rather than one filtered afterwards, because the first changes the answers
    ///     the second gets.</b> A goal wearing the root label is solved as the character's placement
    ///     and is excluded from the pose solve; a project using <c>root</c> to mean something of its
    ///     own would find those goals moving the character instead of its limbs.
    /// </remarks>
    void Gather(Span<BoneTransform> model, float deltaTime, bool root) {
        var wanted = handles.Count + (Tags?.Count ?? 0) + instances.Count;

        if (resolved.Length < wanted) {
            var size = Math.Max(8, wanted);
            Array.Resize(ref resolved, size);
            Array.Resize(ref residuals, size);
            Array.Resize(ref identities, size);
            Array.Resize(ref placement, size);
        }

        if (root) {
            roots = 0;
        } else {
            count = 0;
        }

        for (var index = handles.Count - 1; index >= 0; index--) {
            var handle = handles[index];

            if (IsRoot(handle.Goal) != root) {
                continue;
            }

            var alive = Take(
                Context(model, handle.Goal.Phase),
                new(handle, 0),
                handle.Goal,
                handle.Released ? 0f : handle.Goal.Weight,
                deltaTime,
                root
            );

            if (!alive && handle.Released) {
                handles.RemoveAt(index);
            }
        }

        if (Tags is null) {
            return;
        }

        for (var index = 0; index < Tags.Count; index++) {
            var live = Tags[index];

            if (IsRoot(live.Tag.Goal) != root) {
                continue;
            }

            // ⚠ The phase comes off the tag and not off the goal. A clip's goal object is shared by
            // every character playing it, so writing this frame's phase into it would have one
            // character's reach driven by another's playback position.
            Take(
                Context(model, live.Phase),
                new(live.Track, live.Index),
                live.Tag.Goal,
                live.Weight * live.Tag.Goal.Weight,
                deltaTime,
                root
            );
        }
    }

    /// <summary>Whether a goal is about where the character stands rather than about its pose.</summary>
    static bool IsRoot(ConstraintGoal goal) => goal.Label == ConstraintLabels.Root;

    /// <summary>One goal: resolve it, ease it, keep it if it still counts for anything.</summary>
    bool Take(
        in ConstraintContext context,
        InstanceId identity,
        ConstraintGoal goal,
        float wanted,
        float deltaTime,
        bool root
    ) {
        instances.TryGetValue(identity, out var instance);
        instance.Stamp = stamp;

        var frame = Frame.Identity;
        var resolvable = goal.Lods.Contains(Lod);

        if (resolvable) {
            // An additive offset is expressed in the frame it was measured against, which is not the
            // frame an absolute goal is placed in. A goal that names neither is in model space, which
            // is the right answer for a displacement a physics pass produced.
            var source = goal.Mode is GoalMode.Additive ? goal.Reference ?? goal.Goal : goal.Goal;
            resolvable = source is null || source.TryResolve(context, out frame);
        }

        var target = resolvable
            ? MathUtil.Saturate(wanted) * MathUtil.Saturate(goal.MaxWeight) * Suppression(goal.Label)
            : 0f;

        var duration = target > instance.Weight ? goal.EaseIn : goal.EaseOut;

        instance.Weight = duration <= 0f || deltaTime <= 0f
            ? target
            : MoveTowards(instance.Weight, target, deltaTime / duration);

        if (resolvable) {
            instance.Frame = frame;
            instance.HasFrame = true;
        }

        if (instance.Weight <= 1e-4f) {
            instances.Remove(identity);
            return false;
        }

        instances[identity] = instance;

        if (!instance.HasFrame) {
            // Never resolved even once, so there is nothing to ease from. A goal whose entity has not
            // spawned yet is not a goal that should drag a limb towards the model-space origin.
            return true;
        }

        if (root) {
            placement[roots++] = new(goal, instance.Frame, instance.Weight);
            return true;
        }

        resolved[count] = new(goal, instance.Frame, instance.Weight);
        identities[count] = identity;
        Sort(count);
        count++;

        return true;
    }

    /// <summary>Keeps the scan array in chain order as it is filled.</summary>
    /// <remarks>
    ///     An insertion pass rather than a sort at the end, because the arbiter's whole shape depends
    ///     on goals sharing a chain being contiguous, and the number of goals on one character is a
    ///     handful. Sorting a handful with a comparer costs more than placing each one as it arrives.
    /// </remarks>
    void Sort(int index) {
        var goal = resolved[index];
        var identity = identities[index];
        var chain = goal.Goal.Solved;
        var at = index;

        while (at > 0 && resolved[at - 1].Goal.Solved > chain) {
            resolved[at] = resolved[at - 1];
            identities[at] = identities[at - 1];
            at--;
        }

        resolved[at] = goal;
        identities[at] = identity;
    }

    /// <summary>Forgets instances nothing touched this frame.</summary>
    void Prune() {
        if (instances.Count <= count + roots) {
            return;
        }

        foreach (var (identity, instance) in instances) {
            if (instance.Stamp != stamp) {
                instances.Remove(identity);
            }
        }
    }

    static float MoveTowards(float from, float to, float step) =>
        MathF.Abs(to - from) <= step ? to : from + (MathF.Sign(to - from) * step);

    /// <summary>What identifies a goal from one frame to the next.</summary>
    /// <remarks>
    ///     ⚠ <b>Reference identity on the source, not a value.</b> A goal reached through a clip's
    ///     track is the same goal next frame because it is the same track and the same index — not
    ///     because its fields happen to match, which they would for the left hand and the right.
    /// </remarks>
    readonly record struct InstanceId(object Source, int Index);

    /// <summary>What is remembered about one goal between frames.</summary>
    struct Instance {
        public float Weight;
        public Frame Frame;
        public bool HasFrame;
        public ConstraintResidual Residual;
        public int Stamp;
    }

    /// <summary>The handles wearing a label, walked without allocating.</summary>
    public readonly struct ActiveHandles {
        readonly List<ConstraintHandle> handles;
        readonly Symbol label;

        internal ActiveHandles(List<ConstraintHandle> handles, Symbol label) {
            this.handles = handles;
            this.label = label;
        }

        /// <summary>Walks them.</summary>
        /// <returns>The enumerator.</returns>
        public Enumerator GetEnumerator() => new(handles, label);

        /// <summary>Walks the handles wearing a label.</summary>
        public struct Enumerator {
            readonly List<ConstraintHandle> handles;
            readonly Symbol label;
            int index = -1;

            internal Enumerator(List<ConstraintHandle> handles, Symbol label) {
                this.handles = handles;
                this.label = label;
            }

            /// <summary>The one being looked at.</summary>
            public readonly ConstraintHandle Current => handles[index];

            /// <summary>Moves on.</summary>
            /// <returns>Whether there was another.</returns>
            public bool MoveNext() {
                while (++index < handles.Count) {
                    if (!label.IsSome || handles[index].Goal.Label == label) {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
