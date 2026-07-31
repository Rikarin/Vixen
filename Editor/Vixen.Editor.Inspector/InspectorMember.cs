// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Inspector;

/// <summary>Reads a member of <typeparamref name="TOwner" /> by reference.</summary>
/// <typeparam name="TOwner">What the member belongs to.</typeparam>
/// <typeparam name="TValue">What it holds.</typeparam>
/// <param name="owner">The object.</param>
/// <returns>A reference to the member itself, not a copy of it.</returns>
/// <remarks>
///     What makes a <c>struct</c> member editable without boxing: the generator emits
///     <c>static (Foo o) =&gt; ref o.Tint</c>, so a drawer writing one channel of a colour writes the
///     colour that is on the object rather than a box that nothing holds. This is the difference
///     between this assembly and <c>Vixen.Core.Reflection</c>'s descriptors, whose accessors pass
///     values as <see cref="object" /> and therefore cannot.
/// </remarks>
public delegate ref TValue MemberReference<in TOwner, TValue>(TOwner owner);

/// <summary>The bounds a numeric member is edited within.</summary>
/// <param name="Minimum">The low end.</param>
/// <param name="Maximum">The high end.</param>
/// <param name="Step">How far one nudge moves it, or zero for continuous.</param>
/// <param name="Logarithmic">Whether the slider's travel is logarithmic.</param>
public readonly record struct InspectorRange(double Minimum, double Maximum, double Step, bool Logarithmic);

/// <summary>How a colour member is edited.</summary>
/// <param name="Hdr">Whether values above one are meaningful.</param>
/// <param name="ShowAlpha">Whether the alpha channel is shown.</param>
public readonly record struct ColorUsage(bool Hdr, bool ShowAlpha);

/// <summary>How a curve member is edited.</summary>
/// <param name="Minimum">The lowest value the vertical axis shows.</param>
/// <param name="Maximum">The highest.</param>
public readonly record struct CurveUsage(float Minimum, float Maximum);

/// <summary>What an inspector needs to draw a row, without knowing what type the row holds.</summary>
/// <remarks>
///     <para>
///         <b>Everything here was decided by a generator reading the source</b>, so a member's
///         attributes are facts about the build rather than a reflection pass over whatever
///         assemblies happened to load. That is the same bet <c>Vixen.Core.Reflection</c> makes; what
///         this adds is the inspector's own vocabulary — headings, conditions, asset pickers, curves
///         — which do not belong in a serialisation-facing descriptor.
///     </para>
///     <para>
///         The boxed accessors below are the un-generic path, taken by the search box, the
///         copy-property command and any drawer that does not care what it is editing.
///         <see cref="InspectorMember{TOwner,TValue}" /> is the typed one.
///     </para>
/// </remarks>
public abstract class InspectorMember {
    /// <summary>The member's name in source, which is what a condition names it by.</summary>
    public string Name { get; }

    /// <summary>What the row is labelled.</summary>
    public string DisplayName { get; }

    /// <summary>What it holds.</summary>
    public abstract Type MemberType { get; }

    /// <summary>What it belongs to.</summary>
    public abstract Type OwnerType { get; }

    /// <summary>The section this member starts, or <see langword="null" /> if it starts none.</summary>
    public string? Header { get; init; }

    /// <summary>What hovering the row says.</summary>
    public string? Tooltip { get; init; }

    /// <summary>The bounds it is edited within, if it has any.</summary>
    public InspectorRange? Range { get; init; }

    /// <summary>How it is edited if it is a colour.</summary>
    public ColorUsage? Color { get; init; }

    /// <summary>How it is edited if it is a curve.</summary>
    public CurveUsage? Curve { get; init; }

    /// <summary>What kind of asset it picks, if it picks one.</summary>
    public Type? AssetType { get; init; }

    /// <summary>Whether an empty asset reference is allowed.</summary>
    public bool AllowNull { get; init; } = true;

    /// <summary>How many lines a string member is edited over, or zero for one.</summary>
    public int Lines { get; init; }

    /// <summary>The <c>bool</c> member that decides whether this one is shown, if any.</summary>
    public string? Condition { get; init; }

    /// <summary>Whether the condition is inverted — <c>[HideIf]</c> rather than <c>[ShowIf]</c>.</summary>
    public bool ConditionNegated { get; init; }

    /// <summary>Whether the inspector refuses to write it.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>Whether consecutive edits to it collapse into one undo step.</summary>
    /// <remarks>
    ///     On for anything with a range, because a bounded number is a slider and a slider drag is
    ///     one edit. Off otherwise, matching <c>EditorProperty</c>'s reasoning: two edits to a
    ///     dropdown are two decisions and collapsing them takes away an undo the user is entitled to.
    /// </remarks>
    public bool CoalescesEdits => Range is not null || Color is not null;

    /// <summary>Where it sits among its type's members.</summary>
    public int Order { get; init; }

    /// <summary>The attribute types found on it, in the order they were declared.</summary>
    /// <remarks>
    ///     What <see cref="DrawerRegistry.ForAttribute" /> matches against. The attributes themselves
    ///     are not kept — their <i>content</i> is already above, and holding instances would mean
    ///     constructing every attribute on every described type at module-initialisation time.
    /// </remarks>
    public IReadOnlyList<Type> Attributes { get; init; } = [];

    /// <summary>Reads it, boxing a value type.</summary>
    /// <param name="owner">What to read it from.</param>
    /// <returns>Its value.</returns>
    public abstract object? GetBoxed(object owner);

    /// <summary>Writes it, unboxing a value type.</summary>
    /// <param name="owner">What to write it on.</param>
    /// <param name="value">What to write.</param>
    public abstract void SetBoxed(object owner, object? value);

    /// <summary>Whether it can be written at all, condition and read-only flag aside.</summary>
    public abstract bool CanWrite { get; }

    /// <summary>Builds the undoable command that sets this member across a selection.</summary>
    /// <param name="targets">What to set it on.</param>
    /// <param name="value">What to set it to, boxed.</param>
    /// <param name="document">The document the objects belong to, if any.</param>
    /// <returns>The command.</returns>
    /// <remarks>
    ///     Here rather than in the caller because this is the one place that knows both type
    ///     parameters. A caller holding an <see cref="InspectorMember" /> has neither, and working
    ///     them out would mean <c>MakeGenericType</c> — the one thing the whole descriptor layer
    ///     exists to avoid.
    /// </remarks>
    public abstract Core.IEditorCommand CreateSetCommand(
        IReadOnlyList<object> targets,
        object? value,
        Core.EditorDocument? document
    );

    /// <summary>Describes a member.</summary>
    /// <param name="name">Its name in source.</param>
    /// <param name="displayName">What the row is labelled, or <see langword="null" /> for the name.</param>
    protected InspectorMember(string name, string? displayName) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Name = name;
        DisplayName = string.IsNullOrEmpty(displayName) ? Humanise(name) : displayName;
    }

    /// <summary>Renders the member as <c>Name : Type</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => $"{Name} : {MemberType.Name}";

    /// <summary>Turns <c>FoamWidth</c> into <c>Foam Width</c>.</summary>
    /// <param name="name">The member's name.</param>
    /// <returns>The label.</returns>
    /// <remarks>
    ///     ⚠ <b>Forwards to <see cref="EditorNames.Humanise" />, which is where the rule now lives.</b>
    ///     A component's foldout wants the same one and is built in <c>Vixen.Editor.SceneView</c>,
    ///     which cannot see this assembly — so the implementation moved down to the layer both
    ///     reference. Kept here because it is what every caller and every test already names, and
    ///     because a member label is still the case it was written for.
    /// </remarks>
    public static string Humanise(string name) => Core.EditorNames.Humanise(name);
}

/// <summary>One member, with the accessors that reach it without boxing.</summary>
/// <typeparam name="TOwner">What it belongs to.</typeparam>
/// <typeparam name="TValue">What it holds.</typeparam>
/// <remarks>
///     <para>
///         Constructed one of two ways. A <i>field</i> gets a <see cref="MemberReference{TOwner,TValue}" />
///         and can be edited in place, which is what a <c>Vector3</c> member needs. A <i>property</i>
///         gets a getter and a setter, because there is no reference to take — and a drawer that
///         wants to change one channel of a property-held colour therefore reads, modifies and writes
///         the whole value, which is correct and merely less direct.
///     </para>
///     <para>
///         ⚠ <b>An owner that is not a <typeparamref name="TOwner" /> is a bug in the caller</b>, and
///         it throws rather than returning null. The descriptor was looked up by the object's own
///         type; handing it a different object means two selections got crossed.
///     </para>
/// </remarks>
public sealed class InspectorMember<TOwner, TValue> : InspectorMember where TOwner : class {
    readonly MemberReference<TOwner, TValue>? reference;
    readonly Func<TOwner, TValue>? getter;
    readonly Action<TOwner, TValue>? setter;

    /// <inheritdoc />
    public override Type MemberType => typeof(TValue);

    /// <inheritdoc />
    public override Type OwnerType => typeof(TOwner);

    /// <inheritdoc />
    public override bool CanWrite => reference is not null || setter is not null;

    /// <summary>Describes a field, reachable by reference.</summary>
    /// <param name="name">Its name in source.</param>
    /// <param name="reference">Takes a reference to it.</param>
    /// <param name="displayName">What the row is labelled.</param>
    public InspectorMember(string name, MemberReference<TOwner, TValue> reference, string? displayName = null)
        : base(name, displayName) {
        ArgumentNullException.ThrowIfNull(reference);
        this.reference = reference;
    }

    /// <summary>Describes a property, reachable through accessors.</summary>
    /// <param name="name">Its name in source.</param>
    /// <param name="getter">Reads it.</param>
    /// <param name="setter">Writes it, or <see langword="null" /> if it cannot be written.</param>
    /// <param name="displayName">What the row is labelled.</param>
    public InspectorMember(
        string name,
        Func<TOwner, TValue> getter,
        Action<TOwner, TValue>? setter,
        string? displayName = null
    ) : base(name, displayName) {
        ArgumentNullException.ThrowIfNull(getter);

        this.getter = getter;
        this.setter = setter;
    }

    /// <summary>Reads the member.</summary>
    /// <param name="owner">What to read it from.</param>
    /// <returns>Its value.</returns>
    public TValue Get(TOwner owner) {
        ArgumentNullException.ThrowIfNull(owner);

        return reference is not null ? reference(owner) : getter!(owner);
    }

    /// <summary>Writes the member.</summary>
    /// <param name="owner">What to write it on.</param>
    /// <param name="value">What to write.</param>
    /// <exception cref="InvalidOperationException">The member has no setter.</exception>
    public void Set(TOwner owner, TValue value) {
        ArgumentNullException.ThrowIfNull(owner);

        if (reference is not null) {
            reference(owner) = value;
            return;
        }

        if (setter is null) {
            throw new InvalidOperationException($"'{Name}' cannot be written.");
        }

        setter(owner, value);
    }

    /// <inheritdoc />
    public override object? GetBoxed(object owner) => Get(Cast(owner));

    /// <inheritdoc />
    public override void SetBoxed(object owner, object? value) => Set(Cast(owner), (TValue) value!);

    /// <inheritdoc />
    public override Core.IEditorCommand CreateSetCommand(
        IReadOnlyList<object> targets,
        object? value,
        Core.EditorDocument? document
    ) {
        ArgumentNullException.ThrowIfNull(targets);

        var owners = new TOwner[targets.Count];
        var previous = new TValue[targets.Count];

        for (var index = 0; index < targets.Count; index++) {
            owners[index] = Cast(targets[index]);
            previous[index] = Get(owners[index]);
        }

        return new SetMembersCommand<TOwner, TValue>(this, owners, previous, (TValue) value!, document);
    }

    static TOwner Cast(object owner) {
        ArgumentNullException.ThrowIfNull(owner);

        return owner as TOwner
            ?? throw new ArgumentException(
                $"A member of '{typeof(TOwner)}' was handed a '{owner.GetType()}'. The descriptor is "
                + "looked up by the inspected object's own type, so this means two selections crossed.",
                nameof(owner)
            );
    }
}
