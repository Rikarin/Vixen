// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using Vixen.Core.Reflection;

namespace Vixen.Core.Yaml;

/// <summary>Binds a <see cref="YamlNode" /> tree to typed objects, and back.</summary>
/// <remarks>
///     <para>
///         <b>Everything goes through the generated type registry.</b> A member is found through a
///         <see cref="TypeDescriptor" />, read and written through the lambdas the reflection
///         generator emitted, and a <c>!TextureImporter</c> tag is resolved by asking
///         <see cref="TypeRegistry" /> what claims that name. Nothing here calls
///         <c>PropertyInfo.GetValue</c>, walks an assembly, or builds a generic method at runtime, so
///         reading a <c>.meta</c> file works on a trimmed NativeAOT build — which is the property
///         Phase 3 exists to establish before the codebase is large enough for it to be expensive.
///     </para>
///     <para>
///         <b>This is not a frame-loop API.</b> Every member read and write boxes, because that is
///         what a descriptor's accessors do. It is for import, tooling and boot-time configuration,
///         where one allocation per property is invisible; runtime asset data goes through the
///         binary serializer instead.
///     </para>
/// </remarks>
public static class YamlSerializer {
    static readonly ConcurrentDictionary<Type, object?> Defaults = new();

    /// <summary>Reads a document into a value.</summary>
    /// <typeparam name="T">What to read it as.</typeparam>
    /// <param name="yaml">The YAML.</param>
    /// <param name="options">How to bind it, or <see langword="null" /> for the defaults.</param>
    /// <returns>The value.</returns>
    public static T Parse<T>(string yaml, YamlSerializerOptions? options = null) =>
        Deserialize<T>(YamlReader.Read(yaml), options);

    /// <summary>Writes a value as a document.</summary>
    /// <typeparam name="T">Its declared type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="options">How to bind it, or <see langword="null" /> for the defaults.</param>
    /// <returns>The YAML, ending in a newline.</returns>
    public static string ToYaml<T>(T value, YamlSerializerOptions? options = null) =>
        YamlWriter.Write(Serialize(value, options));

    /// <summary>Binds a node to a value.</summary>
    /// <typeparam name="T">What to bind it as.</typeparam>
    /// <param name="node">The node.</param>
    /// <param name="options">How to bind it, or <see langword="null" /> for the defaults.</param>
    /// <returns>The value.</returns>
    public static T Deserialize<T>(YamlNode node, YamlSerializerOptions? options = null) =>
        (T)Deserialize(node, typeof(T), options)!;

    /// <summary>Binds a node to a value.</summary>
    /// <param name="node">The node.</param>
    /// <param name="expected">What to bind it as.</param>
    /// <param name="options">How to bind it, or <see langword="null" /> for the defaults.</param>
    /// <returns>The value.</returns>
    /// <exception cref="YamlBindingException">The document does not fit the type.</exception>
    public static object? Deserialize(YamlNode node, Type expected, YamlSerializerOptions? options = null) {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(expected);
        return Bind(node, expected, options ?? YamlSerializerOptions.Default, string.Empty);
    }

    /// <summary>Turns a value into a node.</summary>
    /// <typeparam name="T">Its declared type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="options">How to bind it, or <see langword="null" /> for the defaults.</param>
    /// <returns>The node.</returns>
    public static YamlNode Serialize<T>(T value, YamlSerializerOptions? options = null) =>
        Serialize(value, typeof(T), options);

    /// <summary>Turns a value into a node.</summary>
    /// <param name="value">The value.</param>
    /// <param name="declared">The type it is declared as, which decides whether a tag is needed.</param>
    /// <param name="options">How to bind it, or <see langword="null" /> for the defaults.</param>
    /// <returns>The node.</returns>
    /// <exception cref="YamlBindingException">The value cannot be described.</exception>
    public static YamlNode Serialize(object? value, Type declared, YamlSerializerOptions? options = null) {
        ArgumentNullException.ThrowIfNull(declared);
        return Emit(value, declared, options ?? YamlSerializerOptions.Default, string.Empty);
    }

    static object? Bind(YamlNode node, Type expected, YamlSerializerOptions options, string path) {
        var underlying = Nullable.GetUnderlyingType(expected) ?? expected;
        var nullable = underlying != expected || !expected.IsValueType;

        if (IsNull(node)) {
            // A few types have a null of their own — AssetReference's is a real reference to
            // nothing, not the absence of a reference — so the converter gets asked before the
            // document's null is treated as C#'s.
            if (!nullable && YamlScalarConverters.TryGet(underlying, out var nullConverter) && nullConverter.AcceptsNull) {
                return nullConverter.Parse("null");
            }

            return nullable
                ? null
                : throw new YamlBindingException(path, $"null cannot be read as {underlying.Name}.");
        }

        var target = ResolveTag(node, underlying, path);

        return node switch {
            YamlScalar scalar => BindScalar(scalar.Value, target, path),
            YamlSequence sequence => BindSequence(sequence, target, options, path),
            YamlMapping mapping when IsDictionary(target, out var valueType) =>
                BindDictionary(mapping, target, valueType, options, path),
            YamlMapping mapping => BindContract(mapping, target, options, path),
            _ => throw new YamlBindingException(path, $"Cannot read {node.GetType().Name} as {target.Name}.")
        };
    }

    /// <summary>
    ///     Decides what a node actually is, given what was expected and what tag it carries.
    /// </summary>
    /// <remarks>
    ///     This is the whole of polymorphism: <c>importer: !TextureImporter</c> is declared as
    ///     <c>IImportSettings</c> and read as whatever claims that name. A tag that names nothing is
    ///     an importer the running build does not have — a plugin that was removed — and the message
    ///     says so, because the asset database's answer is to quarantine the asset rather than to
    ///     refuse to open the project.
    /// </remarks>
    static Type ResolveTag(YamlNode node, Type expected, string path) {
        if (node.Tag is not { } tag) {
            return expected.IsAbstract && !expected.IsSealed && !IsCollection(expected)
                ? throw new YamlBindingException(
                    path,
                    $"{expected.Name} is abstract, so the node needs a type tag saying which one it is — "
                    + "'!TextureImporter', for instance."
                )
                : expected;
        }

        if (!TypeRegistry.TryGetByAlias(tag, out var descriptor)) {
            throw new YamlBindingException(
                path,
                $"Nothing in this build claims the name '{tag}'. Either the type is gone, or the assembly "
                + "declaring it is not referenced."
            );
        }

        return expected.IsAssignableFrom(descriptor.Type)
            ? descriptor.Type
            : throw new YamlBindingException(path, $"'{tag}' is {descriptor.Type.Name}, which is not a {expected.Name}.");
    }

    static object BindScalar(string text, Type type, string path) {
        try {
            if (type == typeof(string)) {
                return text;
            }

            if (type.IsEnum) {
                return Enum.Parse(type, text, ignoreCase: true);
            }

            return Type.GetTypeCode(type) switch {
                TypeCode.Boolean => ParseBoolean(text, path),
                TypeCode.SByte => sbyte.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.Byte => byte.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.Int16 => short.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.UInt16 => ushort.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.Int32 => int.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.UInt32 => uint.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.Int64 => long.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.UInt64 => ulong.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.Single => float.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.Double => double.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.Decimal => decimal.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.Char => text.Length == 1
                    ? text[0]
                    : throw new YamlBindingException(path, $"'{text}' is not a single character."),
                _ => BindOtherScalar(text, type, path)
            };
        } catch (Exception failure) when (failure is FormatException or OverflowException or ArgumentException) {
            throw new YamlBindingException(path, $"'{text}' is not a {type.Name}.", failure);
        }
    }

    static object BindOtherScalar(string text, Type type, string path) =>
        YamlScalarConverters.TryGet(type, out var converter)
            ? converter.Parse(text)
            : throw new YamlBindingException(
                path,
                $"There is no way to read a scalar as {type.Name}. Register one with "
                + "YamlScalarConverters.Register if it should be one."
            );

    static bool ParseBoolean(string text, string path) =>
        text switch {
            "true" or "True" or "TRUE" or "yes" or "on" => true,
            "false" or "False" or "FALSE" or "no" or "off" => false,
            _ => throw new YamlBindingException(path, $"'{text}' is not true or false.")
        };

    static object BindSequence(YamlSequence sequence, Type type, YamlSerializerOptions options, string path) {
        var element = ElementTypeOf(type, path);
        var created = Make(type, sequence.Count, path);

        // An array is already the right length and cannot grow; a List<T> is empty with the capacity
        // reserved. Both are ILists, and that is the only difference between them here.
        if (created is Array array) {
            for (var index = 0; index < sequence.Count; index++) {
                array.SetValue(Bind(sequence[index], element, options, $"{path}[{index}]"), index);
            }

            return array;
        }

        var list = (IList)created;

        for (var index = 0; index < sequence.Count; index++) {
            list.Add(Bind(sequence[index], element, options, $"{path}[{index}]"));
        }

        return list;
    }

    static object BindDictionary(
        YamlMapping mapping,
        Type type,
        Type valueType,
        YamlSerializerOptions options,
        string path
    ) {
        var dictionary = (IDictionary)Make(type, mapping.Count, path);

        foreach (var (key, value) in mapping.Entries) {
            dictionary.Add(key, Bind(value, valueType, options, Join(path, key)));
        }

        return dictionary;
    }

    /// <summary>Makes a collection, using the constructor the reflection generator wrote for it.</summary>
    /// <remarks>
    ///     Never <c>Array.CreateInstance</c> or <c>Activator.CreateInstance</c>: both are
    ///     <c>RequiresDynamicCode</c>, and a binder that used them would work on a desktop and throw
    ///     on a phone. See <see cref="CollectionFactory" /> for why that is the whole reason it
    ///     exists.
    /// </remarks>
    static object Make(Type type, int capacity, string path) =>
        CollectionFactory.TryCreate(type, capacity, out var created)
            ? created
            : throw new YamlBindingException(
                path,
                $"Nothing registered a way to make a {type.Name}. A collection type is registered by the "
                + "reflection generator when it appears on a described member, so either the declaring "
                + "assembly does not run that generator, or this type was reached some other way."
            );

    static object BindContract(YamlMapping mapping, Type type, YamlSerializerOptions options, string path) {
        if (!TypeRegistry.TryGet(type, out var descriptor)) {
            throw new YamlBindingException(
                path,
                $"{type.Name} has no descriptor. Give it [DataContract] so the generator describes it; a type "
                + "the registry does not know cannot be read on a trimmed build either."
            );
        }

        if (!descriptor.CanCreate) {
            throw new YamlBindingException(
                path,
                $"{type.Name} cannot be constructed without arguments, so there is nothing to read into. "
                + "Give it a parameterless constructor; init-only members are set afterwards."
            );
        }

        var instance = descriptor.Create();

        foreach (var (key, value) in mapping.Entries) {
            var member = FindMember(descriptor, key, options);

            if (member is null) {
                options.OnUnknownKey?.Invoke(Join(path, key));
                continue;
            }

            if (!member.CanWrite) {
                options.OnUnknownKey?.Invoke(Join(path, key));
                continue;
            }

            member.SetValue(instance, Bind(value, member.MemberType, options, Join(path, key)));
        }

        return instance;
    }

    /// <summary>
    ///     Finds the member a key names: the naming policy's answer first, then a case-insensitive
    ///     match.
    /// </summary>
    /// <remarks>
    ///     Strict on write, lenient on read. These files are hand-edited, and someone who typed
    ///     <c>MaxSize</c> meant <c>maxSize</c>; refusing would be pedantry, and the next write puts
    ///     it back in the canonical form anyway.
    /// </remarks>
    static MemberDescriptor? FindMember(TypeDescriptor descriptor, string key, YamlSerializerOptions options) {
        foreach (var member in descriptor.Members) {
            if (string.Equals(options.KeyFor(member.Name), key, StringComparison.Ordinal)) {
                return member;
            }
        }

        foreach (var member in descriptor.Members) {
            if (string.Equals(member.Name, key, StringComparison.OrdinalIgnoreCase)) {
                return member;
            }
        }

        return null;
    }

    static YamlNode Emit(object? value, Type declared, YamlSerializerOptions options, string path) {
        if (value is null) {
            return new YamlScalar("null", YamlScalarStyle.Plain);
        }

        var runtime = value.GetType();
        var node = EmitValue(value, runtime, options, path);

        // A tag only when the reader could not have worked it out: the declared type already says
        // what to expect, and tagging every node would make a .meta file unreadable.
        if (runtime != (Nullable.GetUnderlyingType(declared) ?? declared)
            && TypeRegistry.TryGet(runtime, out var descriptor)) {
            node.Tag = descriptor.Alias;
        }

        return node;
    }

    static YamlNode EmitValue(object value, Type runtime, YamlSerializerOptions options, string path) {
        if (value is string text) {
            return new YamlScalar(text);
        }

        if (runtime.IsEnum) {
            return new YamlScalar(value.ToString()!, YamlScalarStyle.Plain);
        }

        if (YamlScalarConverters.TryGet(runtime, out var converter)) {
            return new YamlScalar(converter.Format(value), converter.Style);
        }

        switch (value) {
            case bool flag:
                return new YamlScalar(flag ? "true" : "false", YamlScalarStyle.Plain);

            case IFormattable formattable when runtime.IsPrimitive || runtime == typeof(decimal):
                return new YamlScalar(formattable.ToString(null, CultureInfo.InvariantCulture), YamlScalarStyle.Plain);

            case IDictionary dictionary:
                return EmitDictionary(dictionary, runtime, options, path);

            case IEnumerable items:
                return EmitSequence(items, runtime, options, path);

            default:
                return EmitContract(value, runtime, options, path);
        }
    }

    static YamlMapping EmitDictionary(IDictionary dictionary, Type runtime, YamlSerializerOptions options, string path) {
        var valueType = runtime.IsGenericType ? runtime.GetGenericArguments()[^1] : typeof(object);
        var mapping = new YamlMapping();

        foreach (DictionaryEntry entry in dictionary) {
            var key = entry.Key.ToString() ?? string.Empty;
            mapping.Set(key, Emit(entry.Value, valueType, options, Join(path, key)));
        }

        return mapping;
    }

    static YamlSequence EmitSequence(IEnumerable items, Type runtime, YamlSerializerOptions options, string path) {
        var element = runtime.IsArray
            ? runtime.GetElementType()!
            : runtime.IsGenericType
                ? runtime.GetGenericArguments()[0]
                : typeof(object);

        var sequence = new YamlSequence();
        var index = 0;

        foreach (var item in items) {
            sequence.Add(Emit(item, element, options, $"{path}[{index++}]"));
        }

        return sequence;
    }

    static YamlMapping EmitContract(object value, Type runtime, YamlSerializerOptions options, string path) {
        if (!TypeRegistry.TryGet(runtime, out var descriptor)) {
            throw new YamlBindingException(
                path,
                $"{runtime.Name} has no descriptor. Give it [DataContract] so the generator describes it."
            );
        }

        var mapping = new YamlMapping();
        var defaults = options.OmitDefaults ? DefaultOf(descriptor) : null;

        foreach (var member in descriptor.Members) {
            if (!member.CanWrite) {
                // Nothing can read it back, so writing it would be a key that vanishes on the next
                // load — a diff that appears and disappears with no edit behind it.
                continue;
            }

            var member1 = member.GetValue(value);

            if (defaults is not null && Equals(member1, member.GetValue(defaults))) {
                continue;
            }

            var key = options.KeyFor(member.Name);
            mapping.Set(key, Emit(member1, member.MemberType, options, Join(path, key)));
        }

        return mapping;
    }

    static object? DefaultOf(TypeDescriptor descriptor) =>
        Defaults.GetOrAdd(descriptor.Type, _ => descriptor.CanCreate ? descriptor.Create() : null);

    /// <summary>What a sequence's items are.</summary>
    /// <remarks>
    ///     <see cref="System.Collections.Immutable.ImmutableArray{T}" /> is refused by name rather
    ///     than failing obscurely. Building one for a <c>T</c> only known at run time needs
    ///     <c>MakeGenericMethod</c>, which is exactly the thing NativeAOT cannot do — so a settings
    ///     record that used it would bind on the desktop and throw on a phone, which is the failure
    ///     mode this whole phase is scheduled early to prevent. An init-only <c>T[]</c> is as
    ///     immutable as the record around it.
    /// </remarks>
    static Type ElementTypeOf(Type type, string path) {
        if (type.IsArray) {
            return type.GetElementType()!;
        }

        if (type.IsGenericType) {
            var definition = type.GetGenericTypeDefinition();

            if (definition == typeof(System.Collections.Immutable.ImmutableArray<>)) {
                throw new YamlBindingException(
                    path,
                    "ImmutableArray<T> cannot be built without MakeGenericMethod, which NativeAOT does not "
                    + "have. Declare the member as T[] — in an init-only record it is just as immutable."
                );
            }

            if (definition == typeof(List<>)
                || definition == typeof(IList<>)
                || definition == typeof(ICollection<>)
                || definition == typeof(IEnumerable<>)
                || definition == typeof(IReadOnlyList<>)
                || definition == typeof(IReadOnlyCollection<>)) {
                return type.GetGenericArguments()[0];
            }
        }

        throw new YamlBindingException(path, $"There is no way to read a sequence as {type.Name}.");
    }

    static bool IsDictionary(Type type, out Type valueType) {
        if (type.IsGenericType) {
            var definition = type.GetGenericTypeDefinition();

            if (definition == typeof(Dictionary<,>)
                || definition == typeof(IDictionary<,>)
                || definition == typeof(IReadOnlyDictionary<,>)) {
                var arguments = type.GetGenericArguments();

                if (arguments[0] == typeof(string)) {
                    valueType = arguments[1];
                    return true;
                }
            }
        }

        valueType = typeof(object);
        return false;
    }

    static bool IsCollection(Type type) =>
        type.IsArray || (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type));

    static bool IsNull(YamlNode node) =>
        node is YamlScalar { Style: YamlScalarStyle.Plain or YamlScalarStyle.Any } scalar
        && scalar.Value is "" or "null" or "Null" or "NULL" or "~";

    static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";
}
