// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Sprites;

/// <summary>
///     Many sprites on one texture: an atlas, a tile set, a character's frames.
/// </summary>
/// <remarks>
///     <para>
///         <b>What a sheet buys is one texture and one material for a hundred sprites</b>, which is
///         the difference between a hundred draws and one. The renderer never sees the sheet — it is
///         handed sprites, and two sprites cut from the same sheet share a material and therefore
///         share a descriptor set, which is what lets them batch. So this type is an authoring and
///         lookup convenience over a list, and deliberately nothing more.
///     </para>
///     <para>
///         ⚠ <b>The lookup is built once and frozen, not searched.</b> A sheet is read by name at
///         load time and often by name per frame — a state machine asking for "run_03" — and a linear
///         search over a few hundred sprites in a frame path is the kind of cost that shows up as a
///         profile with no obvious hot spot. Built lazily, because a sheet that is only ever indexed
///         by number should not pay for a dictionary it never asks a question of.
///     </para>
///     <para>
///         ⚠ <b>A class where <see cref="Sprite" /> is a record, and that cache is the reason.</b> A
///         record's generated equality compares every instance field including the private ones, so a
///         sheet that had answered a lookup would stop being equal to the identical sheet that had
///         not. Value equality over a hundred sprites was never worth having; being quietly wrong
///         about it would have been.
///     </para>
/// </remarks>
[DataContract("SpriteSheet")]
public sealed class SpriteSheet {
    FrozenDictionary<string, int>? lookup;

    /// <summary>What the sheet is called.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>How big the texture is, in texels.</summary>
    /// <remarks>
    ///     The same number every sprite in <see cref="Sprites" /> carries — see
    ///     <see cref="Sprite.TextureSize" /> for why it is in both places rather than only in this
    ///     one.
    /// </remarks>
    public Int2 TextureSize { get; init; }

    /// <summary>The sprites, in the order they were cut.</summary>
    /// <remarks>
    ///     ⚠ <b>An array rather than an <c>IReadOnlyList</c>, and the serializer is the reason.</b> A
    ///     member declared as an interface is written polymorphically — the concrete type's name
    ///     goes into the stream so a reader can rebuild it — and <c>List&lt;Sprite&gt;</c> has no
    ///     serialised name, so a sheet declared that way serialises for exactly as long as nobody
    ///     tries. Nothing here is polymorphic anyway: the elements of a sheet are sprites and can be
    ///     nothing else.
    /// </remarks>
    public Sprite[] Sprites { get; init; } = [];

    /// <summary>How many sprites the sheet holds.</summary>
    public int Count => Sprites.Length;

    /// <summary>One sprite by its position in the sheet.</summary>
    /// <param name="index">Which one.</param>
    /// <exception cref="ArgumentOutOfRangeException">There is no sprite there.</exception>
    public Sprite this[int index] => Sprites[index];

    /// <summary>One sprite by name.</summary>
    /// <param name="name">Its <see cref="Sprite.Name" />.</param>
    /// <returns>The sprite, or null if the sheet has no such name.</returns>
    /// <remarks>
    ///     Null rather than a throw, because a name comes from a state machine or a script and the
    ///     right answer to a frame that asks for a sprite nobody cut is to draw the last one — not to
    ///     take the frame down.
    /// </remarks>
    public Sprite? Find(string name) => IndexOf(name) is var index && index >= 0 ? Sprites[index] : null;

    /// <summary>Where a name sits in the sheet.</summary>
    /// <param name="name">The name.</param>
    /// <returns>Its index, or -1 if there is no such sprite.</returns>
    /// <remarks>
    ///     ⚠ <b>The first of a duplicated name wins, and duplicates are not refused.</b> A sheet is
    ///     content, and content that failed to load because two cells came out of an importer with
    ///     the same name would be a build broken by a naming convention.
    /// </remarks>
    public int IndexOf(string name) {
        ArgumentNullException.ThrowIfNull(name);

        lookup ??= Build();

        return lookup.TryGetValue(name, out var index) ? index : -1;
    }

    /// <summary>Cuts a texture into a grid of equally sized cells.</summary>
    /// <param name="name">What the sheet is called. Its sprites are named <c>name_0</c> upwards.</param>
    /// <param name="textureSize">How big the texture is, in texels.</param>
    /// <param name="cell">How big one cell is, in texels.</param>
    /// <param name="count">How many cells to take, or zero for as many as fit.</param>
    /// <param name="offset">Where the grid starts, in texels from the top-left.</param>
    /// <param name="padding">The gap between cells, in texels.</param>
    /// <param name="pivot">Where each sprite's origin sits, as a fraction from the bottom-left.</param>
    /// <param name="border">Each sprite's nine-slice border, in texels.</param>
    /// <param name="pixelsPerUnit">How many texels make one world unit.</param>
    /// <returns>The sheet.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A cell has no size.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Row-major from the top-left</b>, because that is the order a texture is laid out in
    ///         and therefore the order an animation's frames are drawn in. The alternative — bottom
    ///         up, matching the pivot's origin — would put frame zero of every walk cycle in the
    ///         bottom-left corner of the sheet, which is not where anybody draws it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A partial cell at the right or bottom edge is not taken.</b> A sheet whose texture
    ///         is not a whole number of cells wide is a mistake in the artwork or in the numbers, and
    ///         a half-width final column drawn as though it were whole is that mistake made invisible.
    ///     </para>
    /// </remarks>
    public static SpriteSheet Grid(
        string name,
        Int2 textureSize,
        Int2 cell,
        int count = 0,
        Int2 offset = default,
        Int2 padding = default,
        Vector2 pivot = default,
        NineSlice border = default,
        float pixelsPerUnit = Sprite.DefaultPixelsPerUnit
    ) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cell.X);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cell.Y);

        // A pivot of exactly zero is the bottom-left corner and a perfectly sensible thing to ask
        // for, so it cannot be the sentinel for "unspecified". It is the *default* of the parameter
        // that is the sentinel, and a caller who means the corner passes a value that says so.
        var origin = pivot == default ? new Vector2(0.5f, 0.5f) : pivot;

        var columns = Fit(textureSize.X, offset.X, cell.X, padding.X);
        var rows = Fit(textureSize.Y, offset.Y, cell.Y, padding.Y);
        var wanted = count > 0 ? Math.Min(count, columns * rows) : columns * rows;

        var sprites = new List<Sprite>(Math.Max(wanted, 0));

        for (var index = 0; index < wanted; index++) {
            var column = index % columns;
            var row = index / columns;

            sprites.Add(
                new() {
                    Name = $"{name}_{index}",
                    Region = new(
                        offset.X + (column * (cell.X + padding.X)),
                        offset.Y + (row * (cell.Y + padding.Y)),
                        cell.X,
                        cell.Y
                    ),
                    TextureSize = textureSize,
                    Pivot = origin,
                    Border = border,
                    PixelsPerUnit = pixelsPerUnit
                }
            );
        }

        return new() { Name = name, TextureSize = textureSize, Sprites = [.. sprites] };
    }

    /// <summary>How many whole cells fit along one axis.</summary>
    static int Fit(int extent, int offset, int cell, int padding) {
        var available = extent - offset;

        if (available < cell || cell <= 0) {
            return 0;
        }

        // The last cell has no padding after it, so the room is one padding more than the stride
        // divides — which is the off-by-one that costs a sheet its final column.
        return ((available + padding) / (cell + padding));
    }

    FrozenDictionary<string, int> Build() {
        var pairs = new Dictionary<string, int>(Sprites.Length, StringComparer.Ordinal);

        for (var index = 0; index < Sprites.Length; index++) {
            pairs.TryAdd(Sprites[index].Name, index);
        }

        return pairs.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
