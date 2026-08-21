// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     Phase 7's lookup, composed on a device and asked about points whose answer the host computed —
///     <c>docs/plan/22-virtualized-geometry.md</c> phase 7.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists: the shader had no gate at all.</b> The only thing in the tree that
///         composed <c>VirtualShadowLookup</c> was sample 13's frame — <c>ShadowMode.Virtual</c>
///         appears in no golden fixture and in no device test — and no gate renders a sample. So
///         <see cref="VirtualShadowLevel" />'s ninety-six bytes, its declared tail padding, and the
///         shipped shader's reading of them were asserted by a program nobody runs.
///     </para>
///     <para>
///         ⚠ <b>The record grew from eighty bytes to ninety-six when toroidal page addressing landed
///         (task #317), which is the change this is written against.</b>
///         <see cref="VirtualShadowLevel.Origin" /> was added at offset 80 and the padding moved to
///         88, and the array <em>stride</em> moved with them. A stride the two sides disagree about
///         reads level one out of the middle of level zero: every page of every level addressed into
///         another level's world, which renders, and renders plausibly.
///         <c>VirtualShadowMapTests.The_level_record_is_the_stride_the_device_reads</c> pins the
///         ninety-six on the host; nothing pinned what the device makes of it.
///     </para>
///     <para>
///         <b>The arrangement, and why the answers are exact rather than a picture.</b>
///         <c>Shaders/VirtualShadowProbe.rvn</c> composes the shipped lookup and does nothing else —
///         one query in, one <c>DirectionalShadowSample</c> out. Every level record, every page-table
///         entry and every stored depth is built here by <see cref="VirtualShadowMap" />'s own
///         arithmetic, so what comes back is <c>visibility</c> and <c>found</c> as numbers, and the
///         assertions are equalities rather than tolerances on a rendering.
///     </para>
///     <para>
///         ⚠ <b>Three levels, and the answer deliberately comes from the last of them.</b> A fixture
///         whose only map is level zero reads the buffer at offset zero and agrees with any stride
///         whatsoever. <see cref="Distance" /> is chosen so <c>Vsm.LevelFor</c> picks level two, which
///         is read at byte 192 — a host writing eighty-byte records would hand the shader bytes 160
///         to 240, which is the tail of level one and the head of level two.
///     </para>
///     <para>
///         ⚠ <b>And the fitted origin is asserted non-zero before anything else.</b>
///         <c>Vsm.Toroidal</c> is the identity for an origin of <c>(0, 0)</c>, so a level that had
///         never moved would give the same page address whether the shader read
///         <see cref="VirtualShadowLevel.Origin" /> or the tail padding beside it — and the fixture
///         would pass with the two fields transposed. <see cref="Camera" /> stands where it does for
///         that reason and nothing else.
///     </para>
///     <para>
///         Serialised with the rest of the driver tests: <see cref="VulkanDiagnostics" /> is
///         process-wide.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class VirtualShadowLookupDeviceTests {
    /// <summary>How many clipmap levels the fixture's map has.</summary>
    /// <remarks>
    ///     Three, because the answer has to come from a level that is not the first — see the class
    ///     remarks — and because a level past the last is what makes the "no page" case answer
    ///     <c>found = 0</c> rather than falling through to a coarser map the fixture never set up.
    /// </remarks>
    const int Levels = 3;

    /// <summary>How wide clipmap level zero is, in world units.</summary>
    const float FirstExtent = 10f;

    /// <summary>How deep each level's box is along the light.</summary>
    const float DepthRange = 400f;

    /// <summary>How many pages the physical atlas is on a side.</summary>
    /// <remarks>
    ///     Two rather than the shipped thirty-two: the atlas is <c>pages × Vsm.PageTexels</c> texels
    ///     square and every one of them is staged from host memory here, so two is a 256 × 256 upload
    ///     where thirty-two would be sixty-four megabytes to hold four pages of interest.
    /// </remarks>
    const int AtlasPages = 2;

    /// <summary>How many pixels tall the view the level selection is made for is.</summary>
    const int ScreenHeight = 1080;

    /// <summary><c>1 / tan(fov / 2)</c>. One, so the distance below is a round number.</summary>
    const float ScreenHeightScale = 1f;

    /// <summary>The constant depth bias, in the level's own normalised depth.</summary>
    /// <remarks>
    ///     The shipped conversion of the cascades' 0.008 m over a four hundred metre box, which is
    ///     what <c>VirtualShadowRenderer</c> publishes. Four orders of magnitude under
    ///     <see cref="Separation" />, so the fixture's lit and occluded cases are decided by the
    ///     depths and not by the bias.
    /// </remarks>
    const float ConstantBias = 0.00002f;

    /// <summary>The slope-scaled bias. Never reached: every query is asked at <c>NdotL = 1</c>.</summary>
    const float SlopeBias = 0.000025f;

    /// <summary>
    ///     How far a page's stored depth is put either side of the point's own, in normalised depth.
    /// </summary>
    /// <remarks>
    ///     A tenth is enormous — forty metres of a four hundred metre box — and deliberately so. What
    ///     is being asserted is that the lookup found the right page of the right level, not where a
    ///     comparison falls within a texel, so the two cases are placed where no bias, no filter and
    ///     no rounding can move a tap across.
    /// </remarks>
    const float Separation = 0.1f;

    /// <summary>Which physical page holds the occluding depths, and which the clear ones.</summary>
    const int OccludedSlot = 1;

    const int LitSlot = 3;

    /// <summary>The direction the sun's light travels — <c>VirtualShadowRenderer</c>'s own default.</summary>
    /// <remarks>
    ///     Tilted rather than straight down, because a light within a hair of the world's up axis
    ///     takes <see cref="VirtualShadowMap.Basis" />' other branch, and the shipped frames do not.
    /// </remarks>
    static Vector3 Light => Vector3.Normalize(new(-0.4f, -1f, -0.3f));

    /// <summary>Where the clipmap is centred.</summary>
    /// <remarks>
    ///     ⚠ <b>Off the origin on purpose, and the test refuses to run if it stops being.</b> The
    ///     fitted origin is the cell of the camera in the light's own page grid, so a camera at the
    ///     world origin fits every level with <c>Origin = (0, 0)</c> — for which <c>Vsm.Toroidal</c>
    ///     is the identity and the page address no longer depends on the field at all.
    ///     <see cref="The_fixture_is_asking_a_question_that_can_fail" /> is the guard.
    /// </remarks>
    static Vector3 Camera => new(37.5f, 2f, -21.25f);

    /// <summary>
    ///     How far the queried points are from the camera, which is what picks the level.
    /// </summary>
    /// <remarks>
    ///     <c>Vsm.WorldTexelSize</c> is <c>2 d / (scale × height)</c> and <c>Vsm.LevelFor</c> is
    ///     <c>ceil(log2(texel / firstTexel))</c>, so this is the distance whose footprint is three
    ///     level-zero texels — a ratio of three rather than of four, because <c>ceil</c> of a
    ///     logarithm that lands exactly on an integer is a coin toss between two levels the moment
    ///     anything about the arithmetic moves by an ulp.
    /// </remarks>
    static float Distance =>
        3f * VirtualShadowMap.TexelOf(0, FirstExtent) * ScreenHeightScale * ScreenHeight / 2f;

    /// <summary>One question, laid out as the probe declares it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Query {
        public Vector3 Position;
        public float ViewDistance;
        public Vector3 Normal;
        public float NdotL;
    }

    /// <summary>One answer — <c>DirectionalShadowSample</c>, padded to a <c>float4</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Answer {
        public float Visibility;
        public float Found;
        public float Pad0;
        public float Pad1;
    }

    /// <summary>What the fixture set up, so the assertions can say what they expected and why.</summary>
    sealed class Arrangement {
        public required VirtualShadowLevel[] Records { get; init; }
        public required uint[] Table { get; init; }
        public required float[] Atlas { get; init; }
        public required Query[] Queries { get; init; }
        public required Int2 OccludedPage { get; init; }
        public required Int2 LitPage { get; init; }
        public required int Level { get; init; }
    }

    /// <summary>
    ///     ⚠ The fixture's own premises, before any of the assertions below mean anything.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A harness built to prove a defect will prove one that is not there.</b> Every
    ///         assertion in this file is of the form "the shader found the page the host addressed" —
    ///         which is vacuous if the host addressed page zero of level zero of a map that never
    ///         moved, because then almost any misreading of the record lands on the same answer. So
    ///         the three things that make the question sharp are checked without a device: the level
    ///         is not the first, the origin is not zero, and the two queried points are in different
    ///         pages.
    ///     </para>
    ///     <para>
    ///         This runs everywhere, including on the machines where no Vulkan device can be opened,
    ///         which is the point: it is the half of the fixture that cannot be skipped into silence.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_fixture_is_asking_a_question_that_can_fail() {
        var arranged = Arrange();

        Assert.True(arranged.Level > 0, $"the level selection picked {arranged.Level}, so the stride is not under test");

        var origin = arranged.Records[arranged.Level].Origin;

        Assert.True(
            (origin.X & (VirtualShadowMap.PagesPerSide - 1)) != 0
            || (origin.Y & (VirtualShadowMap.PagesPerSide - 1)) != 0,
            $"level {arranged.Level} fitted with origin {origin.X}, {origin.Y}, which is the identity "
            + "for Vsm.Toroidal — so the page address would not depend on Origin and the field could "
            + "be transposed with the padding beside it without this fixture noticing. Move Camera."
        );

        Assert.True(
            arranged.OccludedPage.X != arranged.LitPage.X || arranged.OccludedPage.Y != arranged.LitPage.Y,
            "the two queried points landed in one page, so the lit and occluded cases read the same depths"
        );

        Assert.True(
            Marshal.SizeOf<VirtualShadowLevel>() == 96,
            $"the record is {Marshal.SizeOf<VirtualShadowLevel>()} bytes on the host and ninety-six on "
            + "the device, so every level after the first is read out of the middle of the one before it"
        );
    }

    /// <summary>
    ///     ⚠ The composed lookup decodes the host's records: the right level, the right page, the
    ///     right depth.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Four questions in one dispatch, and each is the control for the others. A point over a
    ///         page whose stored depth is nearer the light comes back shadowed; a point over a page
    ///         whose depths are further comes back lit; a point in a page the table calls absent comes
    ///         back <em>unfound</em> rather than lit, which is the distinction the whole feature rests
    ///         on; and a point outside the map's own volume comes back unfound too.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The unfound cases are what stop the found ones being an accident.</b> A shader
    ///         that answered <c>found = 1, visibility = 1</c> for everything — which is what reading
    ///         a page table at a mangled index does whenever the garbage happens not to be
    ///         <c>PageAbsent</c> — would satisfy the lit assertion on its own.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_lookup_reads_the_levels_the_host_wrote() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;

        var arranged = Arrange();
        var answers = Dispatch(owned, arranged);

        Clean();

        Assert.True(
            answers[0].Found == 1f,
            "the point over the occluding page came back unfound, so the lookup did not reach the page "
            + "the host addressed. " + Explain(arranged)
        );

        Assert.True(
            answers[0].Visibility == 0f,
            $"the point over the occluding page came back {answers[0].Visibility} lit, not shadowed. "
            + Explain(arranged)
        );

        Assert.True(
            answers[1].Found == 1f,
            "the point over the drawn page came back unfound, so the lookup did not reach the page the "
            + "host addressed. " + Explain(arranged)
        );

        Assert.True(
            answers[1].Visibility == 1f,
            $"the point over the drawn page came back {answers[1].Visibility} lit, not fully lit. "
            + Explain(arranged)
        );

        Assert.True(
            answers[2].Found == 0f,
            $"a point in a page the table calls absent came back found, with visibility "
            + $"{answers[2].Visibility}. An index built from a misread record lands somewhere else in "
            + "the table, and anything there that is not PageAbsent reads as a page. " + Explain(arranged)
        );

        Assert.Equal(1f, answers[2].Visibility);

        Assert.True(
            answers[3].Found == 0f,
            "a point outside every level's volume came back found, so Vsm.MapUv's containment test "
            + "was answered from a projection that is not the one the host wrote. " + Explain(arranged)
        );
    }

    // --- The arrangement ----------------------------------------------------

    /// <summary>Fits the map, addresses the pages and fills the atlas, all on the host.</summary>
    /// <remarks>
    ///     ⚠ <b>Through <see cref="VirtualShadowMap" /> and never by hand.</b> The host half of every
    ///     line the shader executes lives there — the projection, the page grid, the toroidal address,
    ///     the global index, the atlas origin — and arithmetic written out again here would be a third
    ///     implementation agreeing with this test rather than with the engine.
    /// </remarks>
    static Arrangement Arrange() {
        var records = new VirtualShadowLevel[Levels];

        for (var level = 0; level < Levels; level++) {
            records[level] = new() {
                ViewProjection = VirtualShadowMap.ClipmapProjection(level, FirstExtent, Camera, Light, DepthRange),
                First = (uint)(level * VirtualShadowMap.PagesPerMap),
                Kind = (uint)VirtualShadowKind.Clipmap,
                TexelWorldSize = VirtualShadowMap.TexelOf(level, FirstExtent),
                Light = 0u,
                Origin = VirtualShadowMap.ClipmapOrigin(level, FirstExtent, Camera, Light, DepthRange)
            };
        }

        var chosen = VirtualShadowMap.LevelFor(
            VirtualShadowMap.WorldTexelSize(Distance, ScreenHeightScale, ScreenHeight),
            FirstExtent,
            Levels
        );

        var record = records[chosen];
        var (right, _, _) = VirtualShadowMap.Basis(Light);

        // Three points inside the chosen level and one well outside it. The lateral spacing is a page
        // of the chosen level and a half — enough that no two of the three share a page, and small
        // enough that all three stay inside a window thirty-two pages across.
        var page = VirtualShadowMap.ExtentOf(chosen, FirstExtent) / VirtualShadowMap.PagesPerSide;

        var occludedAt = Camera;
        var litAt = Camera + (right * (page * 1.5f));
        var absentAt = Camera + (right * (page * 3f));
        var outsideAt = Camera + (right * VirtualShadowMap.ExtentOf(Levels, FirstExtent));

        Assert.True(VirtualShadowMap.PageOf(record.ViewProjection, occludedAt, out var occludedPage));
        Assert.True(VirtualShadowMap.PageOf(record.ViewProjection, litAt, out var litPage));
        Assert.True(VirtualShadowMap.PageOf(record.ViewProjection, absentAt, out var absentPage));
        Assert.False(VirtualShadowMap.PageOf(record.ViewProjection, outsideAt, out _));

        // Absent everywhere first. ⚠ All ones and not zero: zero is physical page zero, and a table
        // cleared to it says every virtual page in the scene is backed by the same one.
        var table = new uint[VirtualShadowMap.MaxPages];
        Array.Fill(table, VirtualShadowMap.PageAbsent);

        table[Index(record, occludedPage)] = OccludedSlot;
        table[Index(record, litPage)] = LitSlot;

        // absentPage is deliberately left at PageAbsent.
        var atlas = new float[AtlasPages * VirtualShadowMap.PageTexels * AtlasPages * VirtualShadowMap.PageTexels];

        // Reverse-Z: the texel holds the largest depth of any caster, the one nearest the light, and a
        // receiver is lit when its own depth is at least that. So a page filled *above* the receiver's
        // depth shadows it and one filled below does not — see `Lighting.ShadowTap`.
        Fill(atlas, OccludedSlot, MapDepth(record.ViewProjection, occludedAt) + Separation);
        Fill(atlas, LitSlot, MapDepth(record.ViewProjection, litAt) - Separation);

        return new() {
            Records = records,
            Table = table,
            Atlas = atlas,
            Queries = [Ask(occludedAt), Ask(litAt), Ask(absentAt), Ask(outsideAt)],
            OccludedPage = occludedPage,
            LitPage = litPage,
            Level = chosen
        };
    }

    /// <summary>Where a window cell's page is in the global numbering, address and all.</summary>
    static int Index(in VirtualShadowLevel record, Int2 page) =>
        VirtualShadowMap.IndexOf(record.First, VirtualShadowMap.ToroidalOf(page, record.Origin));

    /// <summary>A world position's device depth in a map — <c>Vsm.MapDepth</c>'s host half.</summary>
    static float MapDepth(in Matrix4x4 viewProjection, Vector3 world) {
        var clip = Matrix4x4.TransformVector4(new(world, 1f), viewProjection);

        return clip.Z / clip.W;
    }

    /// <summary>Fills a whole physical page with one depth.</summary>
    /// <remarks>
    ///     The whole page rather than the tap's own texels, because <c>VirtualShadowLookup.Look</c>
    ///     runs a 3 × 3 filter and clamps it a texel and a half inside the page. A page filled uniformly
    ///     makes every one of the nine taps the same answer, which is what lets the assertions be
    ///     <c>0</c> and <c>1</c> rather than a fraction somebody has to reason about.
    /// </remarks>
    static void Fill(float[] atlas, int slot, float depth) {
        var side = AtlasPages * VirtualShadowMap.PageTexels;
        var origin = VirtualShadowMap.AtlasOrigin(slot, AtlasPages);

        for (var y = 0; y < VirtualShadowMap.PageTexels; y++) {
            for (var x = 0; x < VirtualShadowMap.PageTexels; x++) {
                atlas[((origin.Y + y) * side) + origin.X + x] = depth;
            }
        }
    }

    /// <summary>One question about a point, facing the light.</summary>
    /// <remarks>
    ///     ⚠ <c>NdotL = 1</c> throughout, which is not laziness: it is what makes
    ///     <c>Lighting.NormalOffset</c> zero and <c>Lighting.ShadowBias</c> exactly
    ///     <see cref="ConstantBias" />. The point the shader looks up is then the point the host
    ///     addressed, so a disagreement is a disagreement about the record and not about an offset
    ///     the fixture would have had to reproduce.
    /// </remarks>
    static Query Ask(Vector3 position) =>
        new() { Position = position, ViewDistance = Distance, Normal = -Light, NdotL = 1f };

    static string Explain(Arrangement arranged) =>
        $"Level {arranged.Level} of {Levels} was fitted with origin "
        + $"{arranged.Records[arranged.Level].Origin.X}, {arranged.Records[arranged.Level].Origin.Y} and "
        + $"first {arranged.Records[arranged.Level].First}; the occluded point is in page "
        + $"{arranged.OccludedPage.X}, {arranged.OccludedPage.Y} and the lit one in "
        + $"{arranged.LitPage.X}, {arranged.LitPage.Y}. A record the two sides lay out differently is "
        + "read at the wrong stride or with Origin transposed against the tail padding, and either "
        + "addresses every page of every level into another level's world.";

    // --- The device ---------------------------------------------------------

    /// <summary>Uploads the arrangement, dispatches the probe and reads the answers back.</summary>
    static Answer[] Dispatch(Fixture fixture, Arrangement arranged) {
        var device = fixture.Device;
        var effect = Compiled(device);

        // ⚠ MemoryMarshal over the host's own array, which is the claim: this is what
        // `UploadBuffer<VirtualShadowLevel>` does inside `VirtualShadowAtlas.Begin`, so the bytes the
        // shader decodes are the bytes the engine would have sent.
        var levelBytes = MemoryMarshal.AsBytes(arranged.Records.AsSpan()).ToArray();

        Assert.True(
            levelBytes.Length == Levels * 96,
            $"{Levels} records marshalled to {levelBytes.Length} bytes, and the device reads them at a "
            + "stride of ninety-six"
        );

        var levels = Buffer(fixture, levelBytes, BufferUsage.Storage, "probe levels");
        var table = Buffer(fixture, MemoryMarshal.AsBytes(arranged.Table.AsSpan()).ToArray(), BufferUsage.Storage, "probe page table");
        var queries = Buffer(fixture, MemoryMarshal.AsBytes(arranged.Queries.AsSpan()).ToArray(), BufferUsage.Storage, "probe queries");

        var atlasSide = AtlasPages * VirtualShadowMap.PageTexels;
        var atlasBytes = MemoryMarshal.AsBytes(arranged.Atlas.AsSpan()).ToArray();

        var pages = fixture.Owned(
            "probe pages",
            TextureUsage.Sampled | TextureUsage.CopyDestination,
            PixelFormat.R32Float,
            atlasSide,
            atlasSide
        );

        var staging = Buffer(fixture, atlasBytes, BufferUsage.CopySource, "probe pages staging");

        var answerBytes = arranged.Queries.Length * Marshal.SizeOf<Answer>();

        var results = device.CreateBuffer(
            new(answerBytes, BufferUsage.Storage | BufferUsage.CopySource, MemoryAccess.DeviceLocal, "probe results")
        );

        var readback = device.CreateBuffer(
            new(answerBytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "probe readback")
        );

        fixture.Owns(() => device.Destroy(results));
        fixture.Owns(() => device.Destroy(readback));

        // ⚠ Point and clamp, exactly what `VirtualShadowAtlas.Publish` binds. The lookup compares
        // after sampling, so a linear sampler would blend four depths into a value no surface wrote —
        // and the fixture's uniform pages would hide that everywhere except the seams, which is the
        // worst place for a difference to be invisible.
        using var samplers = new SamplerCache(device);

        // Two blocks, because the lookup's own parameters are PerFrame and the probe's questions are
        // PerDraw — WaterSurfaceProbe's arrangement, and the composition is what puts them apart.
        var perFrame = effect.BlockOf(DescriptorSetSlot.PerFrame);
        var perDraw = effect.BlockOf(DescriptorSetSlot.PerDraw);

        var frameConstants = new byte[Math.Max(4, perFrame.Size)];
        var drawConstants = new byte[Math.Max(4, perDraw.Size)];

        WriteInt(perFrame, frameConstants, "shadowLevelCount", Levels);
        WriteInt(perFrame, frameConstants, "shadowClipmapLevels", Levels);
        WriteInt(perFrame, frameConstants, "shadowAtlasPages", AtlasPages);
        WriteInt(perFrame, frameConstants, "shadowScreenHeight", ScreenHeight);
        WriteFloat(perFrame, frameConstants, "shadowFirstTexel", VirtualShadowMap.TexelOf(0, FirstExtent));
        WriteFloat(perFrame, frameConstants, "shadowScreenHeightScale", ScreenHeightScale);
        WriteFloat(perFrame, frameConstants, "shadowPageConstantBias", ConstantBias);
        WriteFloat(perFrame, frameConstants, "shadowPageSlopeBias", SlopeBias);
        WriteInt(perDraw, drawConstants, "queryCount", arranged.Queries.Length);

        var frameBlock = Buffer(fixture, frameConstants, BufferUsage.Uniform, "probe frame block");
        var drawBlock = Buffer(fixture, drawConstants, BufferUsage.Uniform, "probe draw block");

        var frameSet = device.CreateDescriptorSet(effect.SetLayouts[(int)DescriptorSetSlot.PerFrame], "probe frame");
        var drawSet = device.CreateDescriptorSet(effect.SetLayouts[(int)DescriptorSetSlot.PerDraw], "probe draw");

        fixture.Owns(() => device.Destroy(frameSet));
        fixture.Owns(() => device.Destroy(drawSet));

        device.UpdateDescriptorSet(
            frameSet,
            [
                DescriptorWrite.Uniform(perFrame.Binding, frameBlock, 0, frameConstants.Length),
                DescriptorWrite.Storage(Binding(effect, "shadowLevels"), levels),
                DescriptorWrite.Storage(Binding(effect, "shadowPageTable"), table),
                DescriptorWrite.Texture(Binding(effect, "shadowPages"), pages.View),
                DescriptorWrite.SamplerAt(Binding(effect, "shadowPageSampler"), samplers.PointClamp)
            ]
        );

        device.UpdateDescriptorSet(
            drawSet,
            [
                DescriptorWrite.Uniform(perDraw.Binding, drawBlock, 0, drawConstants.Length),
                DescriptorWrite.Storage(Binding(effect, "queries"), queries),
                DescriptorWrite.Storage(Binding(effect, "results"), results)
            ]
        );

        var shader = device.CreateShader(
            ShaderStage.Compute,
            effect.Stages.Single(stage => stage.Stage == ShaderStage.Compute).Bytecode.AsSpan(),
            "VirtualShadowProbe"
        );

        fixture.Owns(() => device.Destroy(shader));

        PipelineHandle pipeline;

        try {
            pipeline = device.CreateComputePipeline(new(shader, effect.Layout, "VirtualShadowProbe"));
        } catch (VulkanException error) {
            throw new InvalidOperationException(
                $"{error.Message} The layers said: {string.Join(Environment.NewLine, VulkanDiagnostics.Messages)}",
                error
            );
        }

        fixture.Owns(() => device.Destroy(pipeline));

        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "virtual shadow probe")) {
            // The atlas, before anything samples it. A copy into a texture cannot be recorded inside a
            // render pass, and this list has none — but the layout transitions are still the fixture's
            // to make, because nothing here goes through the render graph.
            commands.Barrier(new([], [new(pages.Texture, ResourceState.Undefined, ResourceState.CopyDestination)]));
            commands.CopyBufferToTexture(staging, 0, new(pages.Texture), new(atlasSide, atlasSide, 1));
            commands.Barrier(new([], [new(pages.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)]));
            commands.Barrier(new([new(results, ResourceState.Undefined, ResourceState.ShaderWrite)], []));

            commands.BindPipeline(pipeline);
            commands.BindDescriptorSet(DescriptorSetSlot.PerFrame, frameSet);
            commands.BindDescriptorSet(DescriptorSetSlot.PerDraw, drawSet);
            commands.Dispatch((arranged.Queries.Length + 63) / 64, 1, 1);

            commands.Barrier(new([new(results, ResourceState.ShaderWrite, ResourceState.CopySource)], []));
            commands.CopyBuffer(results, 0, readback, 0, answerBytes);
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var bytes = new byte[answerBytes];
        device.Read(readback, 0, bytes);

        return MemoryMarshal.Cast<byte, Answer>(bytes).ToArray();
    }

    /// <summary>A buffer with contents, disposed with the fixture.</summary>
    static BufferHandle Buffer(Fixture fixture, byte[] bytes, BufferUsage usage, string name) {
        var handle = fixture.Device.CreateBuffer(
            new(Math.Max(bytes.Length, 16), usage, MemoryAccess.HostUpload, name)
        );

        fixture.Device.Write(handle, 0, bytes);
        fixture.Owns(() => fixture.Device.Destroy(handle));

        return handle;
    }

    static void WriteInt(EffectBlock declared, byte[] constants, string name, int value) =>
        BitConverter.TryWriteBytes(constants.AsSpan(Member(declared, name)), value);

    static void WriteFloat(EffectBlock declared, byte[] constants, string name, float value) =>
        BitConverter.TryWriteBytes(constants.AsSpan(Member(declared, name)), value);

    /// <summary>Where a value parameter sits in its block, by the name the shader gave it.</summary>
    static int Member(EffectBlock declared, string name) {
        var member = declared.Members.FirstOrDefault(candidate => Named(candidate.Key.Name, name));

        Assert.True(
            member.Key is not null,
            $"the probe declares no '{name}': {string.Join(", ", declared.Members.Select(m => m.Key.Name))}"
        );

        return member.Offset;
    }

    /// <summary>Which binding the shader gave a name, rather than a number written down here.</summary>
    /// <remarks>
    ///     ⚠ By suffix, because composing qualifies what the slot brought: <c>shadowLevels</c> is
    ///     <c>VirtualShadowLookup</c>'s declaration and reaches the probe under the slot's name.
    ///     Matching the bare name finds nothing and fails a long way from the composition that
    ///     renamed it.
    /// </remarks>
    static uint Binding(Effect effect, string name) {
        var found = effect.Bindings.Where(binding => Named(binding.Name, name)).ToArray();

        Assert.True(
            found.Length == 1,
            $"the probe has {found.Length} bindings called '{name}': "
            + string.Join(", ", effect.Bindings.Select(binding => binding.Name))
        );

        return found[0].Binding;
    }

    static bool Named(string declared, string name) =>
        declared == name || declared.EndsWith("." + name, StringComparison.Ordinal);

    /// <summary>Compiles <c>VirtualShadowProbe.rvn</c> against the shipped library.</summary>
    /// <remarks>
    ///     ⚠ Three packages and one file, rather than the whole library: the material tree declares
    ///     compose slots that have to be bound whether or not this shader reaches them, so handing it
    ///     everything would compile nothing at all. <c>Shading</c> and <c>Geometry</c> are there
    ///     because <c>VirtualShadows.rvn</c> imports them — <c>Lighting.ShadowPcf3x3</c> and
    ///     <c>Transform.NdcToUv</c> are the two lines the lookup's answer actually turns on.
    /// </remarks>
    static Effect Compiled(VulkanDevice device) {
        var path = Path.Combine(AppContext.BaseDirectory, "Shaders", "VirtualShadowProbe.rvn");

        Assert.True(File.Exists(path), $"the probe shader is not beside the binary at {path}");

        var data = RavenEffects
            .Only(["Core", "Geometry", "Shading"], Path.Combine("VirtualShadows", "VirtualShadows.rvn"), path)
            .TryGet(EffectKey.Of("VirtualShadowProbe"));

        Assert.NotNull(data);

        return new EffectLoader(device).Load(data!);
    }

    /// <summary>Refuses an answer produced alongside validation errors.</summary>
    static void Clean() {
        if (VulkanDiagnostics.ErrorCount > 0) {
            throw new InvalidOperationException(
                "The dispatch produced validation errors, so what came back means nothing: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }
    }

    /// <summary>Skips when there is no device, unless the environment insists on one.</summary>
    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
    }
}
