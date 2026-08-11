// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>What kind of virtual map a level is.</summary>
public enum VirtualShadowKind {
    /// <summary>One level of a directional light's clipmap, orthographic and centred on the camera.</summary>
    Clipmap = 0,

    /// <summary>A spot light's single perspective map.</summary>
    Spot = 1
}

/// <summary>
///     One virtual map: a projection, where its pages start, and how coarse its texels are.
/// </summary>
/// <remarks>
///     <para>
///         <b>A clipmap level and a spot light are the same record</b>, which is what keeps the page
///         table, the marking pass and the lookup from having two of everything. What differs is how a
///         pixel <em>chooses</em> one — a clipmap level is chosen by comparing texel sizes, a spot's
///         only level is chosen by being inside its frustum — and that is one branch in one function
///         rather than a second address space.
///     </para>
///     <para>
///         Sixteen-byte rows on both sides, as every record a shader reads is. The matrix is first
///         because it is the only member with an alignment worth arranging around.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VirtualShadowLevel {
    /// <summary>World to this map's clip space.</summary>
    public Matrix4x4 ViewProjection;

    /// <summary>Where this map's pages start in the global page numbering.</summary>
    public uint First;

    /// <summary><see cref="VirtualShadowKind" />, as the shader reads it.</summary>
    public uint Kind;

    /// <summary>
    ///     How many world units one of this map's shadow texels covers. Zero for a spot.
    /// </summary>
    /// <remarks>
    ///     What the clipmap's level selection compares against, and the only reason a level carries a
    ///     number the projection does not already imply: extracting it from an orthographic matrix is a
    ///     division a shader would do per pixel to recover a constant the host computed once.
    /// </remarks>
    public float TexelWorldSize;

    /// <summary>Which light this map belongs to, for a host that wants to attribute a page.</summary>
    public uint Light;
}

/// <summary>
///     The virtual shadow map's address space: clipmap levels, pages, and the arithmetic that turns a
///     world position into one.
/// </summary>
/// <remarks>
///     <para>
///         <b>Phase 7 of <c>docs/plan/22-virtualized-geometry.md</c>, and the reason it exists at all.</b>
///         A Nanite-class scene defeats cascades: the geometry is detailed enough that the cascade's
///         own resolution becomes the visible limit, and there is no cascade count that fixes it —
///         four maps over a whole frustum is four fixed resolutions, and the one a pixel gets is
///         whichever slice it landed in. A virtual map inverts that: the resolution is chosen
///         <em>per pixel</em> from what that pixel actually needs, and only the pages some pixel asked
///         for are ever allocated or drawn.
///     </para>
///     <para>
///         <b>Pure arithmetic with no device and no state</b>, on <see cref="ShadowCascades" />'
///         terms and for the sharper version of its reason: every way a virtual shadow map goes wrong
///         is a property of these functions. A level chosen one step too coarse is a soft shadow
///         nobody can attribute; a page snapped to the wrong grid is a shadow that crawls when the
///         camera moves; a page index that disagrees between the marking pass and the lookup is a
///         pixel reading another part of the world's depth, which looks like a shadow of something
///         that is not there. All three are assertable without rendering anything.
///     </para>
///     <para>
///         <b>Snapped to whole pages, not to texels, and that is what makes a page cacheable.</b>
///         A cascade snaps its centre to a texel so the sampling grid does not slide; a clipmap level
///         snaps to a <em>page</em> so that a camera moving less than a page leaves every page's world
///         footprint bit-identical — which is the whole of why a page already drawn does not have to
///         be drawn again. Snapping to a texel would leave the page boundaries sliding and every page
///         stale every frame, which is a virtual shadow map with none of the point of one.
///     </para>
/// </remarks>
public static class VirtualShadowMap {
    /// <summary>How many texels a page is on a side.</summary>
    /// <remarks>
    ///     A hundred and twenty-eight, which is what Nanite settled on and for a reason that is about
    ///     the page table rather than the raster: a 4096-texel level is 32 pages a side and 1024 page
    ///     entries, so a whole clipmap's table is a few kilobytes and can be read back per frame. Half
    ///     the size quadruples the table for a quarter of the wasted margin around each request.
    /// </remarks>
    public const int PageTexels = 128;

    /// <summary>How many pages a virtual map is on a side.</summary>
    /// <remarks>
    ///     Thirty-two, so a map is 4096 texels square. The clipmap's <em>levels</em> are what give it
    ///     range, not one enormous level: doubling the extent per level reaches a kilometre in eight
    ///     levels at the same texel density, where one 32k-square map would be a page table nobody can
    ///     read back.
    /// </remarks>
    public const int PagesPerSide = 32;

    /// <summary>How many pages one virtual map holds.</summary>
    public const int PagesPerMap = PagesPerSide * PagesPerSide;

    /// <summary>The most maps — clipmap levels plus spot lights — one frame may address.</summary>
    /// <remarks>
    ///     Sixteen, which is a clipmap of eight and eight shadowed spots, or any other split. A
    ///     ceiling rather than a budget: the page table is one word per virtual page, so this is
    ///     sixteen thousand entries and sixty-four kilobytes whether or not a frame fills it.
    /// </remarks>
    public const int MaxLevels = 16;

    /// <summary>How many virtual pages the whole address space holds.</summary>
    public const int MaxPages = MaxLevels * PagesPerMap;

    /// <summary>How many pixels on a side one marking invocation reads.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Not a sampling rate — every pixel of the block is still read.</b> What a block buys
    ///         is that the sixteen pixels in it almost always want the same word of the mark bitset, so
    ///         they coalesce into one <c>atomicOr</c> in a register instead of sixteen writes onto a
    ///         word every other block is also writing. Measured in sample 13, a frame marks a hundred
    ///         and thirteen pages spread over twenty-three words, so a per-pixel dispatch was putting a
    ///         quarter of a million read-modify-writes onto each of two dozen addresses.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sampling one pixel per block instead loses most of the pages, and no block size
    ///         makes that safe.</b> A page is <see cref="PageTexels" /> texels a side and
    ///         <see cref="LevelFor" /> picks a level whose texels are no finer than the pixel's, so a
    ///         page is at least that many pixels across — but only on a surface facing the camera. The
    ///         bound scales with the cosine of the obliquity and goes to zero on a floor receding to
    ///         the horizon. Measured: a four-by-four block reading only its centre marks forty pages a
    ///         frame in sample 13 against a hundred and thirteen for all sixteen, and most of what it
    ///         drops is in the middle of a marked run rather than at its edge. The pixels that lose the
    ///         page are exactly the ones whose lookup wants it.
    ///     </para>
    /// </remarks>
    public const int MarkBlock = 4;

    /// <summary>How many pixels on a side one marking workgroup covers.</summary>
    /// <remarks>
    ///     The workgroup is sixty-four invocations laid out eight by eight, so it covers eight blocks
    ///     each way. <c>VirtualShadowMark</c>'s <c>Main</c> derives the same number from
    ///     <c>Vsm.MarkBlock</c> and its own group index; a host that dispatched a different tiling would
    ///     leave a strip of the screen unmarked, which is a shadow missing along one edge.
    /// </remarks>
    public const int MarkTile = MarkBlock * 8;

    /// <summary>What the page table holds for a virtual page with no physical page behind it.</summary>
    /// <remarks>
    ///     All ones rather than zero, for <see cref="GpuClusterVisibility.PageAbsent" />'s reason: zero
    ///     is a real physical page — the first one — so a table cleared to zero says every virtual page
    ///     in the scene is backed by the same physical one. That draws the whole world's shadow out of
    ///     one page's depths, in all the right places, which reads as a corrupt atlas rather than as an
    ///     uninitialised table.
    /// </remarks>
    public const uint PageAbsent = 0xFFFFFFFFu;

    /// <summary>
    ///     How many world units one screen texel covers at a distance.
    /// </summary>
    /// <param name="distance">How far in front of the camera the pixel is.</param>
    /// <param name="screenHeightScale">
    ///     <see cref="RenderView.ScreenHeightScale" />: <c>1 / tan(fov / 2)</c>.
    /// </param>
    /// <param name="screenHeight">How many pixels tall the view is.</param>
    /// <remarks>
    ///     The quantity the whole clipmap is chosen by, and it is the same one
    ///     <see cref="GpuClusterCulling.ErrorScaleFor(float, int)" /> is built from — a pixel's world
    ///     footprint. That the level selection and the cluster cut are driven by one number is not a
    ///     coincidence worth breaking: a shadow finer than the geometry casting it is resolution spent
    ///     on an edge that is not there.
    /// </remarks>
    public static float WorldTexelSize(float distance, float screenHeightScale, int screenHeight) =>
        screenHeightScale <= 0f || screenHeight <= 0
            ? 0f
            : 2f * MathF.Max(distance, 0f) / (screenHeightScale * screenHeight);

    /// <summary>How close to the light a clipmap level's box begins.</summary>
    /// <remarks>
    ///     Not zero, because an orthographic projection with a near plane of zero has no depth range
    ///     to normalise against. It is a constant rather than a knob for
    ///     <see cref="DepthScale" />'s sake: the metres-to-normalised-depth conversion is a function
    ///     of the level's box alone, and a near plane a caller could move would be a second number a
    ///     bias has to be kept in step with.
    /// </remarks>
    public const float ClipmapNear = 0.0625f;

    /// <summary>One metre along the light, in a clipmap level's normalised depth.</summary>
    /// <param name="depthRange">How deep the level's box is along the light, in world units.</param>
    /// <remarks>
    ///     <para>
    ///         <b><c>ShadowCascade.depthScale</c>'s counterpart, and it exists for the identical
    ///         reason.</b> A level's box is <paramref name="depthRange" /> metres deep — four hundred
    ///         by default — so one unit of normalised depth is four hundred metres of world, and a
    ///         bias stated as a raw normalised number is not a distance at all. The cascade path
    ///         learned that the expensive way and records it at length on
    ///         <c>ShadowCascade.depthScale</c>: a constant in normalised depth is centimetres in one
    ///         projection and metres in the next.
    ///     </para>
    ///     <para>
    ///         ⚠ The virtual map ran without this for its whole life, and its default biases —
    ///         0.002 and 0.004 in normalised depth — were <em>0.8 m and 1.6 m per unit of slope</em>
    ///         against a four hundred metre box, a hundred times the cascades' own. Where a page
    ///         answered, every contact shadow within a metre or so of its caster was biased away;
    ///         where no page had been drawn the cascade kept it. That is two estimates of one
    ///         quantity that cannot agree, and a page arriving is then a shadow going out.
    ///     </para>
    /// </remarks>
    public static float DepthScale(float depthRange) =>
        1f / MathF.Max(depthRange - ClipmapNear, 1e-3f);

    /// <summary>How wide one clipmap level is, in world units.</summary>
    /// <param name="level">Which level, zero being the finest.</param>
    /// <param name="firstExtent">How wide level zero is.</param>
    public static float ExtentOf(int level, float firstExtent) =>
        firstExtent * (1 << Math.Clamp(level, 0, 30));

    /// <summary>How many world units one of a level's texels covers.</summary>
    public static float TexelOf(int level, float firstExtent) =>
        ExtentOf(level, firstExtent) / (PagesPerSide * PageTexels);

    /// <summary>
    ///     Which clipmap level a pixel needs, from the world size of its screen texel.
    /// </summary>
    /// <param name="worldTexelSize">What <see cref="WorldTexelSize" /> answered.</param>
    /// <param name="firstExtent">How wide level zero is.</param>
    /// <param name="levels">How many levels the clipmap has.</param>
    /// <returns>The finest level whose texels are no finer than the pixel's, clamped.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>No finer, rather than nearest.</b> A level finer than the pixel needs is not wrong
    ///         to look at — it is wasted pages, and a page is the unit this system is trying to spend
    ///         carefully. A level coarser than the pixel needs is a soft edge, which is the artefact.
    ///         So the rounding is upward, and the clamp at the bottom is what a pixel closer than
    ///         level zero was sized for gets: the finest map there is, and an edge as sharp as it can
    ///         be made.
    ///     </para>
    ///     <para>
    ///         A pixel beyond the last level is <em>not</em> clamped into it here — the caller gets an
    ///         index past the end and treats it as unshadowed, because a clipmap that ran out of range
    ///         has no depth for that pixel and pretending otherwise samples a page fitted somewhere
    ///         else entirely.
    ///     </para>
    /// </remarks>
    public static int LevelFor(float worldTexelSize, float firstExtent, int levels) {
        if (worldTexelSize <= 0f || firstExtent <= 0f) {
            return 0;
        }

        var ratio = worldTexelSize / TexelOf(0, firstExtent);

        if (ratio <= 1f) {
            return 0;
        }

        var level = (int)MathF.Ceiling(MathF.Log2(ratio));

        return Math.Min(level, Math.Max(levels, 1) - 1);
    }

    /// <summary>
    ///     One clipmap level's orthographic projection, snapped to its own page grid.
    /// </summary>
    /// <param name="level">Which level.</param>
    /// <param name="firstExtent">How wide level zero is.</param>
    /// <param name="camera">Where the camera is — what the clipmap is centred on.</param>
    /// <param name="lightDirection">The direction light travels, toward the scene.</param>
    /// <param name="depth">How deep the level's box is along the light, which is its whole caster range.</param>
    /// <remarks>
    ///     <para>
    ///         <b>Snapped to a whole page, and that is the load-bearing line.</b> Every page's world
    ///         footprint is then a function of the level and the snapped centre alone, so a camera
    ///         that moved less than a page leaves every page exactly where it was — which is what lets
    ///         a page already drawn stay drawn. A texel snap, which is what a cascade does, would leave
    ///         the boundaries sliding and invalidate the whole level every frame.
    ///     </para>
    ///     <para>
    ///         The basis is the light's alone and never the camera's, for
    ///         <see cref="ShadowCascades.Fit" />'s reason: a basis that followed the camera would turn
    ///         the shadow map under stationary geometry every time the player looked around.
    ///     </para>
    /// </remarks>
    public static Matrix4x4 ClipmapProjection(
        int level,
        float firstExtent,
        Vector3 camera,
        Vector3 lightDirection,
        float depth
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(firstExtent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);

        var light = Vector3.Normalize(lightDirection);
        var (right, up, reference) = Basis(light);

        var extent = ExtentOf(level, firstExtent);
        var page = extent / PagesPerSide;

        // Snapped along the light's own axes, all three — the two the page grid is made of because a
        // page must not slide, and the third because an unsnapped near plane changes the matrix when
        // nothing visible does, which stops "the projection is identical" being something a test can
        // state. ShadowCascades.Fit makes the same argument about the same third axis.
        var centre =
            (right * Snap(Vector3.Dot(camera, right), page))
            + (up * Snap(Vector3.Dot(camera, up), page))
            + (light * Snap(Vector3.Dot(camera, light), page));

        var origin = centre - (light * (depth * 0.5f));
        var view = Matrix4x4.LookAt(origin, centre, reference);

        var half = extent * 0.5f;
        var projection = Matrix4x4.OrthographicOffCenter(-half, half, -half, half, ClipmapNear, depth);

        return view * projection;
    }

    /// <summary>A light direction snapped to an angular grid, so a drifting sun refits rarely.</summary>
    /// <param name="direction">The direction light travels, toward the scene.</param>
    /// <param name="degrees">The grid's step. Zero or less snaps nothing and only normalizes.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The page snap's missing half.</b> <see cref="ClipmapProjection" /> snaps the
    ///         <em>centre</em> to a whole page so a camera moving less than one leaves every level's
    ///         matrix bit-identical — but the light direction enters that matrix raw, so a sun that
    ///         moves at all changes every level's projection every frame. Each change invalidates every
    ///         resident page of every level, the redraw budget never catches up, and the table is wiped
    ///         by the refit before the pages drawn against the previous fit were ever uploaded as
    ///         sampleable. Measured in sample 13 under its orbiting sun: five hundred and thirty-one
    ///         pages perpetually owed a draw, forty-eight ever published, and the map answering nothing
    ///         while allocating sixteen pages a frame — a stall dressed as work.
    ///     </para>
    ///     <para>
    ///         Snapped in azimuth and elevation rather than by component, because the cost being bounded
    ///         is angular: the step is how far the fitted light may lag the true one, and the refit
    ///         cadence is how long the budget has to redraw a level before it moves again. Between
    ///         snaps the pages and the lookup share the fitted matrices, so the shadows stay
    ///         self-consistent — what the lag costs is the map's shadow direction trailing the shading's
    ///         by at most the step, which at half a degree is inside what the bias already forgives.
    ///     </para>
    /// </remarks>
    public static Vector3 SnapDirection(Vector3 direction, float degrees) {
        var unit = Vector3.Normalize(direction);

        if (degrees <= 0f) {
            return unit;
        }

        var step = degrees * (MathF.PI / 180f);
        var azimuth = MathF.Round(MathF.Atan2(unit.Z, unit.X) / step) * step;
        var elevation = MathF.Round(MathF.Asin(Math.Clamp(unit.Y, -1f, 1f)) / step) * step;
        var planar = MathF.Cos(elevation);

        return new(MathF.Cos(azimuth) * planar, MathF.Sin(elevation), MathF.Sin(azimuth) * planar);
    }

    /// <summary>A light's own basis, which the camera never enters into.</summary>
    /// <remarks>
    ///     <see cref="ShadowCascades.Fit" />'s, extracted because two places deriving a light basis is
    ///     two places for a shadow to rotate under stationary geometry.
    /// </remarks>
    public static (Vector3 Right, Vector3 Up, Vector3 Reference) Basis(Vector3 lightDirection) {
        var light = Vector3.Normalize(lightDirection);
        var reference = new Vector3(0f, 1f, 0f);

        if (MathF.Abs(Vector3.Dot(light, reference)) > 0.99f) {
            reference = new(0f, 0f, 1f);
        }

        var right = Vector3.Normalize(Vector3.Cross(reference, -light));

        return (right, Vector3.Cross(-light, right), reference);
    }

    /// <summary>Where a world position lands in a map's page grid, and whether it lands in it at all.</summary>
    /// <param name="viewProjection">The map's projection.</param>
    /// <param name="world">The position.</param>
    /// <param name="page">Its page, in grid coordinates, when this returns true.</param>
    /// <returns>Whether the position is inside the map's own volume.</returns>
    /// <remarks>
    ///     <b>Containment and not merely a coordinate</b>, which is the same distinction
    ///     <c>ClusteredShading.CascadeContaining</c> records as a defect it carried for its whole life:
    ///     a position outside the map projects to a coordinate that is a perfectly ordinary number, and
    ///     using it reads a page fitted somewhere else. The depth range is tested too, because a caster
    ///     behind the map's near plane is a position the projection wraps around rather than clamps.
    /// </remarks>
    public static bool PageOf(in Matrix4x4 viewProjection, Vector3 world, out Int2 page) {
        page = default;

        var clip = Matrix4x4.TransformVector4(new(world, 1f), viewProjection);

        if (clip.W <= GpuCulling.ClipEpsilon) {
            return false;
        }

        var ndc = new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);

        if (ndc.Z < 0f || ndc.Z > 1f) {
            return false;
        }

        // The engine's one convention about which way up a clip space is — see Transform.NdcToUv,
        // which the marking shader and the lookup both go through. A page grid that disagreed with it
        // would be flipped in y, which is a shadow map that is right along one axis.
        var u = (ndc.X * 0.5f) + 0.5f;
        var v = (-ndc.Y * 0.5f) + 0.5f;

        if (u < 0f || u >= 1f || v < 0f || v >= 1f) {
            return false;
        }

        page = new((int)(u * PagesPerSide), (int)(v * PagesPerSide));

        return true;
    }

    /// <summary>A virtual page's index in the global numbering.</summary>
    /// <param name="first">The map's <see cref="VirtualShadowLevel.First" />.</param>
    /// <param name="page">Its page, in grid coordinates.</param>
    public static int IndexOf(uint first, Int2 page) =>
        (int)first + (page.Y * PagesPerSide) + page.X;

    /// <summary>Where a physical page sits in the atlas, in texels.</summary>
    /// <param name="slot">Which physical page.</param>
    /// <param name="pagesPerSide">How many pages the atlas is on a side.</param>
    public static Int2 AtlasOrigin(int slot, int pagesPerSide) =>
        new(slot % pagesPerSide * PageTexels, slot / pagesPerSide * PageTexels);

    /// <summary>
    ///     The projection that renders one virtual page into the whole of a viewport.
    /// </summary>
    /// <param name="viewProjection">The map's own projection.</param>
    /// <param name="page">Which page of it, in grid coordinates.</param>
    /// <remarks>
    ///     <para>
    ///         A page is a sub-rectangle of the map's clip space, and a draw into a physical page has
    ///         the whole viewport — so the page's rectangle is scaled up to fill it.
    ///         <see cref="ShadowProjections.Tile" /> is the same fold a cascade atlas does, at the
    ///         opposite ratio: a cascade shrinks a whole map into a tile, and this grows a page into a
    ///         whole target.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The y translation is not the x translation</b>, because the UV convention negates
    ///         y and does not negate x — the mistake <see cref="ShadowCascades.AtlasProjection" />
    ///         records having shipped for four days. It is written out here rather than expressed as
    ///         <see cref="ShadowProjections.Tile" /> with a reciprocal scale: the two folds are exact
    ///         inverses, and inverting one through the other is arithmetic nobody can check by reading.
    ///         What keeps them honest instead is a test that composes the pair and gets the identity.
    ///     </para>
    /// </remarks>
    public static Matrix4x4 PageProjection(in Matrix4x4 viewProjection, Int2 page) {
        const float N = PagesPerSide;

        var window = new Matrix4x4(
            N, 0f, 0f, 0f,
            0f, N, 0f, 0f,
            0f, 0f, 1f, 0f,
            N - 1f - (2f * page.X), (2f * page.Y) + 1f - N, 0f, 1f
        );

        return viewProjection * window;
    }

    /// <summary>The last whole multiple of a grid step at or below a value.</summary>
    static float Snap(float value, float step) => step <= 0f ? value : MathF.Floor(value / step) * step;
}
