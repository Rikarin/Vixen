// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Terrain;

public sealed partial class TerrainModule {
    /// <summary>The pictures this module's five file kinds show in the Project panel.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 36 § D6, from the other side of the door.</b> These are contributed by the module
    ///         that introduced the file kinds, through the registry a plugin writes to — so the Project
    ///         panel draws a terrain layer as three coloured bands without <c>Vixen.Editor.App</c>
    ///         knowing that terrain exists. The application's own set is <c>StandardIcons</c> and is
    ///         registered the same way.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Keyed on the extension rather than on an importer tag, and here that is the only
    ///         option.</b> All five of these are read by one importer — <c>TerrainAssetImporter</c>
    ///         claims every <c>.vx*</c> terrain file — so a tag would draw the same picture for a
    ///         heightfield and for a spline. See <c>AssetIcon</c>, which tries the tag first for the
    ///         cases where it is the better key.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Literal colours rather than theme tokens, deliberately.</b> A grid of file tiles is
    ///         scanned by colour and the colours are the subject matter — ground is brown, foliage is
    ///         green — which is exactly the case <c>IconPaint.Of</c> exists for. Anything that meant
    ///         "the accent" or "the warning colour" would want <c>IconPaint.Named</c> so it followed a
    ///         retheme.
    ///     </para>
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>Declared after the art it names, because a static initializer runs in source order.</b>
    ///     Above them this is five nulls, and the compiler says so rather than the Project panel
    ///     showing five blank tiles.
    /// </remarks>
    static IReadOnlyList<AssetIcon> TerrainIcons => [
        new(".vxterrain", Ground),
        new(".vxlayer", Layers),
        new(".vxfoliage", Tree),
        new(".vxgrass", Blades),
        new(".vxspline", Curve)
    ];

    /// <summary>Brown rock under a green skin, which is what a heightfield is.</summary>
    static IconArt Ground { get; } = new(
        new IconPath(
            Fill([new(2f, 20f), new(22f, 20f), new(22f, 13f), new(15f, 8f), new(9f, 13f), new(2f, 10f)]),
            IconPaint.Of(new Color4(0.55f, 0.42f, 0.30f, 1f))
        ),
        new IconPath(
            Fill([new(2f, 10f), new(9f, 13f), new(15f, 8f), new(22f, 13f), new(22f, 10f), new(15f, 5f), new(9f, 10f), new(2f, 7f)]),
            IconPaint.Of(new Color4(0.44f, 0.68f, 0.38f, 1f))
        )
    );

    /// <summary>Three bands, because a paint layer is one of a stack that sums to one.</summary>
    static IconArt Layers { get; } = new(
        new IconPath(Fill([new(3f, 6f), new(21f, 6f), new(21f, 10f), new(3f, 10f)]), IconPaint.Of(new Color4(0.83f, 0.62f, 0.35f, 1f))),
        new IconPath(Fill([new(3f, 11f), new(21f, 11f), new(21f, 15f), new(3f, 15f)]), IconPaint.Of(new Color4(0.44f, 0.68f, 0.38f, 1f))),
        new IconPath(Fill([new(3f, 16f), new(21f, 16f), new(21f, 20f), new(3f, 20f)]), IconPaint.Of(new Color4(0.48f, 0.60f, 0.78f, 1f)))
    );

    /// <summary>A trunk and a canopy.</summary>
    static IconArt Tree { get; } = new(
        new IconPath(Fill([new(11f, 13f), new(13f, 13f), new(13f, 21f), new(11f, 21f)]), IconPaint.Of(new Color4(0.48f, 0.36f, 0.26f, 1f))),
        new IconPath(
            new PathBuilder().AddEllipse(new Rectangle(5f, 3f, 14f, 12f)),
            IconPaint.Of(new Color4(0.36f, 0.62f, 0.34f, 1f))
        )
    );

    /// <summary>Three blades, in three greens, because a clump is never one colour.</summary>
    static IconArt Blades { get; } = new(
        new IconPath(Blade(7f, 6f), IconPaint.Of(new Color4(0.40f, 0.66f, 0.36f, 1f))),
        new IconPath(Blade(12f, 3f), IconPaint.Of(new Color4(0.52f, 0.76f, 0.42f, 1f))),
        new IconPath(Blade(17f, 7f), IconPaint.Of(new Color4(0.33f, 0.56f, 0.32f, 1f)))
    );

    /// <summary>A curve and the two points it is dragged by.</summary>
    static IconArt Curve { get; } = new(
        new IconPath(
            new PathBuilder().MoveTo(new Vector2(4f, 19f)).CubicTo(new(9f, 4f), new(15f, 20f), new(20f, 5f)),
            IconPaint.None,
            IconPaint.Of(new Color4(0.62f, 0.70f, 0.78f, 1f)),
            2f
        ),
        new IconPath(new PathBuilder().AddEllipse(new Rectangle(2f, 17f, 4f, 4f)), IconPaint.Of(new Color4(0.96f, 0.66f, 0.44f, 1f))),
        new IconPath(new PathBuilder().AddEllipse(new Rectangle(18f, 3f, 4f, 4f)), IconPaint.Of(new Color4(0.96f, 0.66f, 0.44f, 1f)))
    );

    /// <summary>A closed polygon through the points given.</summary>
    static PathBuilder Fill(IReadOnlyList<Vector2> points) {
        var path = new PathBuilder().MoveTo(points[0]);

        for (var index = 1; index < points.Count; index++) {
            path.LineTo(points[index]);
        }

        return path.Close();
    }

    /// <summary>One blade of grass, rooted at the bottom of the grid and leaning right.</summary>
    static PathBuilder Blade(float x, float top) =>
        new PathBuilder()
            .MoveTo(new Vector2(x, 21f))
            .QuadraticTo(new Vector2(x, top + 4f), new Vector2(x + 3f, top))
            .QuadraticTo(new Vector2(x + 1f, top + 5f), new Vector2(x + 1.6f, 21f))
            .Close();
}
