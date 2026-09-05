// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>One property on its way from one value to another.</summary>
readonly record struct RunningTransition(
    StyleNodeId Element,
    int Property,
    StyleValue From,
    StyleValue To,
    float Duration,
    float Delay,
    TimingFunction Timing,
    float StartedAt
) {
    /// <summary>Where it has got to.</summary>
    /// <param name="now">The current time in seconds.</param>
    /// <returns>The interpolated value.</returns>
    public StyleValue ValueAt(float now) => StyleValue.Lerp(From, To, Timing.Evaluate(Progress(now)));

    /// <summary>How far through the duration it is.</summary>
    /// <param name="now">The current time in seconds.</param>
    /// <returns>Progress in <c>[0, 1]</c>.</returns>
    public float Progress(float now) {
        if (Duration <= 0f) {
            return 1f;
        }

        return Math.Clamp((now - StartedAt - Delay) / Duration, 0f, 1f);
    }

    /// <summary>Whether it has arrived.</summary>
    /// <param name="now">The current time in seconds.</param>
    /// <returns>Whether it is done.</returns>
    public bool IsFinished(float now) => now >= StartedAt + Delay + Duration;
}

/// <summary>One keyframe animation an element is running, and when it started.</summary>
/// <param name="Spec">What it is.</param>
/// <param name="StartedAt">The time in seconds at which its first iteration began.</param>
readonly record struct RunningAnimation(AnimationSpec Spec, float StartedAt);

/// <summary>Runs transitions, and hands the cascade the values they are currently at.</summary>
/// <remarks>
///     <para>
///         A transition is started by a <i>comparison</i>, not by an event. When an element's style
///         is resolved, the animator is shown the value the cascade arrived at and the value the
///         element is currently displaying; where they differ and a <c>transition</c> rule covers
///         the property, it starts one. That is what makes transitions work for any cause of change
///         — a class toggled, a stylesheet reloaded, a parent's inherited colour moving — rather
///         than only for the ones somebody remembered to hook.
///     </para>
///     <para>
///         <b>Interrupting one is the case that separates a good implementation from a bad one.</b>
///         Hovering away halfway through a fade must reverse from where it actually is, not from
///         where it started, and must not take the full duration to travel the half it has left. A
///         new transition therefore begins at the running one's current value and is given a
///         duration scaled by how far it still has to go.
///     </para>
///     <para>
///         Time is passed in rather than read. The animator has no clock, which is what lets a test
///         step it through a fade deterministically and what lets the engine drive it from the fixed
///         step in <c>Vixen.Engine</c> without this project knowing that exists.
///     </para>
/// </remarks>
public sealed class Animator {
    readonly Dictionary<(int Element, int Property), RunningTransition> running = [];
    readonly List<(int Element, int Property)> finished = [];
    readonly List<TransitionSpec> specs = [];
    readonly List<AnimationSpec> animationSpecs = [];
    readonly Dictionary<int, List<RunningAnimation>> animations = [];
    readonly StyleValueParser parser;
    readonly TransitionParser transitions;
    readonly KeyframesTable keyframes;
    readonly NameTable properties;
    readonly NameTable values;

    /// <summary>What a property computes to on an element that never mentioned it.</summary>
    /// <remarks>
    ///     ⚠ Owned by the animator rather than by <c>StyleResolver</c>, because it is the only reader
    ///     that needs one. Materialising an initial value into every <see cref="ComputedStyle" />
    ///     would put dozens of entries into every element's dictionary to serve a question only a
    ///     transition asks — and would change what "the style holds this property" means for every
    ///     other consumer in the engine.
    /// </remarks>
    readonly InitialValues initials;
    readonly int animationName;
    readonly int animationDuration;
    readonly int animationDelay;
    readonly int animationTiming;
    readonly int animationIterations;
    readonly int animationDirection;
    readonly int animationFill;
    readonly int transitionProperty;
    readonly int transitionPropertyLonghand;
    readonly int transitionDuration;
    readonly int transitionDelay;
    readonly int transitionTiming;

    /// <summary>Creates an animator.</summary>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table declaration values are interned in.</param>
    /// <param name="keywords">The table identifiers are interned in.</param>
    /// <param name="keyframes">The <c>@keyframes</c> rules.</param>
    public Animator(NameTable properties, NameTable values, NameTable keywords, KeyframesTable keyframes) {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(keywords);
        ArgumentNullException.ThrowIfNull(keyframes);

        this.properties = properties;
        this.values = values;
        this.keyframes = keyframes;
        parser = new StyleValueParser(values, keywords);
        this.transitions = new TransitionParser(properties);
        initials = new InitialValues(properties, values);

        animationName = properties.Intern("animation-name");
        animationDuration = properties.Intern("animation-duration");
        animationDelay = properties.Intern("animation-delay");
        animationTiming = properties.Intern("animation-timing-function");
        animationIterations = properties.Intern("animation-iteration-count");
        animationDirection = properties.Intern("animation-direction");
        animationFill = properties.Intern("animation-fill-mode");

        transitionProperty = properties.Intern("transition");
        transitionPropertyLonghand = properties.Intern("transition-property");
        transitionDuration = properties.Intern("transition-duration");
        transitionDelay = properties.Intern("transition-delay");
        transitionTiming = properties.Intern("transition-timing-function");
    }

    /// <summary>Whether the user has asked for less movement, and everything therefore snaps.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Set from <see cref="MediaPreferences.Motion" /> by
    ///         <see cref="StyleEngine.SetMedia(MediaContext)" />, and false until something says
    ///         otherwise.</b> A transition is not started and a <c>@keyframes</c> animation is not
    ///         run, so a property arrives at its new value the instant the cascade decides it — which
    ///         is exactly what <c>transition-duration: 0s</c> already meant here, on the path
    ///         <see cref="Observe" /> already had.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A framework switch <i>and</i> a media feature, because neither alone is
    ///         honest.</b> The web's answer is the query alone, and it is right that an author who
    ///         writes <c>@media (prefers-reduced-motion: reduce)</c> should be able to say what
    ///         happens; but a toolkit whose default is "animate anyway" makes every application that
    ///         did not think about it an application that ignores the preference, and this framework
    ///         ships transitions, keyframes and springs that an author gets without asking. AppKit's
    ///         <c>accessibilityDisplayShouldReduceMotion</c> is honoured by the toolkit for the same
    ///         reason. An author who wants a reduced-but-present motion writes it in the media block
    ///         and turns this off.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A transition already in flight when this is turned on finishes; a keyframe
    ///         animation does not, and the asymmetry is the two being different things.</b> A
    ///         transition has an end — cutting it short freezes a panel at whatever opacity it had
    ///         reached, which is the one state no stylesheet asked for. An animation may loop
    ///         forever, so there is no end to let it reach and a spinner would go on spinning.
    ///     </para>
    /// </remarks>
    public bool ReduceMotion { get; set; }

    /// <summary>How many transitions are running.</summary>
    public int RunningCount => running.Count;

    /// <summary>How many animations are running, over every element.</summary>
    /// <remarks>An element with <c>animation-name: spin, pulse</c> contributes two.</remarks>
    public int AnimationCount {
        get {
            var count = 0;

            foreach (var entries in animations.Values) {
                count += entries.Count;
            }

            return count;
        }
    }

    /// <summary>How many elements have an animation on them.</summary>
    public int AnimatedElementCount => animations.Count;

    /// <summary>Whether anything is running, and therefore whether a frame needs to restyle.</summary>
    public bool IsIdle => running.Count == 0 && animations.Count == 0;

    /// <summary>Notices what a newly resolved style changed, and starts transitions for it.</summary>
    /// <param name="element">The element.</param>
    /// <param name="before">What it was displaying, or null the first time.</param>
    /// <param name="after">What the cascade has just decided.</param>
    /// <param name="now">The current time in seconds.</param>
    /// <remarks>
    ///     Nothing transitions on the first resolve. An element fading in from whatever the
    ///     uninitialised value happened to be is the classic implementation of this done wrong, and
    ///     the correct behaviour — appear as specified, transition only on change — is what CSS
    ///     specifies and what anyone expects.
    /// </remarks>
    public void Observe(StyleNodeId element, ComputedStyle? before, ComputedStyle after, float now) {
        ArgumentNullException.ThrowIfNull(after);

        StartAnimations(element, after, now);

        if (before is null || ReferenceEquals(before, after) || ReduceMotion) {
            return;
        }

        if (!ReadSpecs(after)) {
            return;
        }

        foreach (var property in after.Properties) {
            Start(property);
        }

        // ⚠ <b>And every property the old style had that the new one does not, which is the same
        // defect approached from the other side and the more visible half of it.</b> A hover rule
        // that adds <c>margin-left: 9px</c> leaves the property out of the computed style again the
        // moment the pointer leaves — so a loop over <see cref="ComputedStyle.Properties" /> of
        // <paramref name="after" /> alone never visits it, and the fade in happened while the fade
        // back did not. That asymmetry is invisible in a fixture that only ever adds a class, which
        // is what every transition fixture here did.
        foreach (var property in before.Properties) {
            if (!after.TryGet(property, out _)) {
                Start(property);
            }
        }

        return;

        void Start(int property) {
            var spec = SpecFor(property);
            if (spec is not { } wanted || wanted.Duration <= 0f) {
                return;
            }

            // What it is *displaying*, which is the running transition's current value where there
            // is one and the previous computed value where there is not. Reading the previous
            // computed value alone is what makes an interrupted fade jump.
            var key = (element.Index, property);
            var displayed = running.TryGetValue(key, out var current)
                ? current.ValueAt(now)
                : Computed(before, property);

            var destination = Computed(after, property);

            if (displayed.Kind == StyleValueKind.Unknown
                || destination.Kind == StyleValueKind.Unknown
                || displayed == destination) {
                running.Remove(key);

                return;
            }

            running[key] = new RunningTransition(
                element,
                property,
                displayed,
                destination,
                wanted.Duration * RemainingFraction(current, running.ContainsKey(key), destination, displayed),
                wanted.Delay,
                wanted.Timing,
                now
            );
        }
    }

    /// <summary>What a style computes a property to, filling in an initial value where it says nothing.</summary>
    /// <param name="style">The computed style.</param>
    /// <param name="property">The interned property name.</param>
    /// <returns>The value, or <see cref="StyleValue.Unknown" /> where there is nothing to say.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The computed-value stage this cascade does not otherwise have, and it exists for
    ///         exactly one reader.</b> A <see cref="ComputedStyle" /> holds only what a declaration or
    ///         an inheritance put in it, so "absent" and "computes to its initial value" are one state
    ///         here — which every consumer downstream is content with, because a missing length means
    ///         zero to the layout store and a missing colour means nothing is painted. A transition
    ///         cannot be content with it: it needs a value to travel from and a value to travel to,
    ///         and <c>absent</c> is neither.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="StyleValue.Unknown" /> is still the answer for a property with no entry,
    ///         and that is the honest partial rather than an omission.</b> See
    ///         <see cref="InitialValues" /> for the two rules that decide the table — it does not
    ///         inherit, and its initial is something <see cref="StyleValue.Lerp" /> can travel from.
    ///         A property outside them keeps the behaviour it had: no fade, and the new value the
    ///         instant the cascade decides it. Guessing at the rest would put a wrong fade where there
    ///         is currently a correct snap.
    ///     </para>
    /// </remarks>
    StyleValue Computed(ComputedStyle style, int property) {
        if (style.TryGet(property, out var declared)) {
            return parser.Parse(declared);
        }

        return initials.TryGet(property, out var initial) ? parser.Parse(initial) : StyleValue.Unknown;
    }

    /// <summary>Starts or stops the element's keyframe animations.</summary>
    /// <remarks>
    ///     <para>
    ///         An animation whose name and parameters are unchanged keeps its start time and
    ///         therefore its place in the cycle. Restarting it whenever the style is re-resolved would
    ///         make a spinner stutter every time anything else on the element changed — which, with
    ///         invalidation working properly, is exactly when it would be least expected.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Matched by position in the list, not by name.</b> <c>animation-name: spin,
    ///         pulse</c> becoming <c>spin, flash</c> must leave the spinner where it is and start the
    ///         flash from zero — matching by name would do that too, right up until a stylesheet
    ///         reorders the list, where every animation would keep running and the two would have
    ///         swapped which of them wins for a shared property. Position is the identity CSS gives
    ///         these.
    ///     </para>
    /// </remarks>
    void StartAnimations(StyleNodeId element, ComputedStyle style, float now) {
        if (ReduceMotion) {
            // ⚠ Removed rather than left alone, unlike a transition. An animation with
            // `animation-iteration-count: infinite` — which is every spinner, pulse and shimmer a
            // stylesheet writes — has no end to run out to, so leaving one in place would mean the
            // preference stopped exactly the motion that stops by itself and none of the motion that
            // does not.
            animations.Remove(element.Index);

            return;
        }

        if (!ReadAnimations(style)) {
            animations.Remove(element.Index);
            return;
        }

        animations.TryGetValue(element.Index, out var current);

        // The overwhelmingly common frame: the same animations as last time. Rebuilding the list
        // anyway would allocate once per animating element per restyle, which for a document with a
        // spinner in it is every frame.
        if (current is not null && Unchanged(current, animationSpecs)) {
            return;
        }

        var updated = new List<RunningAnimation>(animationSpecs.Count);

        for (var i = 0; i < animationSpecs.Count; i++) {
            updated.Add(
                current is not null && i < current.Count && current[i].Spec == animationSpecs[i]
                    ? current[i]
                    : new RunningAnimation(animationSpecs[i], now)
            );
        }

        animations[element.Index] = updated;
    }

    static bool Unchanged(List<RunningAnimation> current, List<AnimationSpec> wanted) {
        if (current.Count != wanted.Count) {
            return false;
        }

        for (var i = 0; i < current.Count; i++) {
            if (current[i].Spec != wanted[i]) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Discards everything that has arrived.</summary>
    /// <param name="now">The current time in seconds.</param>
    /// <returns>How many transitions finished.</returns>
    public int Advance(float now) {
        finished.Clear();

        foreach (var (key, transition) in running) {
            if (transition.IsFinished(now)) {
                finished.Add(key);
            }
        }

        foreach (var key in finished) {
            running.Remove(key);
        }

        return finished.Count;
    }

    /// <summary>The value a keyframe animation puts a property at, if one does.</summary>
    /// <param name="element">The element.</param>
    /// <param name="property">The interned property.</param>
    /// <param name="now">The current time in seconds.</param>
    /// <param name="value">Receives the value.</param>
    /// <returns>Whether an animation is contributing one.</returns>
    /// <remarks>
    ///     ⚠ <b>The last animation that has an opinion wins.</b> CSS Animations 1 §3: where two of an
    ///     element's animations set the same property, the one closer to the end of
    ///     <c>animation-name</c> decides it. Every one of them is still asked, because an animation
    ///     that says nothing about this property must not stop an earlier one that does.
    /// </remarks>
    public bool TryGetAnimated(StyleNodeId element, int property, float now, out StyleValue value) {
        value = StyleValue.Unknown;

        if (!animations.TryGetValue(element.Index, out var entries)) {
            return false;
        }

        var found = false;

        foreach (var entry in entries) {
            if (TryGetAnimated(entry, property, now, out var candidate)) {
                value = candidate;
                found = true;
            }
        }

        return found;
    }

    bool TryGetAnimated(RunningAnimation entry, int property, float now, out StyleValue value) {
        value = StyleValue.Unknown;

        if (!entry.Spec.TryOffsetAt(now - entry.StartedAt, out var offset)
            || !keyframes.TryGet(entry.Spec.Name, out var stops)
            || stops.Count == 0) {
            return false;
        }

        // The two stops the offset falls between, and how far between them. Stops are sorted, and a
        // property absent from a stop simply is not animated by it — which is why the search is per
        // property rather than per stop.
        var before = -1;
        var after = -1;

        for (var i = 0; i < stops.Count; i++) {
            if (!Declares(stops[i], property, out _)) {
                continue;
            }

            if (stops[i].Offset <= offset) {
                before = i;
            } else if (after < 0) {
                after = i;
            }
        }

        if (before < 0 && after < 0) {
            return false;
        }

        if (before < 0) {
            Declares(stops[after], property, out var only);
            value = parser.Parse(only);
            return true;
        }

        Declares(stops[before], property, out var start);

        if (after < 0) {
            value = parser.Parse(start);
            return true;
        }

        Declares(stops[after], property, out var end);

        var span = stops[after].Offset - stops[before].Offset;
        var t = span <= 0f ? 0f : (offset - stops[before].Offset) / span;

        value = StyleValue.Lerp(parser.Parse(start), parser.Parse(end), t);
        return true;
    }

    bool Declares(Keyframe stop, int property, out int value) {
        foreach (var declaration in keyframes.DeclarationsOf(stop.Declarations)) {
            if (declaration.Property != property) {
                continue;
            }

            value = declaration.Value;
            return true;
        }

        value = NameTable.None;
        return false;
    }

    /// <summary>The value a property is currently displaying, if it is mid-transition.</summary>
    /// <param name="element">The element.</param>
    /// <param name="property">The interned property.</param>
    /// <param name="now">The current time in seconds.</param>
    /// <param name="value">Receives the value.</param>
    /// <returns>Whether a transition is running for it.</returns>
    public bool TryGetCurrent(StyleNodeId element, int property, float now, out StyleValue value) {
        if (running.TryGetValue((element.Index, property), out var transition)) {
            value = transition.ValueAt(now);
            return true;
        }

        value = StyleValue.Unknown;
        return false;
    }

    /// <summary>Overlays the running transitions onto a style, as CSS's transition tier.</summary>
    /// <param name="element">The element.</param>
    /// <param name="style">Its cascaded style.</param>
    /// <param name="now">The current time in seconds.</param>
    /// <returns>The style with in-flight values substituted, or the same instance if none are.</returns>
    /// <remarks>
    ///     Applied over the finished cascade rather than inside it, which is what
    ///     <see cref="CascadeRanks.Transition" /> being the highest rank in the table means: a
    ///     transitioning value beats everything, <c>!important</c> included. That is CSS Cascading 5
    ///     §6.2 and it is the only arrangement that works — a transition that could be outvoted
    ///     would stutter whenever anything else changed.
    /// </remarks>
    public ComputedStyle Apply(StyleNodeId element, ComputedStyle style, float now) {
        ArgumentNullException.ThrowIfNull(style);

        if (running.Count == 0 && animations.Count == 0) {
            return style;
        }

        List<KeyValuePair<int, int>>? overlaid = null;

        for (var i = 0; i < style.Count; i++) {
            var property = style.Properties[i];

            // Transitions above animations, which is the order CSS Cascading 5 §6.2 puts them in and
            // is the only one that reads right: a transition is a response to something that just
            // happened and has to win over a loop that was already running.
            if (!TryGetCurrent(element, property, now, out var value)
                && !TryGetAnimated(element, property, now, out value)) {
                continue;
            }

            overlaid ??= Copy(style);
            overlaid[i] = new KeyValuePair<int, int>(property, values.Intern(value.ToCss(values)));
        }

        overlaid = Introduce(element, style, now, overlaid);

        return overlaid is null ? style : ComputedStyle.Create(overlaid, style.Parent);
    }

    /// <summary>Adds the properties this element's animations name and its cascade never gave it.</summary>
    /// <param name="element">The element.</param>
    /// <param name="style">Its cascaded style.</param>
    /// <param name="now">The current time in seconds.</param>
    /// <param name="overlaid">The copy the loop above made, or <c>null</c> if it changed nothing.</param>
    /// <returns>The copy, made here if it did not exist yet, or <c>null</c> if there was nothing to add.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the whole difference between a transition and an animation, and
    ///         <see cref="Apply" /> used to treat them the same.</b> Overlaying only the properties
    ///         already in the computed style is right for a transition — <see cref="Observe" /> takes
    ///         the from-value out of the previous style, so a transition on a property the cascade
    ///         never set has nothing to start from. A <c>@keyframes</c> block is the opposite: it is a
    ///         complete description of the property over time and asks the cascade for nothing. So
    ///         <c>@keyframes spin { to { rotate: 360deg } }</c> on a rule that does not itself declare
    ///         <c>rotate</c> parsed, started, counted, and answered
    ///         <see cref="TryGetAnimated(StyleNodeId, int, float, out StyleValue)" /> correctly — and
    ///         moved nothing, because the loop it was answering never asked. Writing
    ///         <c>rotate: 0deg</c> into the rule made it work, which is not something CSS asks an
    ///         author to do.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every stop of every running animation, not the first.</b> A block may declare a
    ///         property at <c>to</c> and nowhere else, which is the shape the example above has; a
    ///         scan that stopped at the first keyframe would introduce nothing for it.
    ///     </para>
    /// </remarks>
    List<KeyValuePair<int, int>>? Introduce(
        StyleNodeId element,
        ComputedStyle style,
        float now,
        List<KeyValuePair<int, int>>? overlaid
    ) {
        if (!animations.TryGetValue(element.Index, out var entries)) {
            return overlaid;
        }

        List<int>? introduced = null;

        foreach (var entry in entries) {
            if (!keyframes.TryGet(entry.Spec.Name, out var stops)) {
                continue;
            }

            foreach (var stop in stops) {
                foreach (var declaration in keyframes.DeclarationsOf(stop.Declarations)) {
                    var property = declaration.Property;

                    // The loop in `Apply` already answered for anything the cascade set, and a
                    // property named by two stops or by two animations must not be appended twice —
                    // `ComputedStyle` is a sorted table and a duplicate key makes its binary search
                    // return whichever of them it lands on.
                    if (style.TryGet(property, out _) || introduced?.Contains(property) is true) {
                        continue;
                    }

                    // Transitions still first, for `Apply`'s reason. A transition on a property the
                    // cascade never set cannot start, so this arm is all but unreachable — it is here
                    // so that the precedence is stated once rather than in two places that can drift.
                    if (!TryGetCurrent(element, property, now, out var value)
                        && !TryGetAnimated(element, property, now, out value)) {
                        continue;
                    }

                    overlaid ??= Copy(style);
                    overlaid.Add(new KeyValuePair<int, int>(property, values.Intern(value.ToCss(values))));
                    (introduced ??= []).Add(property);
                }
            }
        }

        return overlaid;
    }

    /// <summary>Forgets everything, as a stylesheet reload must.</summary>
    public void Clear() {
        running.Clear();
        animations.Clear();
    }

    /// <summary>Moves what is running to follow a compacted tree.</summary>
    /// <param name="remap">The mapping <see cref="StyleTree.Compact" /> produced.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Remapped rather than cleared</b>, which is the whole reason this method exists —
    ///         <see cref="Clear" /> was already available and would have been one line. Clearing
    ///         restarts every fade and every animation on the frame a document happened to compact,
    ///         so removing an item from a list would visibly jolt the ones that were mid-transition
    ///         around it. That is a worse bug than the leak it is fixing, and a rarer one, which is
    ///         the combination nobody finds.
    ///     </para>
    ///     <para>
    ///         Anything running for a slot that did not survive is dropped: the element it belonged to
    ///         is gone, so there is nothing left to animate.
    ///     </para>
    /// </remarks>
    public void Compact(ReadOnlySpan<int> remap) {
        // Rebuilt rather than updated in place, because a dictionary cannot be re-keyed while it is
        // being walked and two slots can map onto one another's old keys.
        var movedTransitions = new List<(int Element, int Property, RunningTransition Value)>(running.Count);

        foreach (var ((element, property), transition) in running) {
            var to = At(remap, element);

            if (to >= 0) {
                movedTransitions.Add((to, property, transition));
            }
        }

        running.Clear();

        foreach (var (element, property, transition) in movedTransitions) {
            running[(element, property)] = transition;
        }

        var movedAnimations = new List<(int Element, List<RunningAnimation> Value)>(animations.Count);

        foreach (var (element, entry) in animations) {
            var to = At(remap, element);

            if (to >= 0) {
                movedAnimations.Add((to, entry));
            }
        }

        animations.Clear();

        foreach (var (element, entry) in movedAnimations) {
            animations[element] = entry;
        }
    }

    /// <summary>Where a slot went, or -1 if it is past the end of the mapping.</summary>
    /// <remarks>
    ///     Past the end means created after the compaction was measured, which cannot happen from
    ///     inside one pass — but reading off the end of a caller's span would be a much worse way to
    ///     find that out than dropping the entry.
    /// </remarks>
    static int At(ReadOnlySpan<int> remap, int element) =>
        (uint) element < (uint) remap.Length ? remap[element] : -1;

    /// <summary>Reads whichever form of <c>animation</c> the stylesheet produced.</summary>
    /// <returns>Whether the element animates anything.</returns>
    /// <remarks>
    ///     <para>
    ///         ExCSS expands the <c>animation</c> shorthand into longhands and invents defaults for
    ///         the parts that were left out, so unlike <c>transition</c> this can read the longhands —
    ///         with the same caveat, which is that a <c>spring()</c> in the timing function would stop
    ///         it doing so. The shorthand fallback is the same code path either way.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every one of these properties is a list, and the lists are matched by position and
    ///         then <i>cycled</i>.</b> <c>animation-name: spin, pulse</c> with
    ///         <c>animation-duration: 1s</c> gives both a second, because the shorter list repeats;
    ///         with <c>1s, 2s</c> they get one each. That is CSS Animations 1 §4.4, and it is the
    ///         reason a naive reader that took the first value of each longhand gave the second
    ///         animation the first one's timing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>animation-name</c> decides how many there are</b> — not the longest list. A
    ///         stylesheet with one name and three durations has one animation; the extra durations are
    ///         dropped, which is what the specification says and what stops a typo in a duration list
    ///         from inventing animations with no keyframes.
    ///     </para>
    /// </remarks>
    bool ReadAnimations(ComputedStyle style) {
        animationSpecs.Clear();

        if (!style.TryGet(animationName, out var named)) {
            return false;
        }

        var names = values.NameOf(named);
        if (names.Length == 0) {
            return false;
        }

        var durations = Longhand(style, animationDuration);
        var delays = Longhand(style, animationDelay);
        var timings = Longhand(style, animationTiming);
        var counts = Longhand(style, animationIterations);
        var directions = Longhand(style, animationDirection);
        var fills = Longhand(style, animationFill);

        var ranges = TransitionParser.TopLevelSplit(names.AsSpan(), ',');

        for (var index = 0; index < ranges.Count; index++) {
            // ⚠ Quotes stripped: a `@keyframes` name may be written as a string, and `"spin"` and
            // `spin` name the same rule. The index still advances for a name this skips, because the
            // other lists are positional and skipping one would shift every later animation's
            // duration onto its neighbour.
            var name = names.AsSpan(ranges[index]).Trim().Trim("\"'");

            if (name.IsEmpty || name.Equals("none", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var duration = TransitionParser.TryDuration(Nth(durations, index), out var seconds) ? seconds : 0f;

            // A zero-duration animation contributes nothing at any moment, so it is dropped here
            // rather than kept as an entry that every frame asks and every frame declines.
            if (duration <= 0f) {
                continue;
            }

            animationSpecs.Add(
                new AnimationSpec(
                    name.ToString(),
                    duration,
                    TransitionParser.TryDuration(Nth(delays, index), out var waited) ? waited : 0f,
                    TransitionParser.TryTimingFunction(Nth(timings, index), out var parsed)
                        ? parsed
                        : TimingFunction.Ease,
                    Iterations(Nth(counts, index)),
                    Direction(Nth(directions, index)),
                    Fill(Nth(fills, index))
                )
            );
        }

        return animationSpecs.Count > 0;
    }

    string Longhand(ComputedStyle style, int property) => style.TryGet(property, out var value) ? values.NameOf(value) : "";

    /// <summary>The <paramref name="index" />th entry of a comma-separated longhand, cycling.</summary>
    static string Nth(string text, int index) {
        if (text.Length == 0) {
            return "";
        }

        var ranges = TransitionParser.TopLevelSplit(text.AsSpan(), ',');

        // ⚠ The modulo is the cycling rule, and it is why this cannot simply index. One duration for
        // three animations means all three get it; two durations for three means the third gets the
        // first one back again.
        return ranges.Count == 0 ? "" : text.AsSpan(ranges[index % ranges.Count]).Trim().ToString();
    }

    static float Iterations(string text) =>
        string.Equals(text, "infinite", StringComparison.OrdinalIgnoreCase)
            ? float.PositiveInfinity
            : float.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var count
            )
                ? count
                : 1f;

    static AnimationDirection Direction(string text) =>
        text.ToLowerInvariant() switch {
            "reverse" => AnimationDirection.Reverse,
            "alternate" => AnimationDirection.Alternate,
            "alternate-reverse" => AnimationDirection.AlternateReverse,
            _ => AnimationDirection.Normal
        };

    static AnimationFill Fill(string text) =>
        text.ToLowerInvariant() switch {
            "forwards" => AnimationFill.Forwards,
            "backwards" => AnimationFill.Backwards,
            "both" => AnimationFill.Both,
            _ => AnimationFill.None
        };

    /// <summary>
    ///     How much of a transition's duration a re-targeted one should get.
    /// </summary>
    /// <remarks>
    ///     Reversing halfway through a fade should take half as long, not the full duration —
    ///     otherwise moving the mouse on and off a button repeatedly makes it drift further behind
    ///     with every pass. CSS calls this the reversing-shortening factor and specifies it only for
    ///     an exact reversal; applying it to any interruption is a small deliberate generalisation,
    ///     and it is the behaviour that stops a half-finished transition feeling sticky.
    /// </remarks>
    static float RemainingFraction(RunningTransition current, bool wasRunning, StyleValue destination, StyleValue displayed) {
        if (!wasRunning) {
            return 1f;
        }

        // Only meaningful for something with a magnitude; a discrete swap has no "how far".
        if (destination.Kind != StyleValueKind.Number && destination.Kind != StyleValueKind.Length) {
            return 1f;
        }

        var total = MathF.Abs(current.To.Number - current.From.Number);
        if (total <= float.Epsilon) {
            return 1f;
        }

        return Math.Clamp(MathF.Abs(destination.Number - displayed.Number) / total, 0.05f, 1f);
    }

    static List<KeyValuePair<int, int>> Copy(ComputedStyle style) {
        var pairs = new List<KeyValuePair<int, int>>(style.Count);
        for (var i = 0; i < style.Count; i++) {
            pairs.Add(new KeyValuePair<int, int>(style.Properties[i], style.Values[i]));
        }

        return pairs;
    }

    /// <summary>Reads whichever form of <c>transition</c> the stylesheet produced.</summary>
    /// <returns>Whether the element transitions anything at all.</returns>
    bool ReadSpecs(ComputedStyle style) {
        specs.Clear();

        // The shorthand, when ExCSS could not expand it — which is exactly when the author used
        // `spring()`, and is why this cannot simply read the longhands.
        if (style.TryGet(transitionProperty, out var shorthand)
            && transitions.TryParseShorthand(values.NameOf(shorthand), specs)) {
            return true;
        }

        if (!style.TryGet(transitionPropertyLonghand, out var named)) {
            return false;
        }

        var duration = style.TryGet(transitionDuration, out var d)
            && TransitionParser.TryDuration(values.NameOf(d), out var seconds)
                ? seconds
                : 0f;

        var delay = style.TryGet(transitionDelay, out var l)
            && TransitionParser.TryDuration(values.NameOf(l), out var waited)
                ? waited
                : 0f;

        var timing = style.TryGet(transitionTiming, out var t)
            && TransitionParser.TryTimingFunction(values.NameOf(t), out var parsed)
                ? parsed
                : TimingFunction.Ease;

        var text = values.NameOf(named);
        foreach (var range in TransitionParser.TopLevelSplit(text.AsSpan(), ',')) {
            var name = text.AsSpan()[range].Trim();
            if (name.IsEmpty) {
                continue;
            }

            specs.Add(
                new TransitionSpec(
                    name.Equals("all", StringComparison.OrdinalIgnoreCase)
                        ? NameTable.None
                        : properties.Intern(name.ToString()),
                    duration,
                    delay,
                    timing
                )
            );
        }

        return specs.Count > 0;
    }

    TransitionSpec? SpecFor(int property) {
        // Last match wins, which is CSS's rule for `transition: all 1s, opacity 2s` — the later
        // entry overrides the blanket one for the property it names.
        TransitionSpec? found = null;

        foreach (var spec in specs) {
            if (spec.Property == NameTable.None || spec.Property == property) {
                found = spec;
            }
        }

        return found;
    }
}
