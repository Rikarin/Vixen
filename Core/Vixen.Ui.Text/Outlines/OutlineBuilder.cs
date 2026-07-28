// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Ui.Text.Outlines;

/// <summary>Collects segments while a glyph is being read.</summary>
internal sealed class OutlineBuilder {
    readonly ImmutableArray<OutlineSegment>.Builder segments = ImmutableArray.CreateBuilder<OutlineSegment>(64);

    public bool IsEmpty => segments.Count == 0;

    public void Move(float x, float y) => segments.Add(new OutlineSegment(OutlineVerb.Move, x, y));

    public void Line(float x, float y) => segments.Add(new OutlineSegment(OutlineVerb.Line, x, y));

    public void Quadratic(float cx, float cy, float x, float y) =>
        segments.Add(new OutlineSegment(OutlineVerb.Quadratic, cx, cy, x, y));

    public void Cubic(float ax, float ay, float bx, float by, float x, float y) =>
        segments.Add(new OutlineSegment(OutlineVerb.Cubic, ax, ay, bx, by, x, y));

    public void Close() => segments.Add(new OutlineSegment(OutlineVerb.Close, 0, 0));

    public GlyphOutline Build() => segments.Count == 0 ? GlyphOutline.Empty : new GlyphOutline(segments.ToImmutable());
}
