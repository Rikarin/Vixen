// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;

namespace Vixen.Editor.Core.Scenes;

/// <summary>How a vector and a rotation read in a scene file.</summary>
/// <remarks>
///     <para>
///         <b>One scalar, not a mapping.</b> A transform written as nested <c>x</c>/<c>y</c>/<c>z</c>
///         keys is fifteen lines per entity and a diff nobody can scan; <c>position: 1 2 3</c> is one
///         line and reads as the thing it is. The same argument doc 08 makes for a reference being a
///         prefixed scalar rather than a three-key flow mapping.
///     </para>
///     <para>
///         ⚠ <b>This is why the format needs converters at all.</b> The YAML binder builds a value
///         from a described type's members, and <c>Vixen.Core.Mathematics</c> carries the
///         serialisation generator but not the reflection one — its types have binary serializers and
///         no <c>TypeDescriptor</c>. Registering a converter is the narrow fix; the wide one is
///         making the mathematics assembly depend on the type registry, which is a cost every
///         shipping game would pay for a decision the editor's file format wanted.
///     </para>
///     <para>
///         ⚠ <b>Round-trip precision, so "R" and not a fixed number of places.</b> A scene saved,
///         reopened and saved again has to produce the same bytes — a format that quietly rounded
///         would make every open-and-close a diff, and every scene a merge conflict with itself.
///     </para>
/// </remarks>
public static class SceneScalars {
    static bool registered;

    /// <summary>Registers them, once.</summary>
    /// <remarks>
    ///     ⚠ <b>Called from <c>SceneSerializer</c>'s static constructor rather than from a module
    ///     initializer.</b> A module initializer would work and would be worse: the converter table
    ///     is process-wide, so registering from it means merely <i>referencing</i> this assembly
    ///     changes how every other YAML document in the process reads a <c>Vector3</c>. Tying it to
    ///     the type that needs it makes the blast radius the scene format, which is what it is.
    /// </remarks>
    public static void Register() {
        if (registered) {
            return;
        }

        registered = true;

        // ⚠ An id is one scalar and not a mapping over its single field. Without this it writes as
        // `id: { value: aa839e10… }`, which is both unreadable and, worse, a different shape from
        // every other identity in the engine — `AssetId` next to it in the same file would be a bare
        // scalar. The binder has no way to guess: a [DataContract] with one member is a mapping
        // unless something says otherwise.
        YamlScalarConverters.Register(
            typeof(EntityId),
            text => EntityId.Parse(text, CultureInfo.InvariantCulture),
            value => ((EntityId) value).ToString(),
            YamlScalarStyle.Plain
        );

        // Plain style throughout: these are numbers separated by spaces and quoting them would make
        // a hand-edited file's quotes meaningful, which is a trap rather than a feature.
        YamlScalarConverters.Register(
            typeof(Vector2),
            text => Read(text, 2) is var n ? new Vector2(n[0], n[1]) : default,
            value => Write(((Vector2) value).X, ((Vector2) value).Y),
            YamlScalarStyle.Plain
        );

        YamlScalarConverters.Register(
            typeof(Vector3),
            text => Read(text, 3) is var n ? new Vector3(n[0], n[1], n[2]) : default,
            value => Write(((Vector3) value).X, ((Vector3) value).Y, ((Vector3) value).Z),
            YamlScalarStyle.Plain
        );

        YamlScalarConverters.Register(
            typeof(Vector4),
            text => Read(text, 4) is var n ? new Vector4(n[0], n[1], n[2], n[3]) : default,
            value => Write(((Vector4) value).X, ((Vector4) value).Y, ((Vector4) value).Z, ((Vector4) value).W),
            YamlScalarStyle.Plain
        );

        // ⚠ Four numbers, not three angles. Euler angles are what a person edits and they are
        // ambiguous — three orders give three rotations from the same numbers — so the *file* keeps
        // the unambiguous form and the inspector does the conversion at the edge. See EulerAngles.
        YamlScalarConverters.Register(
            typeof(Quaternion),
            text => Read(text, 4) is var n ? new Quaternion(n[0], n[1], n[2], n[3]) : default,
            value => Write(
                ((Quaternion) value).X,
                ((Quaternion) value).Y,
                ((Quaternion) value).Z,
                ((Quaternion) value).W
            ),
            YamlScalarStyle.Plain
        );

        YamlScalarConverters.Register(
            typeof(Color4),
            text => Read(text, 4) is var n ? new Color4(n[0], n[1], n[2], n[3]) : default,
            value => Write(((Color4) value).R, ((Color4) value).G, ((Color4) value).B, ((Color4) value).A),
            YamlScalarStyle.Plain
        );

        // ⚠ Registered alongside <see cref="Color4" /> rather than left to the binder, and the
        // symptom of its absence is worth recording: a light's colour came out as "Color3 has no
        // descriptor", thrown from the *serializer* when a scene was saved. Nothing in
        // `Vixen.Core.Mathematics` carries the reflection generator — see the remarks above — so
        // every type of its that a scene file names has to be one of these, and a colour with no
        // alpha is as much one as a colour with one.
        YamlScalarConverters.Register(
            typeof(Color3),
            text => Read(text, 3) is var n ? new Color3(n[0], n[1], n[2]) : default,
            value => Write(((Color3) value).R, ((Color3) value).G, ((Color3) value).B),
            YamlScalarStyle.Plain
        );
    }

    /// <summary>Splits a scalar into the numbers it holds.</summary>
    /// <param name="text">The text.</param>
    /// <param name="count">How many are expected.</param>
    /// <returns>The numbers, short ones left at zero.</returns>
    /// <exception cref="FormatException">A field is not a number, or there are too many.</exception>
    /// <remarks>
    ///     ⚠ <b>Too few is tolerated and too many is not.</b> A hand-written <c>position: 1 2</c> is
    ///     somebody who meant the third to be zero; <c>1 2 3 4</c> in a <c>Vector3</c> is a value that
    ///     came from somewhere else, and binding the first three would silently drop the one field
    ///     that says the file is wrong.
    /// </remarks>
    static float[] Read(string text, int count) {
        ArgumentNullException.ThrowIfNull(text);

        var fields = text.Split(
            [' ', '\t', ',', '[', ']'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        if (fields.Length > count) {
            throw new FormatException(
                $"'{text}' has {fields.Length} numbers and {count} were expected."
            );
        }

        var numbers = new float[count];

        for (var index = 0; index < fields.Length; index++) {
            numbers[index] = float.TryParse(fields[index], CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new FormatException($"'{fields[index]}' in '{text}' is not a number.");
        }

        return numbers;
    }

    static string Write(params ReadOnlySpan<float> numbers) {
        var text = new System.Text.StringBuilder(numbers.Length * 8);

        foreach (var number in numbers) {
            if (text.Length > 0) {
                text.Append(' ');
            }

            text.Append(number.ToString("R", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
