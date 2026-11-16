// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Vixen.Interchange;

/// <summary>ZBrush's polypaint in an OBJ, carried into the vertex colours Assimp reads.</summary>
/// <remarks>
///     <para>
///         <b>ZBrush writes polypaint as comments</b>: lines of <c>#MRGB</c> followed by up to 64 groups of eight hex
///         digits, a mask byte then red, green and blue, one group per <c>v</c> line in the order the vertices come.
///         Assimp skips comments, so a polypainted OBJ read as it is has no colours at all.
///     </para>
///     <para>
///         ⚠ <b>Written into the <c>v</c> lines rather than matched up afterwards.</b> An OBJ vertex may carry its
///         colour after its position (<c>v x y z r g b</c>), which Assimp reads, and that keeps each colour on its own
///         vertex through the triangulation and the welding Assimp does; matching by position afterwards would guess
///         wherever two vertices share a point. A <c>v</c> line that already has a colour keeps it. The mask byte says
///         what was masked in ZBrush, which no reader here wants, and is dropped.
///     </para>
/// </remarks>
static class ZBrushPolypaint {
    /// <summary>The comment a polypaint line starts with.</summary>
    const string Marker = "#MRGB";

    static readonly char[] Spaces = [' ', '\t'];

    /// <summary>The OBJ with its polypaint written into its vertices, or null when it has none.</summary>
    /// <param name="obj">The file.</param>
    /// <returns>A new file whose <c>v</c> lines carry the colours, or null to read the file as it is.</returns>
    public static byte[]? Carry(ReadOnlySpan<byte> obj) {
        if (obj.IndexOf("#MRGB"u8) < 0) {
            return null;
        }

        var lines = Encoding.UTF8.GetString(obj).Split('\n');
        var colours = Colours(lines);

        if (colours.Count == 0) {
            return null;
        }

        var written = new StringBuilder(obj.Length + colours.Count * 24);
        var vertex = 0;

        foreach (var raw in lines) {
            var line = raw.TrimEnd('\r');
            var words = line.Split(Spaces, StringSplitOptions.RemoveEmptyEntries);

            written.Append(line);

            if (words is ["v", _, _, _]) {
                if (vertex < colours.Count) {
                    var (red, green, blue) = colours[vertex];

                    written.Append(CultureInfo.InvariantCulture, $" {red / 255f:0.######}")
                        .Append(CultureInfo.InvariantCulture, $" {green / 255f:0.######}")
                        .Append(CultureInfo.InvariantCulture, $" {blue / 255f:0.######}");
                }
            }

            vertex += words is ["v", ..] ? 1 : 0;
            written.Append('\n');
        }

        return Encoding.UTF8.GetBytes(written.ToString());
    }

    /// <summary>Every colour the file's polypaint lines hold, in order, up to the first that is not hex.</summary>
    static List<(byte Red, byte Green, byte Blue)> Colours(string[] lines) {
        List<(byte, byte, byte)> colours = [];

        foreach (var raw in lines) {
            var line = raw.Trim();

            if (!line.StartsWith(Marker, StringComparison.Ordinal)) {
                continue;
            }

            // ZBrush runs the groups together; a hand-written file may space them, which says nothing.
            var groups = string.Concat(line[Marker.Length..].Split(Spaces, StringSplitOptions.RemoveEmptyEntries));

            for (var at = 0; at + 8 <= groups.Length; at += 8) {
                if (!int.TryParse(
                        groups.AsSpan(at + 2, 6),
                        NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture,
                        out var rgb
                    )) {
                    return colours;
                }

                colours.Add(((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            }
        }

        return colours;
    }
}
