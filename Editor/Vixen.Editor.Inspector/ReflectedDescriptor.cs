// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Reflection;
using Vixen.Editor.Core;

namespace Vixen.Editor.Inspector;

/// <summary>A member the serialization generator described, drawn by the inspector.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Writing through a boxed struct works, and it only works because the generator went
///         out of its way.</b> The obvious setter — <c>((Foo) instance).X = value</c> — unboxes into
///         a temporary and the language will not even compile the assignment; a version that got past
///         the compiler would write to a copy and silently do nothing. <c>TypeDescriptorGenerator</c>
///         emits <c>Unsafe.Unbox&lt;Foo&gt;(instance)</c>, which is a reference into the box itself,
///         so a component read out of a chunk can be edited in place and written back whole.
///     </para>
///     <para>
///         <b>Boxing is the point rather than a cost here.</b> This is the tooling path — one
///         allocation per property read, against a panel somebody is looking at — which is the trade
///         <c>Vixen.Core.Reflection</c>'s own remarks name. Frame code touches the field.
///     </para>
/// </remarks>
public sealed class ReflectedMember : InspectorMember {
    readonly MemberDescriptor member;
    readonly Type owner;

    /// <inheritdoc />
    public override Type MemberType => member.MemberType;

    /// <inheritdoc />
    public override Type OwnerType => owner;

    /// <inheritdoc />
    public override bool CanWrite => member.CanWrite;

    /// <summary>Describes a member from its serialization descriptor.</summary>
    /// <param name="owner">The type it belongs to.</param>
    /// <param name="member">What the generator recorded about it.</param>
    public ReflectedMember(Type owner, MemberDescriptor member)
        : base(Named(member), Presented(member)) {
        ArgumentNullException.ThrowIfNull(owner);

        this.owner = owner;
        this.member = member;

        var presentation = member.Presentation;

        Tooltip = presentation.Tooltip;
        Header = presentation.Category;
        Order = member.Order;
        IsReadOnly = presentation.IsEditorReadOnly;

        // ⚠ Both ends or neither. `InspectorRange` is what turns a number into a slider, and a
        // slider with one end unstated would run from whatever the other end is to zero.
        if (presentation.Minimum is { } low && presentation.Maximum is { } high) {
            Range = new InspectorRange(low, high, presentation.Step, presentation.Logarithmic);
        }
    }

    /// <inheritdoc />
    public override object? GetBoxed(object owner) => member.GetValue(owner);

    /// <inheritdoc />
    public override void SetBoxed(object owner, object? value) => member.SetValue(owner, value);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Undoes what it did to the box, and nothing beyond it.</b> A boxed component is a copy
    ///     of what is in a chunk, so putting a member back does not put the entity back — the caller
    ///     that unboxed it is the one that has to write it home, and it is the one that knows how.
    ///     Every consumer in the editor binds these fields with no document for exactly that reason
    ///     and records one command over the whole component; this is here so that a caller who does
    ///     bind one gets something that works rather than an exception.
    /// </remarks>
    public override IEditorCommand CreateSetCommand(
        IReadOnlyList<object> targets,
        object? value,
        EditorDocument? document
    ) {
        ArgumentNullException.ThrowIfNull(targets);

        var descriptor = member;
        var boxes = targets.ToArray();
        var previous = boxes.Select(descriptor.GetValue).ToArray();

        return new DelegateCommand(
            "Set " + DisplayName,
            _ => {
                foreach (var box in boxes) {
                    descriptor.SetValue(box, value);
                }
            },
            _ => {
                for (var index = 0; index < boxes.Length; index++) {
                    descriptor.SetValue(boxes[index], previous[index]);
                }
            }
        );
    }

    static string Named(MemberDescriptor member) {
        ArgumentNullException.ThrowIfNull(member);

        return member.Name;
    }

    static string? Presented(MemberDescriptor member) => member.Presentation.DisplayName;
}

/// <summary>Turns what the serialization generator knows about a type into inspector rows.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The gap this closes is that no runtime type carries <c>[Inspector]</c>, and none
///         should.</b> A component in <c>Vixen.Engine</c> annotated for the editor would be a runtime
///         assembly referencing an editor one — the layering doc 11 sets out exists precisely to stop
///         that. But every <c>[DataContract]</c> type already has a full member description, with
///         boxed accessors, categories, tooltips and ranges, generated for serialization. It is the
///         same information under a different name.
///     </para>
///     <para>
///         So an inspector that can read one draws a game's components with nothing asked of the
///         game, which is the difference between a component panel that works for
///         <c>Camera</c> and one that works for whatever somebody wrote this morning.
///     </para>
///     <para>
///         ⚠ <b>An <c>[Inspector]</c> descriptor still wins where there is one.</b> It carries what a
///         serializer has no reason to know — conditions, asset-picker types, multiline hints, header
///         grouping the author chose for a panel rather than for a file — and a type with both is a
///         type whose author said something specific about how it should be edited.
///     </para>
/// </remarks>
public static class ReflectedDescriptor {
    static readonly ConcurrentDictionary<Type, InspectorDescriptor?> Cache = new();

    /// <summary>The descriptor for a type, from whichever generator described it.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The descriptor, or <see langword="null" /> when neither generator saw it.</returns>
    /// <remarks>
    ///     Cached, because a component panel asks this per component per selection change and the
    ///     answer is a compile-time fact.
    /// </remarks>
    public static InspectorDescriptor? For(Type type) {
        ArgumentNullException.ThrowIfNull(type);

        return InspectorRegistry.Find(type) ?? Cache.GetOrAdd(type, static key => Build(key));
    }

    /// <summary>Whether anything can draw rows for a type.</summary>
    /// <param name="type">The type.</param>
    /// <param name="descriptor">Its descriptor.</param>
    /// <returns>Whether one was found.</returns>
    public static bool TryGet(Type type, [NotNullWhen(true)] out InspectorDescriptor? descriptor) {
        descriptor = For(type);

        return descriptor is not null;
    }

    /// <summary>Forgets what has been built. For tests that register descriptors of their own.</summary>
    public static void Clear() => Cache.Clear();

    static InspectorDescriptor? Build(Type type) {
        if (!TypeRegistry.TryGet(type, out var described)) {
            return null;
        }

        List<InspectorMember> members = [];

        foreach (var member in described.Members) {
            // ⚠ What the *serializer* was told to hide stays hidden here too. `IsEditorVisible` is
            // already the annotation for "this is written to a file and is not somebody's business
            // to edit", and having the inspector re-decide would be a second answer to one question.
            if (!member.Presentation.IsEditorVisible) {
                continue;
            }

            members.Add(new ReflectedMember(type, member));
        }

        if (members.Count == 0) {
            return null;
        }

        // ⚠ No factory, so no reset button — and that is honest rather than a gap. A reset needs a
        // fresh instance to read the type's own defaults off, `TypeDescriptor.Create` throws for a
        // type the generator found no constructor for, and offering a reset that sometimes throws
        // while drawing a row is worse than offering none.
        return new InspectorDescriptor(type, members, described.CanCreate ? described.Create : null);
    }
}
