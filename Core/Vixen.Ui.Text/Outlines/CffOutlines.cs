// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Vixen.Ui.Text.Outlines;

/// <summary>Where one charstring lives inside the <c>CFF </c> table.</summary>
internal readonly record struct CffRange(int Start, int End);

/// <summary>Reads PostScript outlines by running the Type 2 charstrings in <c>CFF </c>.</summary>
/// <remarks>
///     ⚠ <b><c>seac</c> is not implemented.</b> An <c>endchar</c> carrying four arguments composes
///     an accented glyph out of two standard-encoding ones. No glyph in the 242 fonts the spike read
///     used it — see <c>docs/plan/spikes/text-glyph-outlines/RESULT.md</c> — and implementing it
///     would mean carrying the Adobe standard encoding table for a case the corpus says does not
///     arise. The base glyph is drawn and the accent is not.
/// </remarks>
internal sealed class CffOutlines {
    /// <summary>How deep a charstring may call before this stops following it.</summary>
    const int MaxDepth = 10;

    readonly byte[] cff;
    readonly CffRange[] charStrings;
    readonly CffRange[] globalSubrs;
    readonly CffRange[][] localSubrs;      // by font-dict index; one entry when the font is not CID
    readonly int[] fdSelect;               // per glyph, or empty for a non-CID font
    readonly float[] matrix = [0.001f, 0, 0, 0.001f, 0, 0];

    public CffOutlines(byte[] cff, int unitsPerEm) {
        this.cff = cff;

        var reader = new SfntReader(cff) { Position = cff.Length > 2 ? cff[2] : 4 };   // past the header
        _ = ReadIndex(ref reader);                                                     // Name
        var top = ReadIndex(ref reader);                                               // Top DICT
        _ = ReadIndex(ref reader);                                                     // String
        globalSubrs = ReadIndex(ref reader);

        var dict = top.Length > 0 ? ReadDict(top[0]) : [];

        if (dict.TryGetValue(FontMatrix, out var fontMatrix) && fontMatrix.Length == 6) {
            for (var i = 0; i < 6; i++) {
                matrix[i] = fontMatrix[i];
            }
        }

        // The matrix normally scales design units down to a 1-unit em. Multiplying it back by the
        // head table's own units-per-em puts the outline in the same space glyf's is in, so a caller
        // never has to ask which format a glyph came from.
        for (var i = 0; i < 6; i++) {
            matrix[i] *= unitsPerEm;
        }

        charStrings = dict.TryGetValue(CharStrings, out var at) && at.Length > 0
            ? ReadIndexAt((int)at[0])
            : [];

        if (dict.TryGetValue(FdArray, out var fdArray) && fdArray.Length > 0) {
            var fonts = ReadIndexAt((int)fdArray[0]);
            localSubrs = new CffRange[fonts.Length][];
            for (var i = 0; i < fonts.Length; i++) {
                localSubrs[i] = ReadSubrs(ReadDict(fonts[i]));
            }

            fdSelect = dict.TryGetValue(FdSelectOp, out var select) && select.Length > 0
                ? ReadFdSelect((int)select[0], charStrings.Length)
                : [];
        } else {
            localSubrs = [ReadSubrs(dict)];
            fdSelect = [];
        }
    }

    // Top DICT and Private DICT operators, two-byte ones prefixed 0xC00.
    const int CharStrings = 17;
    const int Private = 18;
    const int Subrs = 19;
    const int FontMatrix = 0xC07;
    const int FdArray = 0xC24;
    const int FdSelectOp = 0xC25;

    public int GlyphCount => charStrings.Length;

    public GlyphOutline Read(int glyph) {
        if (glyph < 0 || glyph >= charStrings.Length) {
            return GlyphOutline.Empty;
        }

        var fd = glyph < fdSelect.Length ? fdSelect[glyph] : 0;
        var local = fd < localSubrs.Length ? localSubrs[fd] : [];

        var builder = new OutlineBuilder();
        var machine = new Charstrings(builder, matrix, globalSubrs, local);

        // ⚠ A malformed charstring is a broken font, not a broken program. Every read is bounds
        // checked, and what gets past that is a glyph that draws nothing rather than a load that
        // throws — a font with one bad glyph still renders the rest of the page.
        try {
            machine.Run(cff, charStrings[glyph].Start, charStrings[glyph].End, 0);
            machine.Finish();
        } catch (ArgumentOutOfRangeException) {
            return GlyphOutline.Empty;
        } catch (IndexOutOfRangeException) {
            return GlyphOutline.Empty;
        }

        return builder.Build();
    }

    // ================================================================== Structures

    CffRange[] ReadIndexAt(int position) {
        var reader = new SfntReader(cff) { Position = position };
        return ReadIndex(ref reader);
    }

    static CffRange[] ReadIndex(ref SfntReader reader) {
        if (!reader.Has(2)) {
            return [];
        }

        var count = reader.U16();
        if (count == 0) {
            return [];
        }

        var offSize = reader.U8();
        if (offSize is < 1 or > 4) {
            return [];
        }

        var offsets = new int[count + 1];
        for (var i = 0; i <= count; i++) {
            offsets[i] = reader.Has(offSize) ? reader.Offset(offSize) : 0;
        }

        // Offsets are one-based from the byte before the data, which is where the cursor now sits.
        var data = reader.Position - 1;
        var entries = new CffRange[count];
        for (var i = 0; i < count; i++) {
            entries[i] = new CffRange(data + offsets[i], data + offsets[i + 1]);
        }

        reader.Position = data + offsets[count];
        return entries;
    }

    CffRange[] ReadSubrs(Dictionary<int, float[]> dict) {
        if (!dict.TryGetValue(Private, out var priv) || priv.Length < 2) {
            return [];
        }

        var size = (int)priv[0];
        var at = (int)priv[1];
        if (at < 0 || size < 0 || at + size > cff.Length) {
            return [];
        }

        var privateDict = ReadDict(new CffRange(at, at + size));
        return privateDict.TryGetValue(Subrs, out var subrs) && subrs.Length > 0
            ? ReadIndexAt(at + (int)subrs[0])
            : [];
    }

    Dictionary<int, float[]> ReadDict(CffRange range) {
        var result = new Dictionary<int, float[]>();
        var operands = new List<float>();
        var reader = new SfntReader(cff) { Position = range.Start };

        while (reader.Position < range.End && reader.Has(1)) {
            var b0 = reader.U8();

            if (b0 <= 21) {
                var op = b0 == 12 && reader.Has(1) ? 0xC00 | reader.U8() : b0;
                result[op] = [.. operands];
                operands.Clear();
            } else if (b0 == 28 && reader.Has(2)) {
                operands.Add(reader.S16());
            } else if (b0 == 29 && reader.Has(4)) {
                operands.Add((int)reader.U32());
            } else if (b0 == 30) {
                operands.Add(ReadReal(ref reader));
            } else if (b0 is >= 32 and <= 246) {
                operands.Add(b0 - 139);
            } else if (b0 is >= 247 and <= 250 && reader.Has(1)) {
                operands.Add(((b0 - 247) * 256) + reader.U8() + 108);
            } else if (b0 is >= 251 and <= 254 && reader.Has(1)) {
                operands.Add((-(b0 - 251) * 256) - reader.U8() - 108);
            }
        }

        return result;
    }

    /// <summary>A DICT real, stored as a nibble string rather than as a float.</summary>
    static float ReadReal(ref SfntReader reader) {
        var text = new StringBuilder(16);

        while (reader.Has(1)) {
            var b = reader.U8();

            for (var half = 0; half < 2; half++) {
                var nibble = half == 0 ? b >> 4 : b & 15;

                switch (nibble) {
                    case <= 9: text.Append((char)('0' + nibble)); break;
                    case 10: text.Append('.'); break;
                    case 11: text.Append('E'); break;
                    case 12: text.Append("E-"); break;
                    case 14: text.Append('-'); break;
                    case 15:
                        return float.TryParse(text.ToString(), CultureInfo.InvariantCulture, out var value)
                            ? value
                            : 0;
                    default: break;
                }
            }
        }

        return 0;
    }

    int[] ReadFdSelect(int at, int glyphs) {
        var map = new int[glyphs];
        var reader = new SfntReader(cff) { Position = at };
        if (!reader.Has(1)) {
            return map;
        }

        switch (reader.U8()) {
            case 0:
                for (var i = 0; i < glyphs && reader.Has(1); i++) {
                    map[i] = reader.U8();
                }

                break;

            case 3: {
                if (!reader.Has(4)) {
                    break;
                }

                var ranges = reader.U16();
                var first = reader.U16();

                for (var i = 0; i < ranges && reader.Has(3); i++) {
                    var fd = reader.U8();
                    var next = reader.U16();
                    for (var glyph = first; glyph < next && glyph < glyphs; glyph++) {
                        map[glyph] = fd;
                    }

                    first = next;
                }

                break;
            }

            default:
                break;
        }

        return map;
    }

    internal static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;
}
