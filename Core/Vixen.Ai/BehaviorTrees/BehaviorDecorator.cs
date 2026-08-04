// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ai;

/// <summary>What a decorator or a service is handed: the agent, its tree, and which node it is on.</summary>
/// <param name="Agent">Everything an action would get — the world, the entity, the blackboard, the clock.</param>
/// <param name="Tree">The running instance, for the few nodes that need to reach it.</param>
/// <param name="Node">The node this attachment belongs to.</param>
/// <remarks>
///     A struct passed by <c>in</c>. It is <see cref="AgentContext" /> plus the two things an
///     attachment needs and an action does not — which is also why a task takes the smaller one: a
///     task is an <see cref="IAgentAction" /> so that the same object serves a tree, a utility set and
///     a GOAP plan, and it cannot ask for something only one of the three can give it.
/// </remarks>
public readonly record struct BehaviorContext(AgentContext Agent, BehaviorTreeInstance Tree, int Node) {
    /// <summary>The blackboard the agent is thinking with.</summary>
    public Blackboard Blackboard => Agent.Blackboard;

    /// <summary>Seconds since the world started.</summary>
    /// <remarks>
    ///     Wall-clock, not the agent's own accumulated time: a cooldown and a time limit are about
    ///     how long ago something happened, and an agent that thinks less often has not therefore
    ///     been waiting less.
    /// </remarks>
    public float Now => (float)Agent.Time.TotalSeconds;
}

/// <summary>A condition attached to a node, gating entry into it and possibly interrupting it.</summary>
/// <remarks>
///     <para>
///         <b>Shared by every agent running the tree</b>, exactly like an <see cref="IAgentAction" />
///         — so a decorator that needs to remember something declares <see cref="StateSize" /> and
///         keeps it in the span it is handed. A field here is a field a thousand agents write to.
///     </para>
///     <para>
///         The surface is Unreal's, in three parts, and each part exists because a real decorator in
///         doc 37 § Part 3 needs it: <see cref="Evaluate" /> gates entry (every decorator),
///         <see cref="Finish" /> may rewrite the result on the way out (<c>Inverter</c>,
///         <c>ForceSuccess</c>), and <see cref="ShouldRepeat" /> may run the node again
///         (<c>Loop</c>, <c>ConditionalLoop</c>).
///     </para>
///     <para>
///         A class rather than an interface because most decorators want two of the five members and
///         the defaults are the interesting part. It is not a seam — doc 37 § Part 4 lists what is,
///         and a decorator is a node in a closed library rather than a policy a project replaces.
///     </para>
/// </remarks>
public abstract class BehaviorDecorator {
    /// <summary>What this may interrupt when the keys it reads change.</summary>
    /// <remarks>
    ///     ⚠ Overriding this to something other than <see cref="ObserverAborts.None" /> without also
    ///     returning the keys from <see cref="ObservedKeys" /> gives a decorator that can never fire,
    ///     because there is nothing to notice. <c>BehaviorTreeCompiler</c> reports that rather than
    ///     letting it ship.
    /// </remarks>
    public virtual ObserverAborts Aborts => ObserverAborts.None;

    /// <summary>The blackboard keys it reads.</summary>
    public virtual ReadOnlySpan<BlackboardKey> ObservedKeys => default;

    /// <summary>How many bytes of per-agent state it needs.</summary>
    public virtual int StateSize => 0;

    /// <summary>
    ///     Whether it is re-tested every step while the branch it gates is running.
    /// </summary>
    /// <remarks>
    ///     For the conditions that go false without anybody writing a key — a time limit expiring, a
    ///     cooldown ending, a target walking out of a cone. An observer cannot see those, because
    ///     nothing changed on the blackboard; the alternative would be a service writing the clock
    ///     into a key so that a decorator could observe it, which is the same test with a key in the
    ///     middle. This is off by default, and it is what the ✓ against <c>TimeLimit</c> and
    ///     <c>KeepInCone</c> in doc 37 § Part 3 means.
    /// </remarks>
    public virtual bool Continuous => false;

    /// <summary>Whether the node may be entered, or may keep running.</summary>
    /// <param name="context">The agent and the node.</param>
    /// <param name="state">Its own bytes.</param>
    /// <returns>Whether the condition holds.</returns>
    /// <remarks>
    ///     ⚠ <b>Must not write anything.</b> It is called to gate entry, to re-test a
    ///     <see cref="Continuous" /> condition, and — the case that matters — to work out whether an
    ///     observed key's change flipped it, which happens while the tree is deciding what to abort.
    ///     A decorator that wrote a key there would notify observers from inside the resolution of a
    ///     notification.
    /// </remarks>
    public abstract bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state);

    /// <summary>The node is being entered.</summary>
    /// <param name="context">The agent and the node.</param>
    /// <param name="state">Its own bytes.</param>
    public virtual void Enter(in BehaviorContext context, Span<byte> state) { }

    /// <summary>The node has finished, and this is the last chance to change what that meant.</summary>
    /// <param name="context">The agent and the node.</param>
    /// <param name="state">Its own bytes.</param>
    /// <param name="result">What the node returned.</param>
    /// <returns>What the parent should be told.</returns>
    public virtual ActionStatus Finish(in BehaviorContext context, Span<byte> state, ActionStatus result) => result;

    /// <summary>Whether the node should run again instead of returning.</summary>
    /// <param name="context">The agent and the node.</param>
    /// <param name="state">Its own bytes.</param>
    /// <param name="result">What it returned.</param>
    /// <returns>Whether to re-enter it.</returns>
    public virtual bool ShouldRepeat(in BehaviorContext context, Span<byte> state, ActionStatus result) => false;
}

/// <summary>Something that runs on an interval for as long as a composite's branch is active.</summary>
/// <remarks>
///     <para>
///         Where perception updates, target selection and blackboard maintenance go. A service is a
///         local sensor with a schedule — doc 37 § D13 — which is why there is one implementation of
///         "read the world into a key" and two front ends onto it.
///     </para>
///     <para>
///         Shared across agents like everything else here, so per-agent state is the span.
///     </para>
/// </remarks>
public abstract class BehaviorService {
    /// <summary>How many bytes of per-agent state it needs.</summary>
    public virtual int StateSize => 0;

    /// <summary>The branch became active.</summary>
    /// <param name="context">The agent and the composite.</param>
    /// <param name="state">Its own bytes.</param>
    public virtual void Enter(in BehaviorContext context, Span<byte> state) { }

    /// <summary>Its interval has elapsed.</summary>
    /// <param name="context">The agent and the composite.</param>
    /// <param name="state">Its own bytes.</param>
    /// <param name="delta">Seconds since this service last ran, which is not the frame's step.</param>
    public abstract void Tick(in BehaviorContext context, Span<byte> state, float delta);

    /// <summary>The branch stopped being active.</summary>
    /// <param name="context">The agent and the composite.</param>
    /// <param name="state">Its own bytes.</param>
    public virtual void Leave(in BehaviorContext context, Span<byte> state) { }
}
