// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using HarfBuzzSharp;

// A latin font with ligatures and an Arabic one, both shipped with macOS.
const string Latin = "/System/Library/Fonts/Supplemental/Arial.ttf";
const string ArabicFont = "/System/Library/Fonts/GeezaPro.ttc";

Console.WriteLine($"HarfBuzzSharp {typeof(Font).Assembly.GetName().Version}");

Console.WriteLine();

Shape(Latin, "office", "Latn", Direction.LeftToRight, "ligature: ffi should be one glyph");
Shape(Latin, "AVA", "Latn", Direction.LeftToRight, "kerning: AV should tuck");
Shape(ArabicFont, "سلام", "Arab", Direction.RightToLeft, "arabic: joined forms, fewer glyphs than chars");
Shape(Latin, "á", "Latn", Direction.LeftToRight, "decomposed: two chars, cluster mapping");

static void Shape(string path, string text, string script, Direction direction, string what) {
    Console.WriteLine($"── {what}");
    Console.WriteLine($"   text '{text}' ({text.Length} UTF-16 units)");

    var data = File.ReadAllBytes(path);
    var handle = GCHandle.Alloc(data, GCHandleType.Pinned);

    using var blob = new Blob(handle.AddrOfPinnedObject(), data.Length, MemoryMode.Duplicate, () => handle.Free());
    using var face = new Face(blob, 0);
    using var font = new Font(face);

    font.SetScale(64 * 16, 64 * 16);
    font.SetFunctionsOpenType();

    using var buffer = new HarfBuzzSharp.Buffer();
    buffer.AddUtf16(text);
    buffer.Direction = direction;
    buffer.Script = Script.Parse(script);
    buffer.Language = new Language("en");

    font.Shape(buffer);

    var infos = buffer.GlyphInfos;
    var positions = buffer.GlyphPositions;

    Console.WriteLine($"   {infos.Length} glyphs:");
    for (var i = 0; i < infos.Length; i++) {
        Console.WriteLine(
            $"     glyph {infos[i].Codepoint,5}  cluster {infos[i].Cluster,2}  "
            + $"advance {positions[i].XAdvance,6}  offset ({positions[i].XOffset}, {positions[i].YOffset})"
        );
    }

    Console.WriteLine();
}
