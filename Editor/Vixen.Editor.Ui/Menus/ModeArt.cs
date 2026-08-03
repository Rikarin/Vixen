// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Ui;

/// <summary>The coloured pictures on the mode strip.</summary>
/// <remarks>
///     <para>
///         <b>Four destinations, drawn to be told apart at a glance.</b> A mode is the biggest choice
///         on screen — it decides what a click in the viewport means — and the strip had been four
///         text buttons, which is a row somebody reads every time rather than aims at.
///     </para>
///     <para>
///         ⚠ <b>Colour, and a *different* colour each.</b> Recognising one of four is a colour task
///         before it is a shape task; four outlines in the theme's foreground make the strip a
///         reading exercise however good the shapes are. The hues are the ones each mode's own
///         subsystem already uses — terrain's earth, foliage's green — so the strip agrees with the
///         panels it opens.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in each mode's own assembly, and that is a deliberate exception.</b>
///         Doc 36 § D6 has a type declare its own icon, and a plugin's mode must do exactly that.
///         These four ship together, are chosen against each other, and are the one set where "do
///         these look like one family" is the whole requirement — splitting them across four
///         assemblies is how the family drifts. A plugin's mode sets <c>IEditorMode.Art</c> from
///         wherever it likes.
///     </para>
/// </remarks>
public static class ModeArt {
    static readonly Color4 Steel = new(0.42f, 0.62f, 0.92f, 1f);
    static readonly Color4 Slate = new(0.62f, 0.68f, 0.78f, 1f);
    static readonly Color4 Clay = new(0.86f, 0.52f, 0.32f, 1f);
    static readonly Color4 Earth = new(0.72f, 0.56f, 0.36f, 1f);
    static readonly Color4 Grass = new(0.44f, 0.72f, 0.36f, 1f);
    static readonly Color4 Bark = new(0.54f, 0.40f, 0.28f, 1f);

    /// <summary>An arrow cursor: pick things and move them.</summary>
    public static IconArt Select { get; } = new(
        new IconPath(
            new PathBuilder()
                .MoveTo(new Vector2(5f, 3f))
                .LineTo(new Vector2(5f, 18f))
                .LineTo(new Vector2(9f, 14f))
                .LineTo(new Vector2(12f, 20f))
                .LineTo(new Vector2(15f, 18.5f))
                .LineTo(new Vector2(12f, 13f))
                .LineTo(new Vector2(17f, 12f))
                .Close(),
            IconPaint.Of(Steel)
        )
    );

    /// <summary>A box with its top face lit: build the shape of a level out of solids.</summary>
    public static IconArt Blockout { get; } = new(
        new IconPath(
            new PathBuilder()
                .MoveTo(new Vector2(12f, 3f))
                .LineTo(new Vector2(20f, 7.5f))
                .LineTo(new Vector2(12f, 12f))
                .LineTo(new Vector2(4f, 7.5f))
                .Close(),
            IconPaint.Of(Clay)
        ),
        new IconPath(
            new PathBuilder()
                .MoveTo(new Vector2(4f, 7.5f))
                .LineTo(new Vector2(12f, 12f))
                .LineTo(new Vector2(12f, 21f))
                .LineTo(new Vector2(4f, 16.5f))
                .Close(),
            IconPaint.Of(Slate)
        ),
        new IconPath(
            new PathBuilder()
                .MoveTo(new Vector2(20f, 7.5f))
                .LineTo(new Vector2(20f, 16.5f))
                .LineTo(new Vector2(12f, 21f))
                .LineTo(new Vector2(12f, 12f))
                .Close(),
            IconPaint.Of(new Color4(0.48f, 0.54f, 0.64f, 1f))
        )
    );

    /// <summary>Two hills with a sky above them: shape the ground.</summary>
    public static IconArt Terrain { get; } = new(
        new IconPath(
            new PathBuilder()
                .MoveTo(new Vector2(2f, 20f))
                .LineTo(new Vector2(9f, 9f))
                .LineTo(new Vector2(14f, 16f))
                .LineTo(new Vector2(17f, 12f))
                .LineTo(new Vector2(22f, 20f))
                .Close(),
            IconPaint.Of(Earth)
        ),
        new IconPath(
            new PathBuilder()
                .MoveTo(new Vector2(9f, 9f))
                .LineTo(new Vector2(12.5f, 14f))
                .LineTo(new Vector2(5.5f, 14f))
                .Close(),
            IconPaint.Of(Grass)
        )
    );

    /// <summary>A tree: scatter what grows on the ground the mode beside it makes.</summary>
    public static IconArt Foliage { get; } = new(
        new IconPath(
            new PathBuilder()
                .MoveTo(new Vector2(12f, 3f))
                .LineTo(new Vector2(18f, 13f))
                .LineTo(new Vector2(6f, 13f))
                .Close(),
            IconPaint.Of(Grass)
        ),
        new IconPath(
            new PathBuilder()
                .MoveTo(new Vector2(12f, 8f))
                .LineTo(new Vector2(19.5f, 18f))
                .LineTo(new Vector2(4.5f, 18f))
                .Close(),
            IconPaint.Of(new Color4(0.34f, 0.62f, 0.30f, 1f))
        ),
        new IconPath(new PathBuilder().AddRectangle(new Rectangle(10.8f, 18f, 2.4f, 3.5f)), IconPaint.Of(Bark))
    );
}
