// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;

namespace Vixen.Ai.Diagnostics;

/// <summary>How a query's points are drawn.</summary>
/// <remarks>
///     ⚠ <b><c>default</c> is the quiet style</b>, for the reason <see cref="AiOverlayStyle" />'s
///     remarks give: a struct's property initialisers do not run for <c>default</c>, so a zeroed style
///     draws nothing at all. <see cref="Default" /> is the usual one and it is <c>new()</c>.
/// </remarks>
public readonly record struct QueryPreviewStyle {
    /// <summary>How big a point's marker is, in metres.</summary>
    public const float DefaultSize = 0.2f;

    /// <summary>The usual style.</summary>
    public static QueryPreviewStyle Default => new();

    /// <summary>How big a point's marker is, in metres. Zero means the usual.</summary>
    public float Size { get; init; } = DefaultSize;

    /// <summary>Whether the points a filter rejected are drawn at all.</summary>
    /// <remarks>
    ///     ⚠ <b>On, and it is the whole value of the preview.</b> "Why is my query returning nothing"
    ///     is answered by seeing where the points were and that they were all crossed out, and a
    ///     preview that only drew survivors would answer it with an empty screen.
    /// </remarks>
    public bool Rejected { get; init; } = true;

    /// <summary>Whether each surviving point is labelled with its score.</summary>
    public bool Scores { get; init; } = true;

    /// <summary>Whether the winner gets a ring around it.</summary>
    public bool Winner { get; init; } = true;

    /// <summary>The colour of a point that scored nothing.</summary>
    public Color4 Worst { get; init; } = new(1f, 0.35f, 0.3f, 1f);

    /// <summary>The colour of a point that scored one.</summary>
    public Color4 Best { get; init; } = new(0.35f, 1f, 0.45f, 1f);

    /// <summary>The colour of a point a filter rejected.</summary>
    public Color4 Filtered { get; init; } = new(0.45f, 0.45f, 0.5f, 1f);

    /// <summary>Creates the usual style.</summary>
    public QueryPreviewStyle() {
    }

    /// <summary>How big a marker actually is, with a zeroed style answering the usual size.</summary>
    public float Extent => Size > 0f ? Size : DefaultSize;

    /// <summary>What colour a score reads as.</summary>
    /// <param name="score">The score, in <c>[0,1]</c>.</param>
    /// <returns>The colour.</returns>
    public Color4 ColourOf(float score) => Color4.Lerp(Worst, Best, MathUtil.Saturate(score));
}

/// <summary>
///     Draws the points an environment query produced: green through red by score, with the ones a
///     filter rejected crossed out.
/// </summary>
/// <remarks>
///     <para>
///         doc 37 § Part 5's environment-query preview — <i>Unreal's testing pawn, minus the pawn</i>.
///         It takes a <see cref="QueryResults" /> and nothing else, so the same call draws the
///         editor's preview run and a running agent's last query with no second implementation
///         between them.
///     </para>
///     <para>
///         ⚠ <b>Lines and labels, so it is asserted by a test with no window</b> — the same bargain
///         <see cref="AiGameplayDebugger" /> makes and the reason both of them live here rather than
///         in the editor.
///     </para>
/// </remarks>
public static class QueryPreview {
    /// <summary>Draws a run.</summary>
    /// <param name="draw">Where the geometry goes.</param>
    /// <param name="results">What the run produced.</param>
    /// <param name="style">How to draw it, or the usual style.</param>
    /// <returns>How many points were drawn.</returns>
    /// <exception cref="ArgumentNullException">Either of the first two arguments is null.</exception>
    public static int Draw(DebugDraw draw, QueryResults results, QueryPreviewStyle style = default) {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(results);

        if (!draw.Enabled || results.Count == 0) {
            return 0;
        }

        if (style == default) {
            style = QueryPreviewStyle.Default;
        }

        var size = style.Extent;
        var drawn = 0;
        var index = 0;

        foreach (var point in results.Points) {
            var at = point.Position;

            if (point.Filtered) {
                if (style.Rejected) {
                    // A cross rather than a dot, and it reads as one from any angle: two diagonals in
                    // the ground plane are unmistakably "not this one" beside a marker that is not.
                    draw.Line(at + new Vector3(-size, 0f, -size), at + new Vector3(size, 0f, size), style.Filtered);
                    draw.Line(at + new Vector3(-size, 0f, size), at + new Vector3(size, 0f, -size), style.Filtered);
                    drawn++;
                }

                index++;

                continue;
            }

            var colour = style.ColourOf(point.Score);

            draw.Line(at, at + (Vector3.UnitY * (size + (point.Score * size * 4f))), colour);
            draw.Circle(at, Vector3.UnitY, size, colour);

            if (style.Scores) {
                draw.Text(
                    at + (Vector3.UnitY * (size * 1.5f)),
                    point.Score.ToString("0.##", CultureInfo.InvariantCulture),
                    colour,
                    size
                );
            }

            if (style.Winner && index == results.Best) {
                draw.Circle(at, Vector3.UnitY, size * 2.5f, colour);
            }

            drawn++;
            index++;
        }

        return drawn;
    }
}
