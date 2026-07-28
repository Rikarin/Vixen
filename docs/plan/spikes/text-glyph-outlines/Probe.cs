// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

// Spike: can a managed parser get glyph outlines out of the tables HarfBuzz already has?
//
// HarfBuzzSharp exposes no outline API — TryGetGlyphExtents is a bounding box and there is no
// draw, paint or outline surface at all — so an MSDF atlas needs contours from somewhere. This
// reads them from Face.ReferenceTable, and checks the result against HarfBuzz's own extents over
// every glyph of every font on the machine.
//
// Run with a project referencing HarfBuzzSharp 14.2.1.1 and its macOS native assets.

using System.Runtime.InteropServices;
using HarfBuzzSharp;

// ================================================================== The corpus

var fonts = new List<string>();

foreach (var directory in new[] {
    "/System/Library/Fonts", "/System/Library/Fonts/Supplemental",
    "/Users/jiu/Projects/Vixen/references/text-rendering-tests/fonts"
}) {
    if (Directory.Exists(directory)) {
        fonts.AddRange(Directory.EnumerateFiles(directory)
            .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)));
    }
}

fonts.Sort(StringComparer.Ordinal);
Console.WriteLine($"HarfBuzzSharp {typeof(Face).Assembly.GetName().Version} — {fonts.Count} fonts\n");

int totalGlyphs = 0, totalMatched = 0, totalEmpty = 0, ttfFonts = 0, cffFonts = 0, skippedFonts = 0;
int totalPointMatched = 0, compositeMisses = 0, pointMatchMisses = 0, curveOnlyMisses = 0;
int glyfGlyphs = 0, glyfPoint = 0, glyfCurve = 0, cffGlyphs = 0, cffPoint = 0, cffCurve = 0, seacMisses = 0;
var worst = new List<(string Font, int Glyph, float Error)>();

foreach (var path in fonts) {
    try {
        Check(path);
    } catch (Exception e) {
        skippedFonts++;
        Console.WriteLine($"!! {Path.GetFileName(path)}: {e.GetType().Name}: {e.Message}");
    }
}

Console.WriteLine($"\n{new string('=', 70)}");
Console.WriteLine($"fonts     {fonts.Count - skippedFonts} read ({ttfFonts} glyf, {cffFonts} CFF), {skippedFonts} failed");
Console.WriteLine($"glyphs    {totalGlyphs} compared, {totalEmpty} empty");
Console.WriteLine($"points    {totalPointMatched} of {totalGlyphs} agree using control-point bounds " +
                  $"({(totalGlyphs == 0 ? 0 : 100.0 * totalPointMatched / totalGlyphs):F3}%)");
Console.WriteLine($"misses    {compositeMisses} composite, {pointMatchMisses} used point-matching, " +
                  $"{curveOnlyMisses} would pass on point bounds, {seacMisses} CFF seac");
Console.WriteLine($"bounds    {totalMatched} of {totalGlyphs} agree with HarfBuzz within 1 unit " +
                  $"({(totalGlyphs == 0 ? 0 : 100.0 * totalMatched / totalGlyphs):F3}%)");
Console.WriteLine();
Console.WriteLine($"  glyf    {glyfGlyphs,7} glyphs   points {100.0 * glyfPoint / Math.Max(1, glyfGlyphs):F3}%   curve {100.0 * glyfCurve / Math.Max(1, glyfGlyphs):F3}%");
Console.WriteLine($"  CFF     {cffGlyphs,7} glyphs   points {100.0 * cffPoint / Math.Max(1, cffGlyphs):F3}%   curve {100.0 * cffCurve / Math.Max(1, cffGlyphs):F3}%");

if (worst.Count > 0) {
    Console.WriteLine("\nlargest point-bounds disagreements:");
    foreach (var (font, glyph, error) in worst.OrderByDescending(w => w.Error).Take(15)) {
        Console.WriteLine($"  {error,9:F1} units  glyph {glyph,-6} {font}");
    }
}

void Check(string path) {
    var data = File.ReadAllBytes(path);
    var handle = GCHandle.Alloc(data, GCHandleType.Pinned);

    using var blob = new Blob(handle.AddrOfPinnedObject(), data.Length, MemoryMode.Duplicate, () => handle.Free());
    using var face = new Face(blob, 0);
    using var font = new Font(face);

    font.SetScale(face.UnitsPerEm, face.UnitsPerEm);
    font.SetFunctionsOpenType();

    var name = Path.GetFileName(path);
    var hmtxData = Table(face, "hmtx");
    var hheaData = Table(face, "hhea");
    var metrics = hheaData.Length > 34 ? new Reader(hheaData) { Position = 34 }.U16() : 0;

    int Lsb(int glyph) {
        if (hmtxData.Length == 0 || metrics == 0) { return int.MinValue; }
        if (glyph < metrics) { return new Reader(hmtxData) { Position = glyph * 4 + 2 }.S16(); }
        var at = metrics * 4 + (glyph - metrics) * 2;
        return at + 2 <= hmtxData.Length ? new Reader(hmtxData) { Position = at }.S16() : int.MinValue;
    }

    var glyf = Table(face, "glyf");
    var cff = Table(face, "CFF ");

    Func<int, Outline>? read = null;
    TrueTypeOutlines? tt = null;
    CffOutlines? cffReader = null;
    if (glyf.Length > 0) {
        tt = new TrueTypeOutlines(Table(face, "head"), Table(face, "maxp"), Table(face, "loca"), glyf);
        read = tt.Read;
        ttfFonts++;
    } else if (cff.Length > 0) {
        cffReader = new CffOutlines(cff, face.UnitsPerEm);
        read = cffReader.Read;
        cffFonts++;
    }

    if (read is null) { return; }

    int matched = 0, compared = 0, empty = 0, pointMatched = 0;
    float worstHere = 0, worstPointHere = 0;
    var worstGlyph = -1;
    var worstPointGlyph = -1;

    for (var glyph = 0; glyph < face.GlyphCount; glyph++) {
        tt?.ResetPointMatching();
        var outline = read(glyph);
        var has = font.TryGetGlyphExtents((uint)glyph, out var extents);

        if (outline.IsEmpty) {
            empty++;
            continue;
        }

        if (!has) { continue; }

        var (minX, minY, maxX, maxY) = outline.Bounds();
        float hbMinX = extents.XBearing;
        float hbMaxX = extents.XBearing + extents.Width;
        float hbMaxY = extents.YBearing;
        float hbMinY = extents.YBearing + extents.Height;

        var error = Math.Max(
            Math.Max(Math.Abs(minX - hbMinX), Math.Abs(maxX - hbMaxX)),
            Math.Max(Math.Abs(minY - hbMinY), Math.Abs(maxY - hbMaxY))
        );

        // ⚠ HarfBuzz reports *positioned* extents: for glyf it shifts the outline so that xMin
        // lands on the left side bearing. Undo that before comparing, or every font whose lsb
        // disagrees with its own stored xMin looks like a parser bug.
        if (tt is not null && tt.StoredBounds(glyph) is { } sb && Lsb(glyph) != int.MinValue) {
            var shift = Lsb(glyph) - sb.MinX;
            hbMinX -= shift;
            hbMaxX -= shift;
        }

        var (pMinX, pMinY, pMaxX, pMaxY) = outline.PointBounds();
        var pointError = Math.Max(
            Math.Max(Math.Abs(pMinX - hbMinX), Math.Abs(pMaxX - hbMaxX)),
            Math.Max(Math.Abs(pMinY - hbMinY), Math.Abs(pMaxY - hbMaxY)));

        compared++;
        if (pointError <= 1.0f) { pointMatched++; }
        else if (pointError > worstPointHere) { worstPointHere = pointError; worstPointGlyph = glyph; }
        if (error <= 1.0f) { matched++; }
        else {
            if (tt is not null && tt.IsComposite(glyph)) { compositeMisses++; }
            if (tt is not null && tt.UsesPointMatching) { pointMatchMisses++; }
            if (pointError <= 1.0f) { curveOnlyMisses++; }
            if (cffReader is not null && cffReader.LastWasSeac) { seacMisses++; }
            if (error > worstHere) { worstHere = error; worstGlyph = glyph; }
        }
    }

    if (tt is not null) { glyfGlyphs += compared; glyfPoint += pointMatched; glyfCurve += matched; }
    else { cffGlyphs += compared; cffPoint += pointMatched; cffCurve += matched; }

    totalGlyphs += compared;
    totalMatched += matched;
    totalPointMatched += pointMatched;
    totalEmpty += empty;

    if (worstPointGlyph >= 0) { worst.Add((name, worstPointGlyph, worstPointHere)); }

    var rate = compared == 0 ? 100.0 : 100.0 * pointMatched / compared;
    var flag = rate >= 99.99 ? "  " : rate >= 99 ? " ~" : " !";
    if (rate < 99.99) {
        Console.WriteLine($"{flag} {rate,7:F3}%  {pointMatched,6}/{compared,-6} {name}  (worst {worstPointHere:F1})");
    }
}

static byte[] Table(Face face, string name) {
    using var table = face.ReferenceTable(new Tag(name[0], name[1], name[2], name[3]));
    return table.Length == 0 ? [] : table.AsSpan().ToArray();
}


namespace Probe;

public enum Verb { Move, Line, Quad, Cubic, Close }

public readonly record struct Segment(Verb Verb, float X0, float Y0, float X1, float Y1, float X2, float Y2);

public sealed class Outline {
    public List<Segment> Segments { get; } = [];

    public void Move(float x, float y) => Segments.Add(new(Verb.Move, x, y, 0, 0, 0, 0));
    public void Line(float x, float y) => Segments.Add(new(Verb.Line, x, y, 0, 0, 0, 0));
    public void Quad(float cx, float cy, float x, float y) => Segments.Add(new(Verb.Quad, cx, cy, x, y, 0, 0));
    public void Cubic(float ax, float ay, float bx, float by, float x, float y) =>
        Segments.Add(new(Verb.Cubic, ax, ay, bx, by, x, y));
    public void Close() => Segments.Add(new(Verb.Close, 0, 0, 0, 0, 0, 0));

    public bool IsEmpty => Segments.Count == 0;

    /// <summary>Bounds of the control points, ignoring where the curve actually goes.</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) PointBounds() {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        void Hit(float x, float y) {
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        foreach (var s in Segments) {
            switch (s.Verb) {
                case Verb.Move: case Verb.Line: Hit(s.X0, s.Y0); break;
                case Verb.Quad: Hit(s.X0, s.Y0); Hit(s.X1, s.Y1); break;
                case Verb.Cubic: Hit(s.X0, s.Y0); Hit(s.X1, s.Y1); Hit(s.X2, s.Y2); break;
                default: break;
            }
        }
        return IsEmpty ? (0, 0, 0, 0) : (minX, minY, maxX, maxY);
    }

    /// <summary>The bounds of the drawn curve, sampled finely enough to compare with a font's own.</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) Bounds() {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        float cx = 0, cy = 0, sx = 0, sy = 0;

        void Hit(float x, float y) {
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }

        foreach (var s in Segments) {
            switch (s.Verb) {
                case Verb.Move: cx = sx = s.X0; cy = sy = s.Y0; Hit(cx, cy); break;
                case Verb.Line: cx = s.X0; cy = s.Y0; Hit(cx, cy); break;
                case Verb.Quad:
                    for (var i = 1; i <= 64; i++) {
                        var t = i / 64f; var u = 1 - t;
                        Hit(u * u * cx + 2 * u * t * s.X0 + t * t * s.X1, u * u * cy + 2 * u * t * s.Y0 + t * t * s.Y1);
                    }
                    cx = s.X1; cy = s.Y1;
                    break;
                case Verb.Cubic:
                    for (var i = 1; i <= 64; i++) {
                        var t = i / 64f; var u = 1 - t;
                        Hit(u * u * u * cx + 3 * u * u * t * s.X0 + 3 * u * t * t * s.X1 + t * t * t * s.X2,
                            u * u * u * cy + 3 * u * u * t * s.Y0 + 3 * u * t * t * s.Y1 + t * t * t * s.Y2);
                    }
                    cx = s.X2; cy = s.Y2;
                    break;
                case Verb.Close: cx = sx; cy = sy; break;
                default: break;
            }
        }

        return IsEmpty ? (0, 0, 0, 0) : (minX, minY, maxX, maxY);
    }
}

// ====================================================================== Reading

public ref struct Reader(ReadOnlySpan<byte> data) {
    readonly ReadOnlySpan<byte> data = data;
    public int Position { get; set; }

    public byte U8() => data[Position++];
    public sbyte S8() => (sbyte)data[Position++];
    public ushort U16() { var v = (ushort)((data[Position] << 8) | data[Position + 1]); Position += 2; return v; }
    public short S16() => (short)U16();
    public uint U32() { var v = ((uint)U16() << 16) | U16(); return v; }
    public int Offset(int size) {
        var v = 0;
        for (var i = 0; i < size; i++) { v = (v << 8) | data[Position++]; }
        return v;
    }
}

// ====================================================================== glyf

public sealed class TrueTypeOutlines(byte[] head, byte[] maxp, byte[] loca, byte[] glyf) {
    readonly int longLoca = new Reader(head) { Position = 50 }.S16();
    readonly int glyphs = new Reader(maxp) { Position = 4 }.U16();

    public int GlyphCount => glyphs;

    public bool IsComposite(int glyph) {
        var start = LocaAt(glyph); var end = LocaAt(glyph + 1);
        if (end <= start) { return false; }
        return new Reader(glyf) { Position = start }.S16() < 0;
    }

    /// <summary>The bounding box the font itself stores in the glyph header.</summary>
    public (int MinX, int MinY, int MaxX, int MaxY)? StoredBounds(int glyph) {
        var start = LocaAt(glyph); var end = LocaAt(glyph + 1);
        if (end <= start) { return null; }
        var r = new Reader(glyf) { Position = start + 2 };
        return (r.S16(), r.S16(), r.S16(), r.S16());
    }

    public bool UsesPointMatching { get; private set; }
    public void ResetPointMatching() => UsesPointMatching = false;

    int LocaAt(int index) {
        var r = new Reader(loca);
        if (longLoca != 0) { r.Position = index * 4; return (int)r.U32(); }
        r.Position = index * 2; return r.U16() * 2;
    }

    public Outline Read(int glyph) {
        var outline = new Outline();
        Append(outline, glyph, 1, 0, 0, 1, 0, 0, 0);
        return outline;
    }

    void Append(Outline outline, int glyph, float a, float b, float c, float d, float e, float f, int depth) {
        if (depth > 8 || glyph < 0 || glyph >= glyphs) { return; }

        var start = LocaAt(glyph);
        var end = LocaAt(glyph + 1);
        if (end <= start) { return; }               // an empty glyph: a space, legitimately

        var r = new Reader(glyf) { Position = start };
        var contours = r.S16();
        r.Position += 8;                            // the stored bounding box

        if (contours >= 0) {
            Simple(outline, ref r, contours, a, b, c, d, e, f);
            return;
        }

        while (true) {
            var flags = r.U16();
            var component = r.U16();

            float dx, dy;
            if ((flags & 0x0001) != 0) {            // ARG_1_AND_2_ARE_WORDS
                dx = r.S16(); dy = r.S16();
            } else {
                dx = r.S8(); dy = r.S8();
            }

            if ((flags & 0x0002) == 0) { dx = dy = 0; UsesPointMatching = true; }   // point matching

            float ca = 1, cb = 0, cc = 0, cd = 1;
            if ((flags & 0x0008) != 0) { ca = cd = F2Dot14(ref r); }
            else if ((flags & 0x0040) != 0) { ca = F2Dot14(ref r); cd = F2Dot14(ref r); }
            else if ((flags & 0x0080) != 0) { ca = F2Dot14(ref r); cb = F2Dot14(ref r); cc = F2Dot14(ref r); cd = F2Dot14(ref r); }

            var next = r.Position;
            Append(outline, component,
                ca * a + cb * c, ca * b + cb * d,
                cc * a + cd * c, cc * b + cd * d,
                dx * a + dy * c + e, dx * b + dy * d + f,
                depth + 1);
            r.Position = next;

            if ((flags & 0x0020) == 0) { break; }   // MORE_COMPONENTS
        }
    }

    static float F2Dot14(ref Reader r) => r.S16() / 16384f;

    static void Simple(Outline outline, ref Reader r, int contours, float a, float b, float c, float d, float e, float f) {
        var ends = new int[contours];
        for (var i = 0; i < contours; i++) { ends[i] = r.U16(); }

        var points = contours == 0 ? 0 : ends[^1] + 1;

        // ⚠ Not `r.Position += r.U16()`. A compound assignment reads the target before evaluating
        // the right-hand side, so that skips the instructions from where the length *started* —
        // two bytes short, every glyph, in a way that reads perfectly on the page.
        var instructions = r.U16();
        r.Position += instructions;

        var flags = new byte[points];
        for (var i = 0; i < points;) {
            var flag = r.U8();
            flags[i++] = flag;
            if ((flag & 0x08) != 0) {               // REPEAT_FLAG
                var repeat = r.U8();
                while (repeat-- > 0 && i < points) { flags[i++] = flag; }
            }
        }

        var xs = new int[points];
        var x = 0;
        for (var i = 0; i < points; i++) {
            if ((flags[i] & 0x02) != 0) { var v = r.U8(); x += (flags[i] & 0x10) != 0 ? v : -v; }
            else if ((flags[i] & 0x10) == 0) { x += r.S16(); }
            xs[i] = x;
        }

        var ys = new int[points];
        var y = 0;
        for (var i = 0; i < points; i++) {
            if ((flags[i] & 0x04) != 0) { var v = r.U8(); y += (flags[i] & 0x20) != 0 ? v : -v; }
            else if ((flags[i] & 0x20) == 0) { y += r.S16(); }
            ys[i] = y;
        }

        var first = 0;
        foreach (var last in ends) {
            if (last < first) { first = last + 1; continue; }
            Contour(outline, flags, xs, ys, first, last, a, b, c, d, e, f);
            first = last + 1;
        }
    }

    /// <summary>
    ///     One contour of a TrueType glyph. ⚠ Two consecutive off-curve points imply an on-curve
    ///     point midway between them, and a contour may start off-curve — both are common and both
    ///     produce a visibly wrong shape if missed rather than an error.
    /// </summary>
    static void Contour(Outline outline, byte[] flags, int[] xs, int[] ys, int first, int last,
                        float a, float b, float c, float d, float e, float f) {
        var n = last - first + 1;
        if (n <= 0) { return; }

        (float X, float Y) At(int i) {
            var k = first + (((i % n) + n) % n);
            float px = xs[k], py = ys[k];
            return (a * px + c * py + e, b * px + d * py + f);
        }

        bool On(int i) => (flags[first + (((i % n) + n) % n)] & 0x01) != 0;
        (float X, float Y) Mid((float X, float Y) p, (float X, float Y) q) => ((p.X + q.X) / 2, (p.Y + q.Y) / 2);

        var startIndex = 0;
        (float X, float Y) startPoint;

        if (On(0)) { startPoint = At(0); startIndex = 1; }
        else if (On(n - 1)) { startPoint = At(n - 1); startIndex = 0; }
        else { startPoint = Mid(At(n - 1), At(0)); startIndex = 0; }

        outline.Move(startPoint.X, startPoint.Y);

        var i = startIndex;
        var seen = 0;
        (float X, float Y)? pending = null;

        while (seen < n) {
            var p = At(i);
            if (On(i)) {
                if (pending is { } q) { outline.Quad(q.X, q.Y, p.X, p.Y); pending = null; }
                else { outline.Line(p.X, p.Y); }
            } else {
                if (pending is { } q) {
                    var m = Mid(q, p);
                    outline.Quad(q.X, q.Y, m.X, m.Y);
                }

                pending = p;
            }

            i++; seen++;
        }

        if (pending is { } tail) { outline.Quad(tail.X, tail.Y, startPoint.X, startPoint.Y); }

        outline.Close();
    }
}

// ====================================================================== CFF

public sealed class CffOutlines {
    readonly byte[] cff;
    readonly int[][] charStrings;
    readonly int[][] globalSubrs;
    readonly int[][] localSubrs;               // by FD index; [0] when the font is not CID
    readonly int[] fdSelect;                   // per glyph, or empty
    readonly float[] matrix = [0.001f, 0, 0, 0.001f, 0, 0];
    public int Skipped { get; private set; }   // charstrings that used something unimplemented
    public bool LastWasSeac { get; private set; }

    public int GlyphCount => charStrings.Length;

    public CffOutlines(byte[] cff, int unitsPerEm) {
        this.cff = cff;

        var r = new Reader(cff) { Position = cff[2] };   // hdrSize
        _ = Index(ref r);                                 // Name
        var top = Index(ref r);                           // Top DICT
        _ = Index(ref r);                                 // String
        globalSubrs = Index(ref r);

        var dict = Dict(top[0]);

        if (dict.TryGetValue(0xC07, out var fm) && fm.Length == 6) {
            for (var i = 0; i < 6; i++) { matrix[i] = fm[i]; }
        }

        // The matrix is normally 1/upem; anything else is a font that scales its own outlines.
        var scale = unitsPerEm;
        for (var i = 0; i < 6; i++) { matrix[i] *= scale; }

        var start = (int)dict[17][0];
        var cs = new Reader(cff) { Position = start };
        charStrings = Index(ref cs);

        if (dict.TryGetValue(0xC24, out var fdArray)) {          // CID: one Private per FD
            var fr = new Reader(cff) { Position = (int)fdArray[0] };
            var fonts = Index(ref fr);
            localSubrs = new int[fonts.Length][];
            for (var i = 0; i < fonts.Length; i++) { localSubrs[i] = Subrs(Dict(fonts[i])); }

            fdSelect = dict.TryGetValue(0xC25, out var sel) ? FdSelect((int)sel[0], charStrings.Length) : [];
        } else {
            localSubrs = [Subrs(dict)];
            fdSelect = [];
        }
    }

    int[] Subrs(Dictionary<int, float[]> dict) {
        if (!dict.TryGetValue(18, out var priv) || priv.Length < 2) { return []; }

        var size = (int)priv[0];
        var at = (int)priv[1];
        var p = Dict((at, at + size));
        if (!p.TryGetValue(19, out var subrs)) { return []; }

        var r = new Reader(cff) { Position = at + (int)subrs[0] };
        var index = Index(ref r);
        return Flatten(index);
    }

    // An INDEX as a flat list of (start, end) pairs, kept as int[] so the arrays above stay simple.
    static int[] Flatten(int[][] index) {
        var flat = new int[index.Length * 2];
        for (var i = 0; i < index.Length; i++) { flat[i * 2] = index[i][0]; flat[i * 2 + 1] = index[i][1]; }
        return flat;
    }

    int[][] Index(ref Reader r) {
        var count = r.U16();
        if (count == 0) { return []; }

        var offSize = r.U8();
        var offsets = new int[count + 1];
        for (var i = 0; i <= count; i++) { offsets[i] = r.Offset(offSize); }

        var data = r.Position - 1;
        var entries = new int[count][];
        for (var i = 0; i < count; i++) { entries[i] = [data + offsets[i], data + offsets[i + 1]]; }

        r.Position = data + offsets[count];
        return entries;
    }

    Dictionary<int, float[]> Dict(int[] range) => Dict((range[0], range[1]));

    Dictionary<int, float[]> Dict((int Start, int End) range) {
        var result = new Dictionary<int, float[]>();
        var operands = new List<float>();
        var r = new Reader(cff) { Position = range.Start };

        while (r.Position < range.End) {
            var b0 = r.U8();

            if (b0 <= 21) {
                var op = b0 == 12 ? 0xC00 | r.U8() : b0;
                result[op] = [.. operands];
                operands.Clear();
            } else if (b0 == 28) { operands.Add(r.S16()); }
            else if (b0 == 29) { operands.Add((int)r.U32()); }
            else if (b0 == 30) { operands.Add(Real(ref r)); }
            else if (b0 >= 32 && b0 <= 246) { operands.Add(b0 - 139); }
            else if (b0 >= 247 && b0 <= 250) { operands.Add((b0 - 247) * 256 + r.U8() + 108); }
            else if (b0 >= 251 && b0 <= 254) { operands.Add(-(b0 - 251) * 256 - r.U8() - 108); }
        }

        return result;
    }

    static float Real(ref Reader r) {
        var text = "";
        while (true) {
            var b = r.U8();
            foreach (var nibble in new[] { b >> 4, b & 15 }) {
                if (nibble <= 9) { text += (char)('0' + nibble); }
                else if (nibble == 10) { text += '.'; }
                else if (nibble == 11) { text += 'E'; }
                else if (nibble == 12) { text += "E-"; }
                else if (nibble == 14) { text += '-'; }
                else if (nibble == 15) { return float.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0; }
            }
        }
    }

    int[] FdSelect(int at, int glyphs) {
        var map = new int[glyphs];
        var r = new Reader(cff) { Position = at };
        var format = r.U8();

        if (format == 0) {
            for (var i = 0; i < glyphs; i++) { map[i] = r.U8(); }
        } else if (format == 3) {
            var ranges = r.U16();
            var first = r.U16();
            for (var i = 0; i < ranges; i++) {
                var fd = r.U8();
                var next = r.U16();
                for (var g = first; g < next && g < glyphs; g++) { map[g] = fd; }
                first = next;
            }
        }

        return map;
    }

    static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    public Outline Read(int glyph) {
        var outline = new Outline();
        if (glyph < 0 || glyph >= charStrings.Length) { return outline; }

        var fd = fdSelect.Length > glyph ? fdSelect[glyph] : 0;
        var local = fd < localSubrs.Length ? localSubrs[fd] : [];

        var state = new State(outline, matrix, globalSubrs, local);
        try {
            state.Run(cff, charStrings[glyph][0], charStrings[glyph][1], 0);
            state.Finish();
        } catch (Exception) {
            Skipped++;
        }

        LastWasSeac = state.Seac;

        return outline;
    }

    /// <summary>The Type 2 charstring machine.</summary>
    sealed class State(Outline outline, float[] m, int[][] global, int[] local) {
        readonly float[] stack = new float[64];
        int count;
        float x, y;
        int stems;
        bool width;
        bool open;

        public bool Seac;

        readonly int globalBias = Bias(global.Length);
        readonly int localBias = Bias(local.Length / 2);

        void Push(float v) { if (count < stack.Length) { stack[count++] = v; } }

        (float X, float Y) T(float px, float py) => (m[0] * px + m[2] * py + m[4], m[1] * px + m[3] * py + m[5]);

        void MoveTo() {
            if (open) { outline.Close(); }
            var p = T(x, y);
            outline.Move(p.X, p.Y);
            open = true;
        }

        void LineTo() { var p = T(x, y); outline.Line(p.X, p.Y); }

        void CurveTo(float ax, float ay, float bx, float by) {
            var a = T(ax, ay); var b = T(bx, by); var e = T(x, y);
            outline.Cubic(a.X, a.Y, b.X, b.Y, e.X, e.Y);
        }

        public void Finish() { if (open) { outline.Close(); } }

        /// <summary>⚠ The first stack-clearing operator may carry a leading width argument.</summary>
        int Odd(int expected) {
            if (width) { return 0; }

            width = true;

            // ⚠ A stem operator takes pairs, so an *odd* count is the one carrying a width. Getting
            // this backwards miscounts the stems, which makes hintmask skip the wrong number of
            // bytes, which desynchronises the rest of the charstring — a wrong shape rather than an
            // error, and only in fonts hinted heavily enough to have a hintmask at all.
            return expected < 0 ? count % 2 : count > expected ? 1 : 0;
        }

        public void Run(byte[] data, int start, int end, int depth) {
            if (depth > 10) { throw new InvalidOperationException("subroutine recursion"); }

            var r = new Reader(data) { Position = start };

            while (r.Position < end) {
                var b0 = r.U8();

                if (b0 >= 32 || b0 == 28) {
                    if (b0 == 28) { Push(r.S16()); }
                    else if (b0 <= 246) { Push(b0 - 139); }
                    else if (b0 <= 250) { Push((b0 - 247) * 256 + r.U8() + 108); }
                    else if (b0 <= 254) { Push(-(b0 - 251) * 256 - r.U8() - 108); }
                    else { Push((int)r.U32() / 65536f); }
                    continue;
                }

                switch (b0) {
                    case 1: case 3: case 18: case 23:                 // stems
                        stems += (count - Odd(-1)) / 2;
                        count = 0;
                        break;

                    case 19: case 20:                                  // hintmask / cntrmask
                        stems += (count - Odd(-1)) / 2;
                        count = 0;
                        r.Position += (stems + 7) / 8;
                        break;

                    case 21: {                                         // rmoveto
                        var i = Odd(2);
                        x += stack[i]; y += stack[i + 1];
                        MoveTo(); count = 0;
                        break;
                    }

                    case 22: { var i = Odd(1); x += stack[i]; MoveTo(); count = 0; break; }   // hmoveto
                    case 4: { var i = Odd(1); y += stack[i]; MoveTo(); count = 0; break; }    // vmoveto

                    case 5:                                            // rlineto
                        for (var i = 0; i + 1 < count; i += 2) { x += stack[i]; y += stack[i + 1]; LineTo(); }
                        count = 0;
                        break;

                    case 6: case 7: {                                  // hlineto / vlineto
                        var horizontal = b0 == 6;
                        for (var i = 0; i < count; i++) {
                            if (horizontal) { x += stack[i]; } else { y += stack[i]; }
                            LineTo();
                            horizontal = !horizontal;
                        }

                        count = 0;
                        break;
                    }

                    case 8:                                            // rrcurveto
                        for (var i = 0; i + 5 < count; i += 6) { Relative(stack[i], stack[i + 1], stack[i + 2], stack[i + 3], stack[i + 4], stack[i + 5]); }
                        count = 0;
                        break;

                    case 24: {                                         // rcurveline
                        var i = 0;
                        for (; i + 5 < count - 2; i += 6) { Relative(stack[i], stack[i + 1], stack[i + 2], stack[i + 3], stack[i + 4], stack[i + 5]); }
                        if (i + 1 < count) { x += stack[i]; y += stack[i + 1]; LineTo(); }
                        count = 0;
                        break;
                    }

                    case 25: {                                         // rlinecurve
                        var i = 0;
                        for (; i + 1 < count - 6; i += 2) { x += stack[i]; y += stack[i + 1]; LineTo(); }
                        if (i + 5 < count) { Relative(stack[i], stack[i + 1], stack[i + 2], stack[i + 3], stack[i + 4], stack[i + 5]); }
                        count = 0;
                        break;
                    }

                    case 26: {                                         // vvcurveto
                        var i = 0;
                        float dx = 0;
                        if ((count & 1) != 0) { dx = stack[0]; i = 1; }
                        for (; i + 3 < count; i += 4) { Relative(dx, stack[i], stack[i + 1], stack[i + 2], 0, stack[i + 3]); dx = 0; }
                        count = 0;
                        break;
                    }

                    case 27: {                                         // hhcurveto
                        var i = 0;
                        float dy = 0;
                        if ((count & 1) != 0) { dy = stack[0]; i = 1; }
                        for (; i + 3 < count; i += 4) { Relative(stack[i], dy, stack[i + 1], stack[i + 2], stack[i + 3], 0); dy = 0; }
                        count = 0;
                        break;
                    }

                    case 30: case 31: {                                // vhcurveto / hvcurveto
                        var horizontal = b0 == 31;
                        var i = 0;
                        while (i + 3 < count) {
                            var last = i + 8 > count;
                            var extra = last && count - i == 5 ? stack[i + 4] : 0;
                            if (horizontal) { Relative(stack[i], 0, stack[i + 1], stack[i + 2], extra, stack[i + 3]); }
                            else { Relative(0, stack[i], stack[i + 1], stack[i + 2], stack[i + 3], extra); }
                            horizontal = !horizontal;
                            i += 4;
                        }

                        count = 0;
                        break;
                    }

                    case 10: case 29: {                                // callsubr / callgsubr
                        var subrs = b0 == 10 ? local : Flatten(global);
                        var bias = b0 == 10 ? localBias : globalBias;
                        var index = (int)stack[--count] + bias;
                        if (index >= 0 && index * 2 + 1 < subrs.Length) {
                            Run(data, subrs[index * 2], subrs[index * 2 + 1], depth + 1);
                        }

                        break;
                    }

                    case 11: return;                                   // return

                    case 14:                                           // endchar
                        if (count >= 4) { Seac = true; }
                        _ = Odd(count >= 4 ? 4 : 0);
                        Finish();
                        open = false;
                        return;

                    case 12: {
                        var b1 = r.U8();
                        Escape(b1);
                        break;
                    }

                    default:
                        count = 0;
                        break;
                }
            }
        }

        void Escape(byte op) {
            switch (op) {
                case 35:                                               // flex
                    Relative(stack[0], stack[1], stack[2], stack[3], stack[4], stack[5]);
                    Relative(stack[6], stack[7], stack[8], stack[9], stack[10], stack[11]);
                    break;

                case 34: {                                             // hflex
                    var y0 = y;
                    Relative(stack[0], 0, stack[1], stack[2], stack[3], 0);
                    Relative(stack[4], 0, stack[5], y0 - (y + stack[2]), stack[6], 0);
                    y = y0;
                    break;
                }

                case 36: {                                             // hflex1
                    var y0 = y;
                    Relative(stack[0], stack[1], stack[2], stack[3], stack[4], 0);
                    Relative(stack[5], 0, stack[6], stack[7], stack[8], y0 - y - stack[1] - stack[3] - stack[7]);
                    break;
                }

                case 37: {                                             // flex1
                    var x0 = x; var y0 = y;
                    float dx = 0, dy = 0;
                    for (var i = 0; i < 10; i += 2) { dx += stack[i]; dy += stack[i + 1]; }
                    Relative(stack[0], stack[1], stack[2], stack[3], stack[4], stack[5]);
                    Relative(stack[6], stack[7], stack[8], stack[9], x0 + dx + stack[10] - x, y0 + dy - y);
                    break;
                }

                default: break;
            }

            count = 0;
        }

        void Relative(float dxa, float dya, float dxb, float dyb, float dxc, float dyc) {
            var ax = x + dxa; var ay = y + dya;
            var bx = ax + dxb; var by = ay + dyb;
            x = bx + dxc; y = by + dyc;
            CurveTo(ax, ay, bx, by);
        }
    }
}
