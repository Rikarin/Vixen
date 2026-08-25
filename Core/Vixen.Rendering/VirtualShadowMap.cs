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

    /// <summary>Where this map's page grid sits in the light's own endless page grid.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What makes a page's identity survive the level sliding under it</b>, and the whole of
    ///         task #317. A level's window is <see cref="VirtualShadowMap.PagesPerSide" /> pages across
    ///         and its cell in the light's grid is <see cref="VirtualShadowMap.ClipmapCell" />; a camera
    ///         that walks one page moves the window by one, so the page that was the window's second
    ///         column is now its first. Addressing a page by its column <em>renames</em> a thousand and
    ///         twenty-three pages to say that one left and one arrived — which is
    ///         <see cref="Compositor.VirtualShadowRenderer.Fit" /> throwing a whole level away for a
    ///         third of a metre of walking.
    ///     </para>
    ///     <para>
    ///         So a page is addressed by its position <em>modulo the window</em> instead:
    ///         <see cref="VirtualShadowMap.ToroidalOf" /> adds this and wraps, which is a mapping a
    ///         slide leaves alone except on the columns that actually wrapped around. A slide of one
    ///         page then costs the thirty-two pages of one column rather than all thousand and
    ///         twenty-four.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Y</c> is negated against <see cref="VirtualShadowMap.ClipmapCell" />'s
    ///         <c>Up</c>, and that is not a slip.</b> A page grid's rows run <em>down</em> the map —
    ///         <see cref="VirtualShadowMap.PageOf" /> negates y for the engine's UV convention — so a
    ///         camera rising by one page moves a world row's grid coordinate <em>up</em> by one where
    ///         a camera moving right moves its column down by one. Only a negated origin makes the
    ///         same <c>origin + grid</c> arithmetic stable on both axes, which is what lets the two
    ///         shaders share one line.
    ///     </para>
    ///     <para>
    ///         Zero for a spot, whose map does not move at all: at the origin the toroidal address and
    ///         the grid coordinate are the same number, which is what the whole spot path was written
    ///         against.
    ///     </para>
    /// </remarks>
    public Int2 Origin;

    /// <summary>Eight bytes of tail padding the shader declares and never reads.</summary>
    /// <remarks>
    ///     Declared rather than left to the compiler, for <c>CullInstance.Padding</c>'s reason: the
    ///     matrix aligns this record to sixteen, so the device's array stride is the size rounded up to
    ///     it — and a stride the two sides disagree about reads level one out of the middle of level
    ///     zero, which is every page of every level addressed into another level's world.
    /// </remarks>
    public Int2 Padding;
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

    /// <summary>How far a level's centre moves along the light between refits, in world units.</summary>
    /// <param name="level">Which level.</param>
    /// <param name="firstExtent">How wide level zero is.</param>
    /// <param name="depthRange">How deep the level's box is along the light.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The third axis is not a page axis, and giving it the page's step was task #124's
    ///         blink.</b> A level's page grid quantises the two axes it is <em>made</em> of; the third
    ///         is the depth the level's box spans, which is <paramref name="depthRange" /> for every
    ///         level alike and has no page structure at all. Stepping it at the lateral page size made
    ///         level zero's near plane move every 0.3 m of walking and level seven's every 40 m — a
    ///         hundred and twenty-eight to one on an axis the two levels share — and every one of those
    ///         steps shifts every stored depth in the level, so <c>VirtualShadowRenderer.Fit</c>
    ///         invalidated the level wholesale. Measured in sample 13 while walking: the clipmap threw
    ///         away twenty-nine pages a frame against a budget that redraws sixteen, so the map could
    ///         not converge while the camera moved.
    ///     </para>
    ///     <para>
    ///         <paramref name="depthRange" /> over <see cref="PagesPerSide" /> is the step, which is
    ///         the same statement the lateral axes make — the box is divided as many ways along the
    ///         light as across it — and it is the same for every level because the box is. What it
    ///         costs is the near plane trailing the camera by up to half a step, which the box's
    ///         remaining depth absorbs: at the defaults that is 6.25 m of slack in a 400 m range.
    ///     </para>
    ///     <para>
    ///         <paramref name="level" /> and <paramref name="firstExtent" /> are taken and deliberately
    ///         unused: a step finer than the level's own page would quantise the depth more finely than
    ///         the geometry it contains, so the clamp below is what keeps a caller who shrinks the
    ///         depth range from reintroducing the defect at a different scale.
    ///     </para>
    /// </remarks>
    public static float DepthStep(int level, float firstExtent, float depthRange) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(firstExtent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depthRange);

        return MathF.Max(depthRange / PagesPerSide, ExtentOf(level, firstExtent) / PagesPerSide);
    }

    /// <summary>Which cell of its own snap grid a level's centre lands in, along the light's basis.</summary>
    /// <param name="level">Which level.</param>
    /// <param name="firstExtent">How wide level zero is.</param>
    /// <param name="camera">Where the camera is.</param>
    /// <param name="lightDirection">The direction light travels, toward the scene.</param>
    /// <param name="depthRange">How deep the level's box is along the light.</param>
    /// <remarks>
    ///     <b>What <see cref="ClipmapProjection" /> is a function of, exposed so a caller can ask what
    ///     changed rather than only whether something did.</b> Two fits with the same cell have
    ///     bit-identical projections; two that differ only in <c>Right</c> or <c>Up</c> differ by a
    ///     whole number of pages laterally, which is a level that <em>slid</em>; one that differs in
    ///     <c>Light</c> has moved its near plane, which is the only kind of move that changes what a
    ///     page's stored depths mean. <c>VirtualShadowRenderer.Fit</c> tells the two apart with this.
    /// </remarks>
    public static (int Right, int Up, int Light) ClipmapCell(
        int level,
        float firstExtent,
        Vector3 camera,
        Vector3 lightDirection,
        float depthRange
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(firstExtent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depthRange);

        var light = Vector3.Normalize(lightDirection);
        var (right, up, _) = Basis(light);
        var page = ExtentOf(level, firstExtent) / PagesPerSide;

        return (
            (int)MathF.Floor(Vector3.Dot(camera, right) / page),
            (int)MathF.Floor(Vector3.Dot(camera, up) / page),
            (int)MathF.Floor(Vector3.Dot(camera, light) / DepthStep(level, firstExtent, depthRange))
        );
    }

    /// <summary>Where a clipmap level's window sits in the light's own endless page grid.</summary>
    /// <param name="level">Which level.</param>
    /// <param name="firstExtent">How wide level zero is.</param>
    /// <param name="camera">Where the camera is.</param>
    /// <param name="lightDirection">The direction light travels, toward the scene.</param>
    /// <param name="depthRange">How deep the level's box is along the light.</param>
    /// <remarks>
    ///     <see cref="VirtualShadowLevel.Origin" />, which is where the y negation is argued. The value
    ///     is the raw cell and deliberately not reduced modulo <see cref="PagesPerSide" />: the
    ///     wrapping is the shader's business, and a host that only kept the remainder could not tell a
    ///     slide of a whole window from no slide at all — which is <see cref="PageSurvives" />'
    ///     question.
    /// </remarks>
    public static Int2 ClipmapOrigin(
        int level,
        float firstExtent,
        Vector3 camera,
        Vector3 lightDirection,
        float depthRange
    ) {
        var cell = ClipmapCell(level, firstExtent, camera, lightDirection, depthRange);

        return new(cell.Right, -cell.Up);
    }

    /// <summary>Which toroidal address a window cell holds, for a map with this origin.</summary>
    /// <param name="page">The cell, in the window's own grid coordinates.</param>
    /// <param name="origin">The map's <see cref="VirtualShadowLevel.Origin" />.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The line the two shaders share, and the one thing that makes a page's identity a fact
    ///         about the world rather than about the window.</b> A window cell is where a point lands
    ///         <em>in the picture</em>, so it changes whenever the picture slides; the sum of it and
    ///         the origin does not, because the origin slides by exactly as much in the other
    ///         direction. Reducing the sum modulo the window is what keeps the address space finite,
    ///         and the price of that is the one column per page of slide whose address is handed to a
    ///         cell that has just arrived from the other side — which
    ///         <see cref="PageSurvives" /> names and <c>VirtualShadowRenderer.Fit</c> invalidates.
    ///     </para>
    ///     <para>
    ///         An <c>and</c> rather than a remainder because <see cref="PagesPerSide" /> is a power of
    ///         two and the sum may be negative, where <c>%</c> in both C# and GLSL would answer with
    ///         the sign of the dividend and address the page from the far side of the map.
    ///     </para>
    /// </remarks>
    public static Int2 ToroidalOf(Int2 page, Int2 origin) =>
        new((page.X + origin.X) & (PagesPerSide - 1), (page.Y + origin.Y) & (PagesPerSide - 1));

    /// <summary><see cref="ToroidalOf" /> backwards: which window cell an address names.</summary>
    /// <param name="toroidal">The address, as <see cref="ToroidalOf" /> answered it.</param>
    /// <param name="origin">The map's <see cref="VirtualShadowLevel.Origin" />.</param>
    /// <remarks>
    ///     What a page <em>draw</em> needs, and the half a shader never does: a page owed a draw is
    ///     known by its address, and <see cref="PageProjection" /> wants the rectangle of the window it
    ///     occupies. Skipping this leaves every page drawn into the atlas from somewhere else in the
    ///     level — a shadow of real geometry, at a plausible depth, in the wrong place.
    /// </remarks>
    public static Int2 GridOf(Int2 toroidal, Int2 origin) =>
        new((toroidal.X - origin.X) & (PagesPerSide - 1), (toroidal.Y - origin.Y) & (PagesPerSide - 1));

    /// <summary>Whether one axis of a toroidal address still means the same world after a slide.</summary>
    /// <param name="toroidal">The address's coordinate on that axis, 0 to <see cref="PagesPerSide" />.</param>
    /// <param name="before">That axis of the origin the map was fitted with.</param>
    /// <param name="after">That axis of the origin it has been refitted with.</param>
    /// <remarks>
    ///     <para>
    ///         <b>What a slide actually costs, counted rather than assumed.</b> A window covers the
    ///         unwrapped coordinates <c>[origin, origin + PagesPerSide)</c>, and exactly one of them
    ///         carries each address; the address survives when the coordinate it named before the slide
    ///         is still inside the window after it. A slide of <c>d</c> pages therefore kills
    ///         <c>min(|d|, PagesPerSide)</c> of the thirty-two, whichever way it went and however the
    ///         wrap fell.
    ///     </para>
    ///     <para>
    ///         ⚠ This is why <see cref="ClipmapOrigin" /> keeps the raw cell. A slide of exactly
    ///         <see cref="PagesPerSide" /> pages leaves every address's remainder untouched and every
    ///         address's world different, so an origin reduced modulo the window would report a whole
    ///         level of stale pages as fresh — which is the corrupt-atlas failure
    ///         <see cref="PageAbsent" /> exists to keep out of the table.
    ///     </para>
    /// </remarks>
    public static bool PageSurvives(int toroidal, int before, int after) {
        var unwrapped = before + ((toroidal - before) & (PagesPerSide - 1));

        return unwrapped >= after && unwrapped < after + PagesPerSide;
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
        var cell = ClipmapCell(level, firstExtent, camera, lightDirection, depth);

        // Snapped along the light's own axes, all three — the two the page grid is made of because a
        // page must not slide, and the third because an unsnapped near plane changes the matrix when
        // nothing visible does, which stops "the projection is identical" being something a test can
        // state. ShadowCascades.Fit makes the same argument about the same third axis.
        var centre =
            (right * (cell.Right * page))
            + (up * (cell.Up * page))
            + (light * (cell.Light * DepthStep(level, firstExtent, depth)));

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

    /// <summary>Which of a map's pages a world-space sphere's shadow can reach.</summary>
    /// <param name="viewProjection">The map's projection.</param>
    /// <param name="center">The sphere's centre.</param>
    /// <param name="radius">Its radius.</param>
    /// <param name="first">The lowest page of the span, in grid coordinates, when this returns true.</param>
    /// <param name="last">The highest, inclusive.</param>
    /// <returns>Whether the sphere reaches this map at all.</returns>
    /// <remarks>
    ///     <para>
    ///         <b><see cref="PageOf" /> for a volume rather than a point, and the pages it names are
    ///         where that volume's <em>shadow</em> lands — which is the same rectangle.</b> A shadow
    ///         map's projection looks along the light, so a caster's footprint in the map's clip space
    ///         is exactly the set of texels whose stored depth it can change. That is what makes a
    ///         moved caster a bounded invalidation instead of a level: see
    ///         <c>VirtualShadowRenderer.Displace</c>.
    ///     </para>
    ///     <para>
    ///         The corners of the sphere's bounding box rather than the sphere, because a projective
    ///         map takes the box's convex hull to a set containing the sphere's image whenever every
    ///         corner is in front of the near plane — which is conservative in the one direction an
    ///         invalidation may be wrong in. The box is the axis-aligned one and not an oriented one,
    ///         so this costs no rotation and over-covers by at most a page at the scales involved.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Clamped rather than rejected, unlike <see cref="PageOf" />.</b> A caster hanging
    ///         over the edge of a level still shadows the part of it that is inside, and a span that
    ///         refused a sphere whose centre was outside would leave exactly the boundary pages stale.
    ///         Only a sphere entirely outside the map — beside it, or outside its depth slab — answers
    ///         false. A sphere straddling the near plane of a perspective map answers the whole grid,
    ///         because a corner behind the eye projects to a coordinate that means nothing and the
    ///         conservative answer is the only honest one.
    ///     </para>
    /// </remarks>
    public static bool PageSpan(
        in Matrix4x4 viewProjection,
        Vector3 center,
        float radius,
        out Int2 first,
        out Int2 last
    ) {
        first = default;
        last = default;

        var extent = MathF.Abs(radius);
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var ahead = false;
        var behind = false;

        for (var corner = 0; corner < 8; corner++) {
            var world = center + new Vector3(
                (corner & 1) == 0 ? -extent : extent,
                (corner & 2) == 0 ? -extent : extent,
                (corner & 4) == 0 ? -extent : extent
            );

            var clip = Matrix4x4.TransformVector4(new(world, 1f), viewProjection);

            if (clip.W <= GpuCulling.ClipEpsilon) {
                behind = true;

                continue;
            }

            ahead = true;

            var ndc = new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);

            minimum = Vector3.Min(minimum, ndc);
            maximum = Vector3.Max(maximum, ndc);
        }

        if (!ahead) {
            return false;
        }

        if (behind) {
            // Nothing about the projected box can be trusted, and a clipmap never gets here: an
            // orthographic map's w is one for every point in the world.
            last = new(PagesPerSide - 1, PagesPerSide - 1);

            return true;
        }

        // Outside the slab casts nothing into this map, on either side of it.
        if (maximum.Z < 0f || minimum.Z > 1f) {
            return false;
        }

        // The engine's UV convention, as PageOf applies it — and y is negated, so the box's top in
        // clip space is its low v. Getting that backwards mirrors the span about the map's centre,
        // which invalidates a page that was fine and leaves the one that moved.
        var lowU = (minimum.X * 0.5f) + 0.5f;
        var highU = (maximum.X * 0.5f) + 0.5f;
        var lowV = (-maximum.Y * 0.5f) + 0.5f;
        var highV = (-minimum.Y * 0.5f) + 0.5f;

        if (highU < 0f || lowU >= 1f || highV < 0f || lowV >= 1f) {
            return false;
        }

        first = new(Cell(lowU), Cell(lowV));
        last = new(Cell(highU), Cell(highV));

        return true;

        static int Cell(float coordinate) =>
            Math.Clamp((int)MathF.Floor(coordinate * PagesPerSide), 0, PagesPerSide - 1);
    }

    /// <summary>A virtual page's index in the global numbering.</summary>
    /// <param name="first">The map's <see cref="VirtualShadowLevel.First" />.</param>
    /// <param name="page">Its page, as a <see cref="ToroidalOf" /> address.</param>
    /// <remarks>
    ///     ⚠ <b>The toroidal address and not the window cell <see cref="PageOf" /> answers.</b> The two
    ///     are the same number only for a map whose origin is zero — every spot, and a clipmap level
    ///     that has never moved — so a caller that skips <see cref="ToroidalOf" /> is right until the
    ///     camera walks one page and then addresses somebody else's page for the rest of the session.
    /// </remarks>
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
}
