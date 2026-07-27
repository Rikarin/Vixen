// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Vixen.Core.Yaml;

/// <summary>How a type that is not a primitive is written as one scalar, and read back.</summary>
/// <param name="Parse">Turns the text into a value.</param>
/// <param name="Format">Turns the value into text.</param>
/// <param name="Style">How the text should be quoted.</param>
/// <param name="AcceptsNull">
///     Whether <c>null</c> in the document is a legal value rather than the absence of one. True for
///     <see cref="AssetReference" />, whose null is a real reference to nothing.
/// </param>
public sealed record YamlScalarConverter(
    Func<string, object> Parse,
    Func<object, string> Format,
    YamlScalarStyle Style = YamlScalarStyle.Any,
    bool AcceptsNull = false
);

/// <summary>Every type that is one scalar in a document rather than a mapping.</summary>
/// <remarks>
///     <para>
///         A registry rather than a chain of <c>if (type == typeof(…))</c> in the binder, because the
///         set is open: <c>Vector3</c>, <c>Color4</c> and <c>Rectangle</c> all want to be one scalar
///         and none of them can be named from here without <c>Vixen.Core.Yaml</c> depending on the
///         mathematics. An assembly registers its own on the way past.
///     </para>
///     <para>
///         Delegates rather than a generic interface, deliberately. <c>IYamlScalar&lt;T&gt;</c> would
///         read better and would need <c>MakeGenericMethod</c> to invoke for a type only known at run
///         time, which is the one thing NativeAOT cannot do — see <c>CollectionFactory</c> for the
///         same argument reached from the other direction.
///     </para>
/// </remarks>
public static class YamlScalarConverters {
    static readonly ConcurrentDictionary<Type, YamlScalarConverter> Converters = new();

    static YamlScalarConverters() {
        Register(
            typeof(Guid),
            // 32 lowercase hex with no dashes, which is doc 08's GUID form everywhere it appears.
            text => Guid.Parse(text, CultureInfo.InvariantCulture),
            value => ((Guid)value).ToString("N", CultureInfo.InvariantCulture)
        );

        Register(
            typeof(AssetId),
            text => AssetId.Parse(text, CultureInfo.InvariantCulture),
            value => ((AssetId)value).ToString()
        );

        Register(
            typeof(SubAssetId),
            text => SubAssetId.Parse(text, CultureInfo.InvariantCulture),
            value => ((SubAssetId)value).ToString()
        );

        Register(
            typeof(AssetReference),
            text => AssetReference.Parse(text, CultureInfo.InvariantCulture),
            value => ((AssetReference)value).ToString(),
            // Plain, because a reference is either 'vx:…' or the YAML null, and neither wants quotes.
            YamlScalarStyle.Plain,
            acceptsNull: true
        );

        Register(
            typeof(TimeSpan),
            text => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
            value => ((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture)
        );

        Register(
            typeof(DateTimeOffset),
            text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            value => ((DateTimeOffset)value).ToString("O", CultureInfo.InvariantCulture)
        );

        Register(
            typeof(DateTime),
            text => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            value => ((DateTime)value).ToString("O", CultureInfo.InvariantCulture)
        );

        Register(typeof(Uri), text => new Uri(text, UriKind.RelativeOrAbsolute), value => value.ToString()!);
        Register(typeof(Version), Version.Parse, value => value.ToString()!);
    }

    /// <summary>Records how a type is written as one scalar.</summary>
    /// <param name="type">The type.</param>
    /// <param name="parse">Turns the text into a value.</param>
    /// <param name="format">Turns the value into text.</param>
    /// <param name="style">How the text should be quoted.</param>
    /// <param name="acceptsNull">Whether <c>null</c> in the document is a legal value for it.</param>
    public static void Register(
        Type type,
        Func<string, object> parse,
        Func<object, string> format,
        YamlScalarStyle style = YamlScalarStyle.Any,
        bool acceptsNull = false
    ) {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(format);
        Converters[type] = new(parse, format, style, acceptsNull);
    }

    /// <summary>Looks up how a type is written.</summary>
    /// <param name="type">The type.</param>
    /// <param name="converter">How it is written.</param>
    /// <returns>Whether it is one scalar at all.</returns>
    public static bool TryGet(Type type, [NotNullWhen(true)] out YamlScalarConverter? converter) {
        ArgumentNullException.ThrowIfNull(type);
        return Converters.TryGetValue(type, out converter);
    }
}
