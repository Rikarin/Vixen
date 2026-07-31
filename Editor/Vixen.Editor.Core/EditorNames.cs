// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Editor.Core;

/// <summary>Turning the names in source into the words a person reads.</summary>
/// <remarks>
///     <para>
///         <b>One rule, because a member's label and a component's are the same problem.</b> It was
///         <c>InspectorMember.Humanise</c> and served only members, so a component foldout showed
///         <c>PrimitiveShape</c> beside a row reading <c>Cone Inner Angle</c> — two spellings of the
///         same convention in the same panel, an inch apart.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in <c>Vixen.Editor.Inspector</c>, because
///         <c>Vixen.Editor.SceneView</c> cannot see that assembly and is where a component bridge
///         lives.</b> This one is the layer both of them already reference, which is what makes it
///         the answer rather than a copy in each.
///     </para>
/// </remarks>
public static class EditorNames {
    /// <summary>Turns <c>FoamWidth</c> into <c>Foam Width</c>.</summary>
    /// <param name="name">The name in source.</param>
    /// <returns>The label.</returns>
    /// <exception cref="ArgumentException"><paramref name="name" /> is null or empty.</exception>
    /// <remarks>
    ///     ⚠ <b>Done here rather than in the generator</b>, so that the rule is one implementation
    ///     that a test can hold to instead of something baked into thousands of generated string
    ///     literals that would need a rebuild of every consumer to change.
    ///     <para>
    ///         Runs of capitals stay together — <c>UVScale</c> is <c>UV Scale</c>, not <c>U V Scale</c>
    ///         — and a leading underscore or <c>m_</c> is dropped, because a field naming convention
    ///         is not something a user should have to read.
    ///     </para>
    /// </remarks>
    public static string Humanise(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var start = 0;

        if (name.StartsWith("m_", StringComparison.Ordinal)) {
            start = 2;
        }

        while (start < name.Length && name[start] == '_') {
            start++;
        }

        if (start >= name.Length) {
            return name;
        }

        var text = new StringBuilder(name.Length + 8);
        text.Append(char.ToUpperInvariant(name[start]));

        for (var index = start + 1; index < name.Length; index++) {
            var character = name[index];

            if (character == '_') {
                text.Append(' ');
                continue;
            }

            var previous = name[index - 1];
            var next = index + 1 < name.Length ? name[index + 1] : '\0';

            var boundary = (char.IsUpper(character) && !char.IsUpper(previous))
                || (char.IsDigit(character) && !char.IsDigit(previous))
                || (char.IsUpper(character) && char.IsUpper(previous) && char.IsLower(next));

            if (boundary && text.Length > 0 && text[^1] != ' ') {
                text.Append(' ');
            }

            text.Append(character);
        }

        return text.ToString();
    }
}
