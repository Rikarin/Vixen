// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>Reading a node's <c>[Setting]</c> as one of the integer contracts a kernel compares against.</summary>
/// <remarks>
///     <para>
///         <b>A setting is a name and a kernel's selector is a number, and this is the one place the
///         two meet.</b> The names are the enum members' own — <c>Multiply</c>, <c>Worley</c>,
///         <c>Outside</c> — so there is no third table to keep in step with either side, which is the
///         same argument <c>NodeGraph</c>'s README makes about declaring a port's type beside its
///         field.
///     </para>
///     <para>
///         ⚠ <b>A name nothing recognises is a diagnostic, not the first member.</b> Falling back to
///         zero would be a graph that composites with <c>Copy</c> because somebody typed
///         <c>mulitply</c> — a perfectly plausible picture, produced by a typo, with nothing anywhere
///         saying so. The message lists what the setting will take.
///     </para>
/// </remarks>
static class TextureSettings {
    /// <summary>One setting as a member of an enum, or a diagnostic naming what it will take.</summary>
    /// <typeparam name="T">The enum whose members are the accepted names.</typeparam>
    /// <param name="emitter">Where to report.</param>
    /// <param name="setting">The setting's name.</param>
    /// <param name="fallback">What to use so the walk stays sound after the refusal.</param>
    /// <returns>The member, or <paramref name="fallback" /> when the text names none.</returns>
    public static T Enum<T>(TextureEmitter emitter, string setting, T fallback) where T : struct, Enum {
        ArgumentNullException.ThrowIfNull(emitter);

        var text = emitter.Text(setting).Trim();

        if (text.Length == 0) {
            return fallback;
        }

        if (System.Enum.TryParse<T>(text, ignoreCase: true, out var value) && System.Enum.IsDefined(value)) {
            return value;
        }

        emitter.Report(
            "TG0010",
            $"'{setting}' is '{text}', which is not one of {string.Join(", ", System.Enum.GetNames<T>())}.",
            setting
        );

        return fallback;
    }
}
