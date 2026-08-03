// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core;
using Vixen.Core.Reflection;

namespace Vixen.Editor.Scripts;

/// <summary>A <see cref="TypeDescriptor" /> built by reflection, for a type no generator saw.</summary>
/// <remarks>
///     <para>
///         <b>What lets a project's <c>Editor/</c> folder declare an asset importer.</b> An importer
///         is *named* by its settings type's <c>[DataContract]</c> alias, which
///         <c>TypeRegistry</c> answers and <c>Vixen.Core.Reflection.Generator</c> normally writes —
///         and an editor script is compiled with no generator driver. Everything else follows from
///         the descriptor: <c>YamlSerializer</c> reads and writes a <c>.meta</c>'s settings through
///         <c>TypeRegistry</c>, and <c>ArtifactKey</c> hashes them as the YAML it emitted. One
///         registration, and the rest of the pipeline never knows the difference.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in <c>Vixen.Core.Reflection</c>, and that is the whole point of the
///         file.</b> The engine is published NativeAOT and ADR-002 is why the generator exists at
///         all; the editor is not, which is what makes this permissible — but only in the editor. A
///         reflection describer sitting in Core would be one a runtime could reach, and the first
///         person to reach it would get a trimmed publish that works on their machine and not in a
///         build.
///     </para>
///     <para>
///         ⚠ <b>It must agree with the generated one, member for member.</b> A settings type moved
///         from a script into a plugin would otherwise change what its <c>.meta</c> says — the same
///         file read as a different set of values, which is a content bug with no error. The rules
///         below are the generator's, and a test builds both for one type and compares them.
///     </para>
///     <para>
///         ⚠ <b>Narrow on purpose: it describes what an importer's settings need and nothing
///         else.</b> Describing every <c>[DataContract]</c> a script declares would put components
///         one short step away, and a component that exists only when the editor compiled a script is
///         a scene a game build cannot load.
///     </para>
/// </remarks>
public static class ReflectedTypes {
    /// <summary>Describes a type and everything its members need, and registers the lot.</summary>
    /// <param name="type">The type — an importer's settings.</param>
    /// <returns>How many descriptors were registered.</returns>
    /// <exception cref="InvalidOperationException">It has no <c>[DataContract]</c>, or cannot be made.</exception>
    /// <remarks>
    ///     ⚠ <b>Transitive, because a settings type with a nested one is a settings type whose YAML
    ///     cannot be written.</b> <c>YamlSerializer</c> resolves each member's runtime type through
    ///     <c>TypeRegistry</c> in turn, so describing only the outer type produces a file missing
    ///     everything below the first level — silently, because an unknown type writes nothing rather
    ///     than failing.
    /// </remarks>
    public static int Register(Type type) {
        ArgumentNullException.ThrowIfNull(type);

        var registered = 0;

        Walk(type, [], ref registered);
        return registered;
    }

    /// <summary>Describes one type without registering it or anything it references.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The descriptor.</returns>
    /// <exception cref="InvalidOperationException">It has no <c>[DataContract]</c>, or cannot be made.</exception>
    /// <remarks>
    ///     ⚠ <b>Public so that a test can hold a reflected descriptor and a generated one for the same
    ///     type at once and compare them.</b> That comparison is the only thing standing between this
    ///     and a settings type whose <c>.meta</c> means something different depending on which tier
    ///     compiled it — and it cannot be written against <see cref="Register" />, which skips a type
    ///     the generator has already described.
    /// </remarks>
    public static TypeDescriptor Describe(Type type) {
        ArgumentNullException.ThrowIfNull(type);

        var ignored = 0;

        return Build(type, [], ref ignored);
    }

    static void Walk(Type type, HashSet<Type> seen, ref int registered) {
        if (!seen.Add(type) || TypeRegistry.TryGet(type, out _)) {
            return;
        }

        TypeRegistry.Register(Build(type, seen, ref registered));
        registered++;
    }

    static TypeDescriptor Build(Type type, HashSet<Type> seen, ref int registered) {
        if (type.GetCustomAttribute<DataContractAttribute>() is not { } contract) {
            throw new InvalidOperationException(
                $"'{type.Name}' has no [DataContract], so nothing could name it in a file. An importer's "
                + "settings carry one, and its alias is the tag a .meta file writes."
            );
        }

        // ⚠ A parameterless constructor, because the deserializer makes one and fills it in. A
        // positional record has none, and supporting it means matching primary-constructor parameters
        // to members by name — a second set of rules to keep in step with the generator.
        //
        // ⚠ Unreachable for an importer's *own* settings, and kept anyway. `AssetImporter<TSettings>`
        // constrains to `new()`, so the C# compiler refuses a positional one on the line the author
        // wrote — a better message than this. What this catches is a settings type that *contains*
        // one, which nothing constrains, and `Register` being public.
        if (type.GetConstructor(Type.EmptyTypes) is null) {
            throw new InvalidOperationException(
                $"'{type.Name}' has no parameterless constructor. An editor script's settings type has to "
                + "be a class or record with `{ get; init; }` members rather than a positional record, "
                + "because the editor makes one and fills it in."
            );
        }

        List<MemberDescriptor> members = [];
        var order = 0;

        foreach (var member in Serializable(type)) {
            members.Add(Member(member, order++));

            if (Nested(member) is { } nested) {
                Walk(nested, seen, ref registered);
            }
        }

        return new TypeDescriptor(
            type,
            string.IsNullOrEmpty(contract.Alias) ? type.Name : contract.Alias,
            TypeTraits.DataContract | (type.IsAbstract ? TypeTraits.Abstract : TypeTraits.None),
            [.. members],
            () => Activator.CreateInstance(type)!,
            type.GetCustomAttribute<CategoryAttribute>()?.Name
        );
    }

    /// <summary>The members a file carries, in declared order.</summary>
    /// <remarks>
    ///     ⚠ <b>The generator's rule, restated: public fields and read/write properties, unless they
    ///     carry <c>[DataMemberIgnore]</c>.</b> <c>[DataMember]</c> only ever overrides a default —
    ///     an order or a name — so a member without one is still in, and a private member with one is
    ///     still out. Getting this wrong in either direction is a <c>.meta</c> that means something
    ///     different depending on which tier compiled the type.
    /// </remarks>
    static IEnumerable<MemberInfo> Serializable(Type type) {
        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance)) {
            if (member.GetCustomAttribute<DataMemberIgnoreAttribute>() is not null) {
                continue;
            }

            switch (member) {
                case FieldInfo { IsInitOnly: false, IsLiteral: false }:
                case PropertyInfo { CanRead: true, CanWrite: true, GetMethod.IsPublic: true }:
                    yield return member;
                    break;

                default:
                    break;
            }
        }
    }

    static MemberDescriptor Member(MemberInfo member, int order) {
        var (type, get, set, initOnly) = Access(member);
        var declared = member.GetCustomAttribute<DataMemberAttribute>();
        var visible = member.GetCustomAttribute<EditorVisibleAttribute>();
        var range = member.GetCustomAttribute<RangeAttribute>();
        var asset = member.GetCustomAttribute<AssetTypeAttribute>();

        return new MemberDescriptor(
            declared?.Name ?? member.Name,
            type,
            declared is { Order: not 0 } ? declared.Order : order,
            get,
            set,

            // ⚠ Every field written out. `MemberPresentation` is a struct, so a defaulted one has
            // `IsEditorVisible: false` whatever the parameter default says — its own remarks warn
            // that a hand-written descriptor trips over this exactly once, and this is the
            // hand-written descriptor.
            new MemberPresentation(
                Category: member.GetCustomAttribute<CategoryAttribute>()?.Name,
                DisplayName: visible?.DisplayName,
                Tooltip: member.GetCustomAttribute<TooltipAttribute>()?.Text,
                Minimum: range?.Minimum,
                Maximum: range?.Maximum,
                Step: range?.Step ?? 0,
                Logarithmic: range?.Logarithmic ?? false,
                IsEditorVisible: visible?.Visible ?? true,
                IsEditorReadOnly: visible?.ReadOnly ?? false,
                AssetType: asset?.AssetType,
                AllowsNull: asset?.AllowNull ?? true
            ),
            initOnly,
            isSerialized: true
        );
    }

    /// <summary>Reading and writing one member, and whether its setter is <c>init</c>-only.</summary>
    /// <remarks>
    ///     ⚠ <b><c>init</c> is an ordinary setter with a modreq, so reflection can call it.</b> The
    ///     generator reaches one through <c>[UnsafeAccessor]</c> because generated code cannot say
    ///     <c>obj.Member = x</c> for an <c>init</c> outside a constructor; <c>SetValue</c> has no such
    ///     restriction. That matters more than it sounds — most settings records are
    ///     <c>{ get; init; }</c>, and a setter that silently did nothing would be a file that reads
    ///     back as every default.
    /// </remarks>
    static (Type Type, Func<object, object?> Get, Action<object, object?> Set, bool InitOnly) Access(MemberInfo member) {
        if (member is FieldInfo field) {
            return (field.FieldType, field.GetValue, field.SetValue, false);
        }

        var property = (PropertyInfo) member;
        var setter = property.SetMethod;

        var initOnly = setter is not null
            && setter.ReturnParameter
                .GetRequiredCustomModifiers()
                .Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");

        return (property.PropertyType, property.GetValue, property.SetValue, initOnly);
    }

    /// <summary>A member's own <c>[DataContract]</c> type, if it has one that needs describing.</summary>
    /// <remarks>
    ///     ⚠ <b>The element type of a collection, not the collection.</b> A
    ///     <c>List&lt;LayerSettings&gt;</c> needs <c>LayerSettings</c> described; the list itself is
    ///     the serializer's own business.
    /// </remarks>
    static Type? Nested(MemberInfo member) {
        var type = member is FieldInfo field ? field.FieldType : ((PropertyInfo) member).PropertyType;

        if (type.IsGenericType && type.GetGenericArguments() is [var element]) {
            type = element;
        } else if (type.IsArray && type.GetElementType() is { } item) {
            type = item;
        }

        return type.GetCustomAttribute<DataContractAttribute>() is null ? null : type;
    }
}
