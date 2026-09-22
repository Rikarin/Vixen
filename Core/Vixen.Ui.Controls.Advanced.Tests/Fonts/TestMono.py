# SPDX-FileCopyrightText: Copyright (c) Rikarin
# SPDX-License-Identifier: Apache-2.0
#
# Builds TestMono.ttf, the fixed-pitch face the CodeEditor geometry suite is measured against.
# See README.md beside this file for why it exists and what it is not.
#
#     py -3 TestMono.py
#
# Needs fontTools (pip install fonttools). Deterministic: the same script gives the same bytes.

import os

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

UPEM = 2048
ADVANCE = 1229  # 0.6 em, the usual monospace cell
ASCENT = 1638  # 0.8 em
DESCENT = -410  # 0.2 em
FIRST, LAST = 0x20, 0x7E  # printable ASCII, the alphabet a code editor's grid is measured in


def box(pen, x0, y0, x1, y1):
    pen.moveTo((x0, y0))
    pen.lineTo((x0, y1))
    pen.lineTo((x1, y1))
    pen.lineTo((x1, y0))
    pen.closePath()


def glyph(code):
    pen = TTGlyphPen(None)
    if code == 0x20:
        return pen.glyph()  # a space is an advance and nothing else
    # A hollow box, so a rasterised line reads as a row of cells rather than as nothing. Letters and
    # digits get a taller box than punctuation, which is enough to tell a row of `i` from `.` in a
    # screenshot and is otherwise decoration.
    tall = chr(code).isalnum()
    top = 1400 if tall else 700
    box(pen, 160, 0, 1069, top)
    box(pen, 300, 140, 929, top - 140)  # the counter, wound the other way
    return pen.glyph()


def main():
    names = [".notdef"] + [f"uni{code:04X}" for code in range(FIRST, LAST + 1)]
    cmap = {code: f"uni{code:04X}" for code in range(FIRST, LAST + 1)}
    glyphs = {".notdef": glyph(0x3F)}
    glyphs.update({name: glyph(code) for code, name in cmap.items()})
    metrics = {name: (ADVANCE, 160 if name != "uni0020" else 0) for name in names}

    builder = FontBuilder(UPEM, isTTF=True)
    builder.setupGlyphOrder(names)
    builder.setupCharacterMap(cmap)
    builder.setupGlyf(glyphs)
    builder.setupHorizontalMetrics(metrics)
    builder.setupHorizontalHeader(ascent=ASCENT, descent=DESCENT)
    builder.setupNameTable(
        {
            "familyName": "TestMono",
            "styleName": "Regular",
            "uniqueFontIdentifier": "TestMono Regular",
            "fullName": "TestMono Regular",
            "psName": "TestMono-Regular",
            "version": "Version 1.000",
            "copyright": "Copyright (c) Rikarin. Apache-2.0. A synthetic fixed-pitch test face; see README.md.",
        }
    )
    builder.setupOS2(
        sTypoAscender=ASCENT,
        sTypoDescender=DESCENT,
        sTypoLineGap=0,
        usWinAscent=ASCENT,
        usWinDescent=-DESCENT,
        xAvgCharWidth=ADVANCE,
    )
    builder.font["OS/2"].panose.bProportion = 9  # monospaced
    builder.setupPost(isFixedPitch=1)
    # A fixed timestamp, so a regeneration that changed nothing produces the same bytes.
    builder.font["head"].created = 0
    builder.font["head"].modified = 0

    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "TestMono.ttf")
    builder.save(out)
    print(out, os.path.getsize(out), "bytes")


if __name__ == "__main__":
    main()
