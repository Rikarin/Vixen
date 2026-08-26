// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Core.Mathematics;

namespace Vixen.Core.Yaml;

/// <summary>How a vector, a rotation and a colour read in any of Vixen's YAML documents.</summary>
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
///         <b>Here rather than in the scene format, because a material is the second document to want
///         them.</b> <c>SceneScalars</c> owned these for as long as a scene was the only file holding
///         a <c>Vector3</c>; a <c>.vxmat</c>'s feature list holds one too, and it is read by the
///         runtime, which cannot reference an editor assembly. Two registrations of one type would be
///         two spellings of a colour resolved by whichever assembly happened to load second — so
///         there is one, and it sits below both.
///     </para>
///     <para>
///         ⚠ <b>Still an explicit call rather than a module initializer</b>, which is the property
///         <c>SceneScalars</c> was careful about and this keeps: the converter table is process-wide,
///         so registering on load would mean that merely <em>referencing</em> this assembly changed
///         how every other document in the process reads a vector.
///     </para>
///     <para>
///         ⚠ <b>Round-trip precision, so "R" and not a fixed number of places.</b> A file saved,
///         reopened and saved again has to produce the same bytes — a format that quietly rounded
///         would make every open-and-close a diff, and every scene a merge conflict with itself.
///     </para>
/// </remarks>
public static class MathScalars {
    static readonly Lock Gate = new();

    static bool registered;

    /// <summary>Registers them, once.</summary>
    /// <remarks>
    ///     ⚠ <b>Once means once even when two threads ask at the same moment, and the flag alone did
    ///     not give that.</b> It was set <i>before</i> the converters were registered and read
    ///     without a barrier, so a second thread could see <c>true</c> and return while the first was
    ///     still halfway down the list — and then bind a document containing a <c>Vector3</c> against
    ///     a table that did not have one yet, which surfaces as a <c>YamlBindingException</c> about a
    ///     perfectly good asset. Fourteen importers call this from their static constructors, and the
    ///     CLR's per-type initialization lock does not serialise fourteen different types against
    ///     each other; a parallel import is what makes the window wide enough to hit.
    /// </remarks>
    public static void Register() {
        if (Volatile.Read(ref registered)) {
            return;
        }

        lock (Gate) {
            if (registered) {
                return;
            }

            RegisterAll();
            Volatile.Write(ref registered, true);
        }
    }

    static void RegisterAll() {
        // Plain style throughout: these are numbers separated by spaces and quoting them would make
        // a hand-edited file's quotes meaningful, which is a trap rather than a feature.
        YamlScalarConverters.Register(
            typeof(Vector2),
            text => Read(text, 2) is var n ? new Vector2(n[0], n[1]) : default,
            value => Write(((Vector2)value).X, ((Vector2)value).Y),
            YamlScalarStyle.Plain
        );

        YamlScalarConverters.Register(
            typeof(Vector3),
            text => Read(text, 3) is var n ? new Vector3(n[0], n[1], n[2]) : default,
            value => Write(((Vector3)value).X, ((Vector3)value).Y, ((Vector3)value).Z),
            YamlScalarStyle.Plain
        );

        YamlScalarConverters.Register(
            typeof(Vector4),
            text => Read(text, 4) is var n ? new Vector4(n[0], n[1], n[2], n[3]) : default,
            value => Write(((Vector4)value).X, ((Vector4)value).Y, ((Vector4)value).Z, ((Vector4)value).W),
            YamlScalarStyle.Plain
        );

        // ⚠ Four numbers, not three angles. Euler angles are what a person edits and they are
        // ambiguous — three orders give three rotations from the same numbers — so the *file* keeps
        // the unambiguous form and the inspector does the conversion at the edge. See EulerAngles.
        YamlScalarConverters.Register(
            typeof(Quaternion),
            text => Read(text, 4) is var n ? new Quaternion(n[0], n[1], n[2], n[3]) : default,
            value => Write(
                ((Quaternion)value).X,
                ((Quaternion)value).Y,
                ((Quaternion)value).Z,
                ((Quaternion)value).W
            ),
            YamlScalarStyle.Plain
        );

        YamlScalarConverters.Register(
            typeof(Color4),
            text => Read(text, 4) is var n ? new Color4(n[0], n[1], n[2], n[3]) : default,
            value => Write(((Color4)value).R, ((Color4)value).G, ((Color4)value).B, ((Color4)value).A),
            YamlScalarStyle.Plain
        );

        // ⚠ Registered alongside <see cref="Color4" /> rather than left to the binder, and the
        // symptom of its absence is worth recording: a light's colour came out as "Color3 has no
        // descriptor", thrown from the *serializer* when a scene was saved. Nothing in
        // `Vixen.Core.Mathematics` carries the reflection generator — see the remarks above — so
        // every type of its that a document names has to be one of these, and a colour with no
        // alpha is as much one as a colour with one.
        YamlScalarConverters.Register(
            typeof(Color3),
            text => Read(text, 3) is var n ? new Color3(n[0], n[1], n[2]) : default,
            value => Write(((Color3)value).R, ((Color3)value).G, ((Color3)value).B),
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
            throw new FormatException($"'{text}' has {fields.Length} numbers and {count} were expected.");
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
        var text = new StringBuilder(numbers.Length * 8);

        foreach (var number in numbers) {
            if (text.Length > 0) {
                text.Append(' ');
            }

            text.Append(number.ToString("R", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
