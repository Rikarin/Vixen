// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;

namespace Vixen.Ui.Styling;

/// <summary>The CSS colour keywords.</summary>
/// <remarks>
///     <para>
///         A short list rather than all hundred and forty-eight, and the reason it is needed at all
///         is worth stating: ExCSS resolves <c>color: red</c> to <c>rgb(255, 0, 0)</c> before Vixen
///         sees it, so this table would be dead code — except that a value reached through a
///         <c>var()</c> is passed through verbatim and arrives as the word. So a keyword reaches
///         here only via a custom property, and the ones people put in custom properties are the
///         basic sixteen plus <c>transparent</c>.
///     </para>
///     <para>
///         Anything not here is a keyword rather than a colour, which is the correct outcome for a
///         name this engine does not know: <c>color: papayawhip</c> becomes a value that does not
///         interpolate rather than a wrong colour that does.
///     </para>
/// </remarks>
public static class NamedColors {
    static readonly Dictionary<string, Color> Table = new(StringComparer.OrdinalIgnoreCase) {
        ["transparent"] = new(0, 0, 0, 0),
        ["black"] = new(0, 0, 0),
        ["silver"] = new(192, 192, 192),
        ["gray"] = new(128, 128, 128),
        ["grey"] = new(128, 128, 128),
        ["white"] = new(255, 255, 255),
        ["maroon"] = new(128, 0, 0),
        ["red"] = new(255, 0, 0),
        ["purple"] = new(128, 0, 128),
        ["fuchsia"] = new(255, 0, 255),
        ["magenta"] = new(255, 0, 255),
        ["green"] = new(0, 128, 0),
        ["lime"] = new(0, 255, 0),
        ["olive"] = new(128, 128, 0),
        ["yellow"] = new(255, 255, 0),
        ["navy"] = new(0, 0, 128),
        ["blue"] = new(0, 0, 255),
        ["teal"] = new(0, 128, 128),
        ["aqua"] = new(0, 255, 255),
        ["cyan"] = new(0, 255, 255),
        ["orange"] = new(255, 165, 0)
    };

    /// <summary>Looks a colour keyword up.</summary>
    /// <param name="name">The keyword.</param>
    /// <param name="colour">Receives the colour, sRGB-encoded.</param>
    /// <returns>Whether it is one.</returns>
    public static bool TryGet(ReadOnlySpan<char> name, [NotNullWhen(true)] out Color colour) =>
        Table.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out colour);
}
