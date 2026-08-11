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
        for (var page = 0; page < count; page++) {
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
