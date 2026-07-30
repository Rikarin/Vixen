// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;

namespace Vixen.Editor.Core.Scenes;

/// <summary>How the things only a scene names read in a scene file.</summary>
/// <remarks>
///     <para>
///         <b>The vectors moved down to <see cref="MathScalars" /> and an entity id stayed.</b> This
///         owned both for as long as a scene was the only document holding a <c>Vector3</c>; a
///         <c>.vxmat</c>'s feature list holds one too and is read by the runtime, which cannot
///         reference an editor assembly. Two registrations of one type would be two spellings of a
///         colour, resolved by whichever assembly happened to load second.
///     </para>
///     <para>
///         What is left is what nothing outside a scene has: an <see cref="EntityId" />. Everything
///         else is <see cref="MathScalars" />'s, and calling this still registers both — a caller
///         reading a scene wants the whole dialect and should not have to know it comes from two
///         places.
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

        // And the mathematics, which every Vixen document shares.
        MathScalars.Register();
    }
}
