// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Xunit;

namespace Tests;

/// <summary>
///     The virtual shadow node's page lifecycle, driven through whole frames — docs/plan/22 phase 7.
/// </summary>
/// <remarks>
///     <para>
///         <b>What is being tested is the seam the policy tests cannot reach:</b>
///         <c>VirtualShadowPageTests</c> proves the store's promises one call at a time, and this file
///         proves the node keeps them across a frame — that a page taken in <c>Collect</c> is the page
///         <c>Record</c> publishes, that a budget's backlog drains rather than leaks, and that the fit
///         invalidates when the light moved and only then.
///     </para>
///     <para>
///         The stall this file guards against was live in sample 13: its orbiting sun changed every
///         level's projection every frame, every resident page was invalidated every frame, and the
///         sixteen-a-frame budget redrew pages the next refit unpublished before their table upload —
///         five hundred and thirty-one pages perpetually owed, forty-eight ever published, the map
///         answering nothing while allocating at full rate. The snap is what turns that into a map
///         that holds still between steps, and the sweep here is the regression test for it.
///     </para>
/// </remarks>
public class VirtualShadowRendererTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    // --- Fixture ------------------------------------------------------------

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderGraph Graph { get; init; }
        public required VirtualShadowAtlas Atlas { get; init; }
        public required VirtualShadowRenderer Node { get; init; }
        public required Sunlight Sun { get; init; }

        public void Dispose() {
            Graph.DisposePool();
            Atlas.Dispose();
            System.Dispose();
        }
    }

    /// <summary>A sun somebody decided on, which is all a shadow renderer needs to know.</summary>
    sealed class Sunlight : ISunSource {
        public RenderLight? Sun { get; set; }
    }

    Harness Build(int drawsPerFrame = 4) {
        var system = new RenderSystem();
        var caster = system.AddStage(new("Shadow"));
        var atlas = new VirtualShadowAtlas(device, pagesPerSide: 8);

        var sun = new Sunlight {
            Sun = new RenderLight { Kind = LightKind.Directional, Direction = Vector3.Normalize(new(-0.4f, -1f, -0.3f)) }
        };

        var node = new VirtualShadowRenderer {
            Name = "SunPages",
            CasterStage = caster,
            Atlas = atlas,
            Sun = sun,
            Camera = new("Camera"),
            ClipmapLevels = 4,
            DrawsPerFrame = drawsPerFrame,
            PagesPerFrame = 64
        };

        var compositor = new GraphicsCompositor(system) { Game = node, FrameSize = new(64, 64) };

        // Imported for ShadowMapRendererTests' reason: the depth the marking pass reads is
        // host-owned in a real frame, and an import is the shape that says so.
        var description = new TextureDescription(
            PixelFormat.Depth32Float,
            64,
            64,
            TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
            Name: "SceneDepth"
        );

        var texture = device.CreateTexture(description);
        compositor.Imports["SceneDepth"] = new(texture, device.CreateTextureView(texture), description);

        return new() {
            System = system,
            Compositor = compositor,
            Graph = new(device),
            Atlas = atlas,
            Node = node,
            Sun = sun
        };
    }

    /// <summary>One whole frame: collect, build, execute — what the page draws hang off.</summary>
    void Frame(Harness h) {
        var list = device.BeginCommandList();

        h.Graph.Reset();
        h.Compositor.Build(h.Graph, effects, device);
        h.Graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    /// <summary>
    ///     Makes pages of clipmap level zero resident, the way the serviced marks would.
    /// </summary>
    /// <remarks>
    ///     Driven at the residency service rather than through the marking dispatch, because the
    ///     device here records and does not execute — a compute pass cannot fill the mark bitset, and
    ///     what this file is about starts after the marks were read.
    /// </remarks>
    static void Allocate(Harness h, int count) {
        var pages = new int[count];

        for (var page = 0; page < count; page++) {
            pages[page] = page;
        }

        Allocate(h, pages);
    }

    /// <summary>The same, for named pages rather than a prefix of the address space.</summary>
    /// <remarks>
    ///     What a test about <em>addressing</em> needs: once a level has slid, the page a piece of
    ///     world is under is a toroidal address and not a low number, so "the first n pages" is no
    ///     longer a way to name the page a camera is standing on.
    /// </remarks>
    static void Allocate(Harness h, int[] pages) {
        foreach (var page in pages) {
            h.Atlas.Residency.Request(new(VirtualShadowPages.Source, page));
        }

        var waited = System.Diagnostics.Stopwatch.StartNew();

        // VirtualShadowPageTests' Settle: a load is asynchronous even when it reads nothing.
        while (waited.Elapsed < TimeSpan.FromSeconds(30)) {
            h.Atlas.Residency.Service(64);

            if (h.Atlas.Residency.PendingRequests == 0 && h.Atlas.Residency.Loading == 0) {
                h.Atlas.Residency.Service(64);
                return;
            }

            Thread.Sleep(1);
        }

        Assert.Fail("The residency service never settled.");
    }

    /// <summary>Puts the sun on the snap's own lattice, so a drift under a cell stays inside one.</summary>
    /// <remarks>
    ///     <c>VirtualShadowMapTests.A_camera_that_moved_less_than_a_page_does_not_move_the_projection</c>'s
    ///     argument, one axis over: a sun at an arbitrary angle may be a hair from a cell boundary, and
    ///     a test that drifted it would then be asserting where the boundary happens to be rather than
    ///     that there is a lattice at all.
    /// </remarks>
    static void Anchor(Harness h) =>
        h.Sun.Sun = h.Sun.Sun!.Value with {
            Direction = VirtualShadowMap.SnapDirection(h.Sun.Sun!.Value.Direction, h.Node.LightSnapDegrees)
        };

    static int Published(Harness h) {
        var published = 0;

        foreach (var entry in h.Atlas.Pages.Table) {
            if (entry != VirtualShadowMap.PageAbsent) {
                published++;
            }
        }

        return published;
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- The budget drains the backlog --------------------------------------

    /// <summary>
    ///     A burst beyond the budget is absent until drawn, and entirely drawn within its own frames.
    /// </summary>
    /// <remarks>
    ///     The whole allocation-to-draw seam in one sweep: twelve pages allocated at once against a
    ///     budget of four, none of them sampleable before its draw was recorded, four landing per
    ///     frame, and the queue empty after exactly the three frames the arithmetic promises. A page
    ///     sampleable early is a slot holding another page's depths; a queue alive after three frames
    ///     is a draw the node lost between the take and the record.
    /// </remarks>
    [Fact]
    public void A_burst_beyond_the_budget_lands_four_a_frame_and_no_earlier() {
        using var h = Build(drawsPerFrame: 4);

        Allocate(h, 12);

        // Allocated, owed, and not one of them sampleable.
        Assert.Equal(12, h.Atlas.Pages.Pending.Count);
        Assert.Equal(0, Published(h));

        for (var frame = 1; frame <= 3; frame++) {
            Frame(h);

            Assert.Equal(4, h.Node.DrawnPages);
            Assert.Equal(frame * 4, Published(h));
        }

        Assert.Empty(h.Atlas.Pages.Pending);

        // And the steady state is the whole win: nothing owed, nothing drawn, everything held.
        Frame(h);
        Assert.Equal(0, h.Node.DrawnPages);
        Assert.Equal(12, Published(h));
    }

    // --- The bias -------------------------------------------------------------

    /// <summary>The lookup's bias arrives as a distance, divided by the level's own depth range.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>ShadowCascade.depthScale</c>'s lesson, one shadow path late.</b> A clipmap
    ///         level's box is <see cref="VirtualShadowRenderer.Depthrange" /> metres deep, so one unit
    ///         of the normalised depth the lookup compares in is four hundred metres of world at the
    ///         shipped setting. Until this test existed the node published neither bias at all and the
    ///         shader fell back to its own declaration — 0.002 and 0.004 raw, which is <em>0.8 m</em>
    ///         of constant bias and 1.6 m per unit of slope against a cascade path biased by 0.008 m
    ///         and 0.01 m.
    ///     </para>
    ///     <para>
    ///         That is not a tuning difference: it is why a page and a cascade cannot agree. The map
    ///         answers where a page has been drawn and the cascades answer everywhere else, so a bias
    ///         a hundred times theirs means every shadow whose caster stands within a metre of its
    ///         receiver is present in one answer and biased away in the other — and a page arriving
    ///         then puts that shadow out, which is a blink at a page boundary rather than at anything
    ///         in the world.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_page_bias_reaches_the_lookup_as_a_distance() {
        using var h = Build();

        h.Node.Scene = new();

        h.Compositor.Collect();

        var parameters = h.Node.Scene;
        var scale = VirtualShadowMap.DepthScale(h.Node.Depthrange);

        foreach (var prefix in (string[])["ForwardPlus.VirtualShadowLookup", "VisibilityResolve.VirtualShadowLookup"]) {
            var constant = parameters.Get(ParameterKeys.New<float>($"{prefix}.shadowPageConstantBias"));
            var slope = parameters.Get(ParameterKeys.New<float>($"{prefix}.shadowPageSlopeBias"));

            Assert.Equal(h.Node.ConstantBias * scale, constant, 9);
            Assert.Equal(h.Node.SlopeBias * scale, slope, 9);

            // And read back the other way, which is the statement that matters: whatever the node
            // publishes, one unit of it is one unit of the map's depth, and the metres it stands for
            // are the metres the node was given.
            Assert.Equal(h.Node.ConstantBias, constant / scale, 5);
            Assert.Equal(h.Node.SlopeBias, slope / scale, 5);

            // The number the shader used to fall back to, stated so the size of the mistake is on the
            // record: raw 0.002 over this box is four fifths of a metre — a hundred times the node's.
            Assert.Equal(0.8f, 0.002f / scale, 2);
            Assert.Equal(100f, 0.002f / scale / h.Node.ConstantBias, 1);
        }
    }

    // --- The sun and the snap ------------------------------------------------

    /// <summary>A sun drifting less than the snap leaves every drawn page standing.</summary>
    /// <remarks>
    ///     Sample 13's stall, inverted into an assertion. An orbiting sun drifts a fraction of a
    ///     degree a frame; unsnapped, that changed every level's matrix every frame and re-queued
    ///     every resident page faster than any budget drained them — the map answered nothing,
    ///     forever, while reporting full-rate work. Snapped, the fitted direction is a lattice point
    ///     and a drift inside the cell is bit-identical matrices: no invalidation, no redraw, and the
    ///     pages stay exactly as sampleable as the frame before.
    /// </remarks>
    [Fact]
    public void A_sun_drifting_under_the_snap_leaves_the_pages_standing() {
        using var h = Build(drawsPerFrame: 8);

        // Anchored on the lattice, for VirtualShadowMapTests' reason: a sun an arbitrary hair from
        // a cell boundary would make this a test of where the boundary happens to be.
        Anchor(h);
        Allocate(h, 8);
        Frame(h);
        Assert.Equal(8, Published(h));

        var direction = h.Sun.Sun!.Value.Direction;

        // A thirtieth of the snap per frame — the drift a thirty-second orbit makes at speed.
        for (var frame = 0; frame < 6; frame++) {
            direction = Turned(direction, 0.015f);
            h.Sun.Sun = h.Sun.Sun!.Value with { Direction = direction };

            Frame(h);

            Assert.Equal(0, h.Node.DrawnPages);
            Assert.Equal(8, Published(h));
            Assert.Empty(h.Atlas.Pages.Pending);
        }
    }

    /// <summary>A sun stepping past the snap invalidates once, and the budget recovers.</summary>
    /// <remarks>
    ///     The other half of the bargain. The snap does not make a moved sun free — a step past it is
    ///     every level refitted and every resident page's depths about a light that is not there any
    ///     more, so they are unpublished at once: stale-but-sampleable is a shadow at an angle nothing
    ///     in the picture explains, and absent is a frame the cascades cover. What the snap bounds is
    ///     the <em>cadence</em>: one invalidation per step rather than one per frame, which is a
    ///     backlog the budget clears instead of one it chases forever.
    /// </remarks>
    [Fact]
    public void A_sun_stepping_past_the_snap_invalidates_once_and_the_budget_recovers() {
        using var h = Build(drawsPerFrame: 4);

        Allocate(h, 12);
        Frame(h);
        Frame(h);
        Frame(h);
        Assert.Equal(12, Published(h));

        // Two degrees at once — four lattice steps, a sun that visibly moved.
        h.Sun.Sun = h.Sun.Sun!.Value with { Direction = Turned(h.Sun.Sun!.Value.Direction, 2f) };

        // The step frame: every page's depths are about the old sun, so none may answer. The four
        // the budget redrew this frame are already back — drawn against the new fit.
        Frame(h);
        Assert.Equal(4, h.Node.DrawnPages);
        Assert.Equal(4, Published(h));
        Assert.Equal(8, h.Atlas.Pages.Pending.Count);

        // And the backlog drains on the same arithmetic as any other burst.
        Frame(h);
        Frame(h);
        Assert.Equal(12, Published(h));
        Assert.Empty(h.Atlas.Pages.Pending);

        // Standing still afterwards costs nothing again.
        Frame(h);
        Assert.Equal(0, h.Node.DrawnPages);
    }

    // --- A level that slid keeps the pages it still covers -------------------

    /// <summary>A camera walking one page costs one column of pages, not a level — task #317.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The lateral half of the blink, and the number is the whole test.</b> A level's window
    ///         is thirty-two pages across and re-centres on the camera in whole pages, so a camera that
    ///         walks one page of level zero — a third of a metre at the shipped extent — slides that
    ///         window by one and leaves thirty-one of its thirty-two columns over the same world. Under
    ///         the window-cell addressing this replaced, every page of the level was renamed by that
    ///         slide and <see cref="VirtualShadowRenderer.Fit" /> unpublished all thousand and
    ///         twenty-four of them; against a budget that redraws sixteen, a walking camera's map could
    ///         not converge. Now the arriving column is the only address that means somewhere new.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read as a column and not as a count</b>, because a count of two would also be what
    ///         an addressing that happened to retire two scattered pages produced. The two pages that
    ///         went are asserted to share a column and to be on different rows, which is the shape a
    ///         slide along <c>right</c> has and the shape nothing else has.
    ///     </para>
    ///     <para>
    ///         The budget is taken to zero for the moving frame on purpose: the node redraws what it
    ///         invalidated inside the very same <c>Collect</c>, so a frame with a budget republishes
    ///         the retired pages before anything can look. That is correct behaviour and it is exactly
    ///         what would hide this.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_level_that_slid_one_page_keeps_every_column_but_the_one_that_arrived() {
        using var h = Build(drawsPerFrame: 64);

        Anchor(h);

        // Two whole rows of level zero's grid, so a column that retires takes one page from each and
        // a level that is thrown away takes both rows entirely.
        const int Pages = 2 * VirtualShadowMap.PagesPerSide;

        var (right, up, _) = VirtualShadowMap.Basis(h.Sun.Sun!.Value.Direction);
        var light = Vector3.Normalize(h.Sun.Sun!.Value.Direction);
        var page = VirtualShadowMap.ExtentOf(0, h.Node.FirstExtent) / VirtualShadowMap.PagesPerSide;

        // ⚠ **Held off the other two axes' cell boundaries, and this is not decoration.** A camera at
        // `right * k` has an `up` and a `light` component of zero only to within a rounding error, and
        // `ClipmapCell` floors — so the two positions below would land in cell 0 or cell −1 on those
        // axes depending on which way the last bit of a dot product fell, and every level would refit
        // for reasons that have nothing to do with a slide. A third of a page up and half a depth step
        // along the light puts both comfortably inside one cell, for every level at once.
        var off = (up * 0.3f * page) + (light * VirtualShadowMap.DepthStep(0, h.Node.FirstExtent, h.Node.Depthrange) * 0.5f);

        // At the middle of a cell of level zero's own grid, and stepping to the middle of the next —
        // odd multiples of a half page, so no coarser level's boundary is crossed. Level one snaps
        // every two of these pages, so a step over an even multiple would slide two levels at once
        // and this would be measuring the pair.
        h.Node.Camera!.Position = (right * 4.5f * page) + off;

        Allocate(h, Pages);
        Frame(h);
        Assert.Equal(Pages, Published(h));

        var before = new HashSet<int>(Resident(h));

        // One page along `right`, which is perpendicular to the light — so the depth cell does not
        // move and this is a slide and nothing else.
        h.Node.Camera!.Position = (right * 5.5f * page) + off;
        h.Node.DrawsPerFrame = 0;

        Frame(h);

        Assert.Equal(1, h.Node.RefitLevels);
        Assert.Equal(2, h.Node.InvalidatedPages);

        var retired = before.Except(Resident(h)).Order().ToArray();

        Assert.Equal(2, retired.Length);

        // One column of the level, one page from each row: same x, different y.
        Assert.Equal(
            retired[0] % VirtualShadowMap.PagesPerSide,
            retired[1] % VirtualShadowMap.PagesPerSide
        );

        Assert.NotEqual(
            retired[0] / VirtualShadowMap.PagesPerSide,
            retired[1] / VirtualShadowMap.PagesPerSide
        );

        // And the budget puts the column back on the next frame, drawn against the window as it
        // stands now — the same drain any other invalidation gets.
        h.Node.DrawsPerFrame = 64;
        Frame(h);
        Assert.Equal(Pages, Published(h));
    }

    /// <summary>A page drawn after a slide is drawn from where the window puts it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>⚠ The half of toroidal addressing that no counter can see, and the one that renders
    ///         plausibly when it is wrong.</b> The marking pass and the lookup share one line, so an
    ///         address they both got wrong the same way still answers: <c>absent</c> falls, every
    ///         counter in the trace improves, and the atlas fills with real geometry at plausible
    ///         depths taken from <em>somewhere else in the level</em>. What decides where a page is
    ///         drawn from is the host's own inverse — <see cref="VirtualShadowMap.GridOf" /> in
    ///         <c>Collect</c> — and a node that handed the address straight to
    ///         <see cref="VirtualShadowMap.PageProjection" /> would be right for exactly as long as
    ///         every origin was zero, which is until the camera walks one page.
    ///     </para>
    ///     <para>
    ///         Asserted against the world rather than against the arithmetic: the page under the
    ///         camera is drawn, and the camera's own position has to land inside that page's
    ///         viewport. Seven pages along, the address and the window cell differ by seven — which is
    ///         fourteen units of a two-unit clip space, so the bug does not merely blur the assertion,
    ///         it puts the point off the map. That difference is asserted too, or a run where they
    ///         happened to agree would pass without testing anything.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_page_owed_a_draw_after_a_slide_is_drawn_where_the_window_puts_it() {
        using var h = Build(drawsPerFrame: 1);

        Anchor(h);

        // The same function on the same input, so the fit under test and the expectation here share
        // a direction bit for bit rather than to within a snap.
        var light = VirtualShadowMap.SnapDirection(h.Sun.Sun!.Value.Direction, h.Node.LightSnapDegrees);
        var (right, up, _) = VirtualShadowMap.Basis(light);
        var page = VirtualShadowMap.ExtentOf(0, h.Node.FirstExtent) / VirtualShadowMap.PagesPerSide;

        // Off the other two axes' cell boundaries — see the slide test above for why that matters.
        var off = (up * 0.3f * page)
            + (light * VirtualShadowMap.DepthStep(0, h.Node.FirstExtent, h.Node.Depthrange) * 0.5f);

        h.Node.Camera!.Position = (right * 7.5f * page) + off;

        var projection = VirtualShadowMap.ClipmapProjection(
            0,
            h.Node.FirstExtent,
            h.Node.Camera!.Position,
            light,
            h.Node.Depthrange
        );

        var origin = VirtualShadowMap.ClipmapOrigin(
            0,
            h.Node.FirstExtent,
            h.Node.Camera!.Position,
            light,
            h.Node.Depthrange
        );

        Assert.True(VirtualShadowMap.PageOf(projection, h.Node.Camera!.Position, out var cell));

        var address = VirtualShadowMap.ToroidalOf(cell, origin);

        // The premise: seven pages of walking, so the two are not the same page and the assertion
        // below is about which of them the draw used.
        Assert.NotEqual(cell, address);

        Allocate(h, [VirtualShadowMap.IndexOf(0u, address)]);
        Frame(h);

        // Owed again, so it is the one page a budget of one draws next frame and the one view the
        // node registers is unambiguously its.
        Assert.True(h.Atlas.Pages.Invalidate(VirtualShadowMap.IndexOf(0u, address)));
        Frame(h);

        var view = Assert.Single(h.System.Views, candidate => candidate.Name == $"{h.Node}.Page0");
        var clip = Matrix4x4.TransformVector4(new(h.Node.Camera!.Position, 1f), view.ViewProjection);

        Assert.True(clip.W > 0f, "the page's projection put the camera behind its own near plane");

        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);

        Assert.InRange(ndc.X, -1f, 1f);
        Assert.InRange(ndc.Y, -1f, 1f);
    }

    /// <summary>Which virtual pages the table currently answers.</summary>
    static List<int> Resident(Harness h) {
        var table = h.Atlas.Pages.Table;
        var answered = new List<int>();

        for (var page = 0; page < table.Length; page++) {
            if (table[page] != VirtualShadowMap.PageAbsent) {
                answered.Add(page);
            }
        }

        return answered;
    }

    // --- A caster that moved ------------------------------------------------

    /// <summary>An object that moved retires the pages it was under and the pages it is under.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The correctness half of task #250, and the one that reads as a bug rather than as a
    ///         cost.</b> A page is drawn once and kept until something says its depths are wrong, so a
    ///         caster that moves out of a page and nothing to say so leaves that page holding the
    ///         shadow of a thing that is no longer there — a silhouette standing in the air with
    ///         nothing casting it — and the page it moved <em>into</em> holding a floor with no shadow
    ///         on it at all. Neither is transient: the map is a cache, and a cache nobody invalidates
    ///         is wrong for the rest of the session.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both ends, and the "was" end is the one a naive fix misses.</b> Invalidating only
    ///         where the object now is leaves the stale silhouette exactly where it was, which is the
    ///         visible half.
    ///     </para>
    ///     <para>
    ///         The budget is taken to zero for the moving frame for the slide test's reason: the node
    ///         redraws what it invalidated inside the same <c>Collect</c>, so a frame with a budget
    ///         republishes the retired pages before anything can look at the table.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_caster_that_moved_retires_the_pages_it_left_and_the_pages_it_reached() {
        using var h = Build(drawsPerFrame: 64);

        Anchor(h);

        var light = VirtualShadowMap.SnapDirection(h.Sun.Sun!.Value.Direction, h.Node.LightSnapDegrees);
        var (right, up, _) = VirtualShadowMap.Basis(light);
        var page = VirtualShadowMap.ExtentOf(0, h.Node.FirstExtent) / VirtualShadowMap.PagesPerSide;
        var camera = h.Node.Camera!.Position;

        // The same arithmetic Fit does, so the row named below is the row the node fitted.
        var origin = VirtualShadowMap.ClipmapOrigin(0, h.Node.FirstExtent, camera, light, h.Node.Depthrange);

        // One whole row of level zero's grid — thirty-two pages into a sixty-four slot atlas — so
        // the columns that go and the columns that stay are both in the table to be counted.
        var row = new int[VirtualShadowMap.PagesPerSide];

        for (var column = 0; column < row.Length; column++) {
            row[column] = VirtualShadowMap.IndexOf(0u, VirtualShadowMap.ToroidalOf(new(column, 16), origin));
        }

        Allocate(h, row);
        Frame(h);

        Assert.Equal(row.Length, Published(h));

        // ⚠ **Held at the middle of a page on both axes.** A caster on a page boundary has a
        // footprint in two columns whichever way the last bit of a dot product falls, and the count
        // below would then be asserting where the boundary is rather than that both ends went.
        var was = (right * -4.5f * page) + (up * -0.5f * page);
        var now = (right * 3.5f * page) + (up * -0.5f * page);

        var id = h.System.Objects.Add(new() { Bounds = new(was, 0.1f * page), Stages = h.Node.CasterStage.Mask });

        // The caster appearing is itself a change; what matters is that a frame with nothing moving
        // retires nothing, which is the second one.
        Frame(h);
        Frame(h);

        Assert.Equal(0, h.Node.InvalidatedPages);

        var before = new HashSet<int>(Resident(h));

        Assert.Equal(row.Length, before.Count);

        // Eight pages along `right`, which is perpendicular to the light — so no level refits, and
        // every page that goes goes because the caster moved and for no other reason. The budget is
        // taken to zero for the same reason the slide test takes it to zero.
        h.System.Objects[id].Bounds = new(now, 0.1f * page);
        h.Node.DrawsPerFrame = 0;

        Frame(h);

        Assert.Equal(0, h.Node.RefitLevels);

        var retired = before.Except(Resident(h)).Order().ToArray();

        Assert.NotEmpty(retired);

        var columns = VirtualShadowMap.PagesPerSide;
        var grid = retired.Select(entry => VirtualShadowMap.GridOf(new(entry % columns, entry / columns), origin).X)
            .Distinct()
            .Order()
            .ToArray();

        // Both ends, and the gap is what says so: the two footprints are eight pages apart along
        // `right`, so a fix that invalidated only where the object now is would retire one column.
        Assert.True(
            grid[^1] - grid[0] >= 4,
            $"the retired pages cover grid columns {string.Join(", ", grid)}, which is one footprint "
            + "rather than the page the caster left and the page it reached"
        );

        // And a footprint is a footprint: most of the row is untouched. An invalidation that threw
        // the level away would pass every assertion above and fail this one.
        Assert.True(retired.Length <= 8, $"{retired.Length} of {row.Length} pages went for one caster");

        // The budget puts them back, drawn against a level that never moved.
        h.Node.DrawsPerFrame = 64;
        Frame(h);
        Assert.Equal(row.Length, Published(h));
    }

    /// <summary>A caster that turned on the spot, and one that was removed, retire their pages too.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The two moves a bounds comparison cannot see.</b>
    ///         <see cref="RenderObject.Bounds" /> is a sphere, so an object that rotated has the
    ///         bounds it had and an object that was deleted has whatever its dead slot still holds —
    ///         and in both cases the page keeps a shadow of something that is not there any more. The
    ///         first is what <see cref="VirtualShadowRenderer.CasterTransforms" /> exists for; the
    ///         second is why a footprint records whether the slot was casting at all rather than being
    ///         cleared on removal.
    ///     </para>
    ///     <para>
    ///         ⚠ Asserted as a page count and not merely as a counter, because
    ///         <see cref="VirtualShadowRenderer.MovedCasters" /> would be one for a walk that noticed
    ///         the change and then retired nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_caster_that_turned_and_a_caster_that_was_removed_both_retire_their_pages() {
        using var h = Build(drawsPerFrame: 64);

        Anchor(h);

        var world = h.System.Objects.Data.Register<Matrix4x4>();

        h.Node.CasterTransforms = world;

        var light = VirtualShadowMap.SnapDirection(h.Sun.Sun!.Value.Direction, h.Node.LightSnapDegrees);
        var (right, up, _) = VirtualShadowMap.Basis(light);
        var page = VirtualShadowMap.ExtentOf(0, h.Node.FirstExtent) / VirtualShadowMap.PagesPerSide;
        var camera = h.Node.Camera!.Position;
        var origin = VirtualShadowMap.ClipmapOrigin(0, h.Node.FirstExtent, camera, light, h.Node.Depthrange);
        var row = new int[VirtualShadowMap.PagesPerSide];

        for (var column = 0; column < row.Length; column++) {
            row[column] = VirtualShadowMap.IndexOf(0u, VirtualShadowMap.ToroidalOf(new(column, 16), origin));
        }

        var at = (right * -4.5f * page) + (up * -0.5f * page);

        var id = h.System.Objects.Add(new() { Bounds = new(at, 0.1f * page), Stages = h.Node.CasterStage.Mask });

        h.System.Objects.Data.Data(world)[id.Index] = Matrix4x4.FromTranslation(at);

        Allocate(h, row);
        Frame(h);
        Frame(h);

        Assert.Equal(row.Length, Published(h));
        Assert.Equal(0, h.Node.InvalidatedPages);

        // A turn: the same sphere, a different matrix. Nothing in Bounds changed at all.
        var bounds = h.System.Objects[id].Bounds;

        h.System.Objects.Data.Data(world)[id.Index] =
            Matrix4x4.FromRotationY(1f) * Matrix4x4.FromTranslation(at);

        h.Node.DrawsPerFrame = 0;
        Frame(h);

        Assert.Equal(bounds, h.System.Objects[id].Bounds);
        Assert.Equal(1, h.Node.MovedCasters);
        Assert.True(h.Node.CasterInvalidations > 0, "a caster that turned retired no page");
        Assert.Equal(h.Node.CasterInvalidations, h.Node.InvalidatedPages);

        h.Node.DrawsPerFrame = 64;
        Frame(h);

        Assert.Equal(row.Length, Published(h));

        // And a removal, which is the same statement about the page the object is no longer on.
        h.System.Objects.Remove(id);
        h.Node.DrawsPerFrame = 0;
        Frame(h);

        Assert.Equal(1, h.Node.MovedCasters);
        Assert.True(h.Node.CasterInvalidations > 0, "a caster that was removed retired no page");
        Assert.Equal(row.Length - h.Node.CasterInvalidations, Published(h));
    }

    // --- The marking dispatch covers the screen ------------------------------

    /// <summary>Every pixel of the screen falls inside the marking dispatch, and barely so.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The failure this is the guard for is absence with every counter reporting success.</b>
    ///         <see cref="VirtualShadowAtlas.Mark" /> sizes its dispatch in workgroups and
    ///         <c>VirtualShadowMark.Main</c> turns a group index back into pixels, so the two multiply
    ///         the same numbers in opposite directions and nothing checks that they agree. A host that
    ///         kept the old per-pixel tiling after the shader went to blocks would dispatch sixteen
    ///         times the groups it needed; one that did the reverse would leave the right and bottom of
    ///         the screen unmarked — a strip of the picture whose pages are never asked for, which
    ///         shades as the cascades' own shadow and nothing at all in the logs. It is the same shape
    ///         as the cascade atlas that rendered two of its four folds below the texture's bottom
    ///         edge: an empty scissor, silently, with every counter saying it ran.
    ///     </para>
    ///     <para>
    ///         Both directions are asserted: enough groups to reach the last pixel, and not a whole
    ///         extra tile of them.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(3200, 1800)]
    [InlineData(1920, 1080)]
    [InlineData(1, 1)]
    [InlineData(33, 17)]
    public void The_marking_dispatch_reaches_the_last_pixel_and_no_further(int width, int height) {
        using var h = Build();

        h.Atlas.Effects = Marking();
        h.Atlas.Pipelines = new(device);

        h.Atlas.Begin(
            [new() { First = 0, Kind = 0u, TexelWorldSize = VirtualShadowMap.TexelOf(0, 10f) }],
            1,
            VirtualShadowMap.TexelOf(0, 10f)
        );

        var depth = device.CreateTexture(
            new(PixelFormat.Depth32Float, 4, 4, TextureUsage.DepthStencilTarget | TextureUsage.Sampled, Name: "Depth")
        );

        var list = device.BeginCommandList();

        device.Recorder!.Clear();
        Assert.True(h.Atlas.Mark(list, device.CreateTextureView(depth), new("Camera"), new(width, height)));

        // The recorder only sees a list that was finished and submitted — see NullCommandList.Flush.
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        var dispatch = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.Dispatch));
        var tile = VirtualShadowMap.MarkTile;

        Assert.InRange((int)dispatch.A * tile, width, width + tile - 1);
        Assert.InRange((int)dispatch.B * tile, height, height + tile - 1);
    }

    /// <summary>The shader's block size and the host's are the same number.</summary>
    /// <remarks>
    ///     Two files hold it — <c>Vsm.MarkBlock</c> decides how many pixels an invocation walks and
    ///     <see cref="VirtualShadowMap.MarkBlock" /> decides how many invocations are launched — and
    ///     they are not derived from each other, exactly as <c>PageTexels</c> and <c>PagesPerSide</c>
    ///     are not. Disagreeing is not a compile error on either side: it is a screen partly marked
    ///     and partly not.
    /// </remarks>
    [Fact]
    public void The_shader_and_the_host_agree_about_the_block_size() {
        var source = Path.Combine(LibraryPath(), "VirtualShadows", "VirtualShadows.rvn");

        Assert.True(File.Exists(source), $"the Raven library was not found at '{source}'");

        var declared = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(source),
            @"const\s+val\s+MarkBlock\s*=\s*(\d+)"
        );

        Assert.True(declared.Success, "VirtualShadows.rvn no longer declares Vsm.MarkBlock");
        Assert.Equal(VirtualShadowMap.MarkBlock, int.Parse(declared.Groups[1].Value));
    }

    /// <summary>
    ///     ⚠ Every field of <see cref="VirtualShadowLevel" /> is where the device expects it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>VirtualShadowMapTests.The_level_record_is_the_stride_the_device_reads</c>'s
    ///         missing half.</b> That one pins the record at ninety-six bytes, which is the failure
    ///         that renames every page of every level — but it is blind to the layout <em>inside</em>
    ///         those ninety-six. Two fields transposed, or one grown while another shrank, keeps the
    ///         size and moves the meaning, and nothing on either side is a compile error.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="VirtualShadowLevel.Origin" /> is the one that matters and it arrived
    ///         late.</b> The record was eighty bytes until toroidal page addressing landed (task
    ///         #317), and what grew it was an <c>Int2</c> inserted at offset 80 <em>ahead of the tail
    ///         padding</em>. Transposing those two is a page address computed from
    ///         <c>Vsm.Toroidal(0, cell)</c> — the identity — which is right for a level that has never
    ///         moved and wrong for every level that has, so it reads as a map that goes stale when the
    ///         camera walks rather than as a layout mistake.
    ///     </para>
    ///     <para>
    ///         Against the compiled reflection rather than against the <c>.rvn</c> text: the offsets
    ///         are what the Raven compiler decided, and the reflection is what the pipeline binds
    ///         from. A member added to the shader's struct without a field beside it here fails this
    ///         with a name in the message.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_shader_and_the_host_agree_about_every_field_of_a_level() {
        var source = Path.Combine(LibraryPath(), "Pipeline", "VirtualShadowMark.reflect.json");

        Assert.True(File.Exists(source), $"the compiled reflection was not found at '{source}'");

        var declared = LevelMembers(source);

        // What the host lays out, in the order the shader declares it. `padding0` and `padding1` are
        // one Int2 here and two ints there — deliberately, because what has to agree is the bytes.
        (string Name, int Offset, int Size)[] host = [
            ("viewProjection", OffsetOf(nameof(VirtualShadowLevel.ViewProjection)), 64),
            ("first", OffsetOf(nameof(VirtualShadowLevel.First)), 4),
            ("kind", OffsetOf(nameof(VirtualShadowLevel.Kind)), 4),
            ("texelWorldSize", OffsetOf(nameof(VirtualShadowLevel.TexelWorldSize)), 4),
            ("light", OffsetOf(nameof(VirtualShadowLevel.Light)), 4),
            ("origin", OffsetOf(nameof(VirtualShadowLevel.Origin)), 8),
            ("padding0", OffsetOf(nameof(VirtualShadowLevel.Padding)), 4),
            ("padding1", OffsetOf(nameof(VirtualShadowLevel.Padding)) + 4, 4)
        ];

        Assert.Equal(
            host.Select(field => field.Name).Order(StringComparer.Ordinal),
            declared.Keys.Order(StringComparer.Ordinal)
        );

        foreach (var (name, offset, size) in host) {
            Assert.True(
                declared[name] == (offset, size),
                $"'{name}' is at {offset} for {size} bytes on the host and at {declared[name].Offset} "
                + $"for {declared[name].Size} on the device. A record the two sides lay out differently "
                + "is not a compile error on either: it is every page of every level addressed into "
                + "another level's world, which renders and renders plausibly."
            );
        }

        Assert.Equal(
            System.Runtime.InteropServices.Marshal.SizeOf<VirtualShadowLevel>(),
            host.Max(field => field.Offset + field.Size)
        );
    }

    /// <summary>Where a field of <see cref="VirtualShadowLevel" /> starts.</summary>
    static int OffsetOf(string field) =>
        System.Runtime.InteropServices.Marshal.OffsetOf<VirtualShadowLevel>(field).ToInt32();

    /// <summary>The <c>levels</c> buffer's members, as the compiled reflection describes them.</summary>
    /// <remarks>
    ///     Walked rather than addressed by a path, because where a binding lands in the document is a
    ///     property of the shader's descriptor sets and not of the struct — and a test that hard-coded
    ///     the route would start failing when a set was added.
    /// </remarks>
    static Dictionary<string, (int Offset, int Size)> LevelMembers(string path) {
        var found = new Dictionary<string, (int Offset, int Size)>(StringComparer.Ordinal);

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

        Walk(document.RootElement);

        Assert.NotEmpty(found);

        return found;

        void Walk(System.Text.Json.JsonElement element) {
            switch (element.ValueKind) {
                case System.Text.Json.JsonValueKind.Object:
                    if (element.TryGetProperty("Name", out var name)
                        && name.ValueKind == System.Text.Json.JsonValueKind.String
                        && name.GetString() is { } text
                        && text.StartsWith("levels.", StringComparison.Ordinal)
                        && element.TryGetProperty("Offset", out var offset)
                        && element.TryGetProperty("Size", out var size)) {
                        found[text["levels.".Length..]] = (offset.GetInt32(), size.GetInt32());
                    }

                    foreach (var property in element.EnumerateObject()) {
                        Walk(property.Value);
                    }

                    break;

                case System.Text.Json.JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) {
                        Walk(item);
                    }

                    break;
            }
        }
    }

    /// <summary>An effect standing in for the compiled marking pass, so <c>Mark</c> records.</summary>
    EffectSystem Marking() {
        var system = new EffectSystem();
        var layouts = new DescriptorSetLayoutHandle[4];

        DescriptorBinding[] bindings = [
            new(VirtualShadowMarkKeys.SceneDepthBinding, DescriptorKind.SampledTexture, ShaderStage.Compute),
            new(VirtualShadowMarkKeys.LevelsBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
            new(VirtualShadowMarkKeys.MarksBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute)
        ];

        for (var slot = 0; slot < layouts.Length; slot++) {
            layouts[slot] = device.CreateDescriptorSetLayout(
                new((DescriptorSetSlot)slot, bindings, $"VirtualShadowMark.Set{slot}")
            );
        }

        system.Add(
            new() {
                Key = VirtualShadowAtlas.Key,
                Stages = [new(ShaderStage.Compute, [1, 2, 3, 4], "main")],
                SetLayouts = [.. layouts]
            }
        );

        return system;
    }

    /// <summary>Where <c>Raven/Library</c> is, found the way a development effect source finds it.</summary>
    static string LibraryPath() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (; directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library");

            if (Directory.Exists(candidate)) {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>A direction swung about the up axis by some degrees, elevation kept.</summary>
    static Vector3 Turned(Vector3 direction, float degrees) {
        var azimuth = MathF.Atan2(direction.Z, direction.X) + (degrees * (MathF.PI / 180f));
        var elevation = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
        var planar = MathF.Cos(elevation);

        return new(MathF.Cos(azimuth) * planar, MathF.Sin(elevation), MathF.Sin(azimuth) * planar);
    }
}
