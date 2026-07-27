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
    readonly Dictionary<int, (AnimationSpec Spec, float StartedAt)> animations = [];
    readonly StyleValueParser parser;
    readonly TransitionParser transitions;
    readonly KeyframesTable keyframes;
    readonly NameTable properties;
    readonly NameTable values;
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

    /// <summary>How many transitions are running.</summary>
    public int RunningCount => running.Count;

    /// <summary>How many animations are running.</summary>
    public int AnimationCount => animations.Count;

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

        if (before is null || ReferenceEquals(before, after)) {
            return;
        }

        if (!ReadSpecs(after)) {
            return;
        }

        foreach (var property in after.Properties) {
            var spec = SpecFor(property);
            if (spec is not { } wanted || wanted.Duration <= 0f) {
                continue;
            }

            if (!after.TryGet(property, out var target)) {
                continue;
            }

            // What it is *displaying*, which is the running transition's current value where there
            // is one and the previous computed value where there is not. Reading the previous
            // computed value alone is what makes an interrupted fade jump.
            var key = (element.Index, property);
            var displayed = running.TryGetValue(key, out var current)
                ? current.ValueAt(now)
                : before.TryGet(property, out var previous) ? parser.Parse(previous) : StyleValue.Unknown;

            var destination = parser.Parse(target);

            if (displayed.Kind == StyleValueKind.Unknown || displayed == destination) {
                running.Remove(key);
                continue;
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

    /// <summary>Starts or stops the element's keyframe animations.</summary>
    /// <remarks>
    ///     An animation whose name and parameters are unchanged keeps its start time and therefore
    ///     its place in the cycle. Restarting it whenever the style is re-resolved would make a
    ///     spinner stutter every time anything else on the element changed — which, with invalidation
    ///     working properly, is exactly when it would be least expected.
    /// </remarks>
    void StartAnimations(StyleNodeId element, ComputedStyle style, float now) {
        if (!ReadAnimations(style)) {
            animations.Remove(element.Index);
            return;
        }

        // One animation per element for now; `animation-name: a, b` is legal CSS and running several
        // at once needs a per-element list rather than a slot. Recorded rather than silently
        // truncated.
        var spec = animationSpecs[0];

        if (animations.TryGetValue(element.Index, out var current) && current.Spec == spec) {
            return;
        }

        animations[element.Index] = (spec, now);
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
    public bool TryGetAnimated(StyleNodeId element, int property, float now, out StyleValue value) {
        value = StyleValue.Unknown;

        if (!animations.TryGetValue(element.Index, out var entry)
            || !entry.Spec.TryOffsetAt(now - entry.StartedAt, out var offset)
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

        return overlaid is null ? style : ComputedStyle.Create(overlaid, style.Parent);
    }

    /// <summary>Forgets everything, as a stylesheet reload must.</summary>
    public void Clear() {
        running.Clear();
        animations.Clear();
    }

    /// <summary>Reads whichever form of <c>animation</c> the stylesheet produced.</summary>
    /// <returns>Whether the element animates anything.</returns>
    /// <remarks>
    ///     ExCSS expands the <c>animation</c> shorthand into longhands and invents defaults for the
    ///     parts that were left out, so unlike <c>transition</c> this can read the longhands — with
    ///     the same caveat, which is that a <c>spring()</c> in the timing function would stop it
    ///     doing so. The shorthand fallback is the same code path either way.
    /// </remarks>
    bool ReadAnimations(ComputedStyle style) {
        animationSpecs.Clear();

        if (!style.TryGet(animationName, out var named)) {
            return false;
        }

        var name = values.NameOf(named);
        if (name.Length == 0 || string.Equals(name, "none", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var duration = style.TryGet(animationDuration, out var d)
            && TransitionParser.TryDuration(values.NameOf(d), out var seconds)
                ? seconds
                : 0f;

        var delay = style.TryGet(animationDelay, out var l)
            && TransitionParser.TryDuration(values.NameOf(l), out var waited)
                ? waited
                : 0f;

        var timing = style.TryGet(animationTiming, out var t)
            && TransitionParser.TryTimingFunction(values.NameOf(t), out var parsed)
                ? parsed
                : TimingFunction.Ease;

        var iterations = 1f;
        if (style.TryGet(animationIterations, out var i)) {
            var text = values.NameOf(i);
            iterations = string.Equals(text, "infinite", StringComparison.OrdinalIgnoreCase)
                ? float.PositiveInfinity
                : float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var count)
                    ? count
                    : 1f;
        }

        var direction = style.TryGet(animationDirection, out var r)
            ? values.NameOf(r).ToLowerInvariant() switch {
                "reverse" => AnimationDirection.Reverse,
                "alternate" => AnimationDirection.Alternate,
                "alternate-reverse" => AnimationDirection.AlternateReverse,
                _ => AnimationDirection.Normal
            }
            : AnimationDirection.Normal;

        var fill = style.TryGet(animationFill, out var f)
            ? values.NameOf(f).ToLowerInvariant() switch {
                "forwards" => AnimationFill.Forwards,
                "backwards" => AnimationFill.Backwards,
                "both" => AnimationFill.Both,
                _ => AnimationFill.None
            }
            : AnimationFill.None;

        animationSpecs.Add(new AnimationSpec(name, duration, delay, timing, iterations, direction, fill));
        return duration > 0f;
    }

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
