// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Sprites;

/// <summary>
///     A named region of a texture, and everything needed to draw it at the size it was drawn at.
/// </summary>
/// <remarks>
///     <para>
///         <b>Texels in, world units out.</b> Everything authored about a sprite is in the texture's
///         own pixels — where the region is, how wide its nine-slice borders are, where its pivot
///         sits — because that is what an artist sees and what an importer reads out of a sheet.
///         <see cref="PixelsPerUnit" /> is the one number that turns all of it into the units a scene
///         is measured in, and it is per sprite rather than per project so that a background painted
///         at a quarter of the resolution is a different sprite and not a different scene.
///     </para>
///     <para>
///         ⚠ <b>No texture handle, and that is not an omission.</b> What binds the atlas is the
///         material — the same <c>MaterialRenderFeature</c> a mesh goes through — so a sprite that
///         named a texture view would be naming it twice and would be a graphics type in a record
///         that is otherwise pure data. What it does need is <see cref="TextureSize" />, because that
///         is the denominator that turns a region in texels into the UVs a vertex carries, and no
///         amount of material binding supplies it.
///     </para>
///     <para>
///         ⚠ <b><see cref="TextureSize" /> is on the sprite even though the sheet has it too.</b> A
///         sprite reaches <c>SpriteRenderFeature</c> on its own — one sprite on one object, most of
///         the time — and a sprite that could not answer its own UVs without the sheet it was cut
///         from would make every consumer carry the pair. The duplication is a value copied into a
///         value; the alternative is a back-reference, which turns two records into a graph.
///     </para>
/// </remarks>
[DataContract("Sprite")]
public sealed record Sprite {
    /// <summary>How many texels make one world unit, when nobody says otherwise.</summary>
    /// <remarks>
    ///     A hundred, which is Unity's and therefore what most 2D artwork in existence is drawn
    ///     against. It is a default rather than a constant because the right answer is a property of
    ///     the art.
    /// </remarks>
    public const float DefaultPixelsPerUnit = 100f;

    /// <summary>What it is called, which is how a sheet is looked up.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Which part of the texture it is, in texels from the top-left.</summary>
    public Rectangle Region { get; init; }

    /// <summary>How big the whole texture is, in texels.</summary>
    public Int2 TextureSize { get; init; }

    /// <summary>Where the sprite's origin sits inside its region, as a fraction from the bottom-left.</summary>
    /// <remarks>
    ///     ⚠ <b>From the bottom-left, where <see cref="Region" /> is measured from the top-left.</b>
    ///     Two origins in one record reads like a mistake and is not: a region indexes a texture,
    ///     where V runs downward (<c>Conventions.md</c>), and a pivot is a point in the world, where
    ///     Y runs up. A pivot measured the texture's way would put a character's feet at the top of
    ///     the quad, and would do it consistently enough to look deliberate.
    /// </remarks>
    public Vector2 Pivot { get; init; } = new(0.5f, 0.5f);

    /// <summary>How far the nine-slice corners reach in, in texels. Empty for a plain sprite.</summary>
    public NineSlice Border { get; init; }

    /// <summary>How many texels make one world unit.</summary>
    public float PixelsPerUnit { get; init; } = DefaultPixelsPerUnit;

    /// <summary>How big the sprite is in world units at its authored size.</summary>
    public Vector2 Size => new(Region.Width / Scale, Region.Height / Scale);

    /// <summary>Where the region is in UVs, which is what a vertex carries.</summary>
    /// <remarks>
    ///     A texture with no size gives the whole of it rather than a division by zero: a sprite whose
    ///     importer has not filled in the dimensions yet should draw the picture it was pointed at, not
    ///     an infinity.
    /// </remarks>
    public Rectangle Uv =>
        TextureSize.X <= 0 || TextureSize.Y <= 0
            ? new(0f, 0f, 1f, 1f)
            : new(
                Region.X / TextureSize.X,
                Region.Y / TextureSize.Y,
                Region.Width / TextureSize.X,
                Region.Height / TextureSize.Y
            );

    /// <summary>The nine-slice border in UVs, which is how <see cref="Uv" /> is cut.</summary>
    public NineSlice UvBorder =>
        TextureSize.X <= 0 || TextureSize.Y <= 0
            ? default
            : Border.Scaled(1f / TextureSize.X, 1f / TextureSize.Y);

    /// <summary>The nine-slice border in world units, which is how <see cref="Size" /> is cut.</summary>
    /// <remarks>
    ///     ⚠ <b>Scaled by the pixel density and by nothing else</b>, which is the whole promise of a
    ///     nine-slice: a sixteen-texel corner is the same size in the world whatever the panel it is
    ///     the corner of. It is <see cref="NineSlice.Fit" /> that shrinks it, and only when the panel
    ///     is smaller than its own two corners together.
    /// </remarks>
    public NineSlice UnitBorder => Border.Scaled(1f / Scale, 1f / Scale);

    /// <summary>Whether this sprite is drawn as nine cells rather than one quad.</summary>
    public bool IsSliced => !Border.IsEmpty;

    /// <summary>Whether there is anything to draw at all.</summary>
    public bool IsDrawable => Region.Width > 0f && Region.Height > 0f;

    /// <summary>The density, floored away from zero so nothing divides by it.</summary>
    float Scale => PixelsPerUnit > 0f ? PixelsPerUnit : DefaultPixelsPerUnit;
}
