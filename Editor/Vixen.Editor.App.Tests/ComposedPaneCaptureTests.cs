// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Vixen.Geometry;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Ecs;
using Vixen.Shaders.Generated;
using Vixen.Ui.Desktop;
using Vixen.Ui.Renderer;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Four composed panes, on a real device, written out as a picture to look at.</summary>
/// <remarks>
///     <para>
///         <b>The deliverable a passing suite is not.</b> Everything in
///         <see cref="ComposedPaneTests" /> is a claim about counters, indices and names, and this
///         engine's commonest defect is a frame in which every one of those is healthy and no
///         fragment survives — a water surface submitting forty-five patches, a lake four decades too
///         dim, a "split" frame bit-identical to the unsplit one. So this renders the arrangement on
///         the Vulkan backend and writes what came out.
///     </para>
///     <para>
///         ⚠ <b>Off unless it is asked for, by <c>VIXEN_PANE_CAPTURE=&lt;directory&gt;</c>.</b> It
///         opens a device, compiles the editor's Raven library and writes files — none of which
///         belongs in a suite that runs on every push, and all of which is what somebody looking at
///         the viewport wants. The editor's own host cannot do this: it opens a window.
///     </para>
///     <para>
///         ⚠ <b>The harness lends the frame its own colour targets, under the pane's own names.</b> A
///         <see cref="FramePresenter" />'s texture is <c>ColourTarget | Sampled</c> because the
///         interface samples it, and a texture without <c>CopySource</c> is one no readback may
///         touch. An import wins over a declaration <em>and</em> over an earlier import of the same
///         name, so writing over the presenter's import after it prepared is the documented way to
///         take the picture without changing what the editor allocates for a pane it is only going to
///         sample. What is asserted below is that the four textures came back <em>different</em>,
///         which is the claim a one-view frame fails.
///     </para>
/// </remarks>
public sealed class ComposedPaneCaptureTests {
    /// <summary>Where the pictures go, or null when nobody asked for any.</summary>
    static string? Destination => Environment.GetEnvironmentVariable("VIXEN_PANE_CAPTURE");

    /// <summary>The four panes of a quad layout, each drawing its own camera.</summary>
    [Fact]
    public void A_quad_layout_draws_four_panes() {
        var directory = Destination;

        Assert.SkipWhen(
            string.IsNullOrEmpty(directory),
            "VIXEN_PANE_CAPTURE names no directory, so there is nowhere to write a picture."
        );

        Assert.SkipUnless(
            VulkanDevice.TryCreate(new(), out var device, out var reason),
            reason ?? "no Vulkan"
        );

        using (device!) {
            Capture(device, directory!, "panes-shaded.png", modes: null);

            // ⚠ And with the modes differing per pane, which is the half a shared stage would break.
            // Two panes in wireframe are one stage index and two views; the pipeline cache bakes a
            // stage's state in on the first miss, so a mode that mutated a shared stage would change
            // the menu and not the picture.
            Capture(
                device,
                directory!,
                "panes-modes.png",
                [ViewMode.Shaded, ViewMode.Wireframe, ViewMode.Wireframe, ViewMode.Shaded]
            );
        }
    }

    /// <summary>The same pane, the same camera, the crate unselected and then selected.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one claim a structural test cannot make about a selection affordance.</b> Every
    ///         counter in a composed pane is identical whether or not the thing in it is selected —
    ///         the same objects extract, the same draws record, the same targets come back — so
    ///         "selection is invisible" is a defect with no failing assertion anywhere until the two
    ///         frames are compared as pixels. Which is what this does: two runs of one scene from one
    ///         camera, differing only in <c>SceneDocument.Selection</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The gizmo is switched off for the comparison, and that is the whole design of this
    ///         test.</b> With it on, selecting the crate changes thousands of pixels — a gizmo is
    ///         lines, lines survive the composed pane, and it appears exactly where the selection is.
    ///         So "the two frames differ" is satisfied by the build this test was written against, in
    ///         which the crate itself is drawn identically both times: that build's entire difference
    ///         is the gizmo, plus eleven pixels of a parent link changing hue. The claim has to be
    ///         about the <em>object</em>, so what is asserted is that the difference surrounds the
    ///         crate's own silhouette — its projected box, to within a quarter of that box on every
    ///         side — which a gizmo pointing at it does not do and a sliver of parent link does not
    ///         either.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The picture keeps the gizmo, because the picture is the argument.</b> What somebody
    ///         is looking for in it is the thing the brief describes: a pane where the gizmo says an
    ///         object is selected and the object does not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not gated on <c>VIXEN_PANE_CAPTURE</c>, unlike the quad above.</b> The picture is;
    ///         the comparison is the regression that put this here and it costs one device and eight
    ///         frames. A machine with no Vulkan skips it either way.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A session per frame rather than one selected between frames.</b> The frame's
    ///         material descriptors, the geometry residency and the pipeline cache all carry state
    ///         across frames, so a second capture out of the same session is a picture of a warmer
    ///         renderer as well as of a different selection — and the difference asserted here would
    ///         then have two possible causes.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_selected_object_does_not_look_like_an_unselected_one() {
        Assert.SkipUnless(
            VulkanDevice.TryCreate(new(), out var device, out var reason),
            reason ?? "no Vulkan"
        );

        using (device!) {
            var plain = Selected(device, select: false, gizmo: false, out _);
            var marked = Selected(device, select: true, gizmo: false, out var box);

            Assert.True(
                Interesting(plain),
                "the pane came back a single flat colour, so nothing was drawn to select in the first place"
            );

            if (Destination is { Length: > 0 } directory) {
                Directory.CreateDirectory(directory);

                // ⚠ Three, and the third is the one worth arguing about. `SceneShow.Bounds` already
                // draws a box round every shaped entity and already turns it amber for a selected
                // one, so the question a cage round the selection has to answer in a picture rather
                // than in prose is whether somebody with that flag on now sees two things that mean
                // different things and look the same.
                PngCodec.Save(
                    Path.Combine(directory, "selection-composed.png"),
                    Row(
                        Selected(device, select: false, gizmo: true, out _),
                        Selected(device, select: true, gizmo: true, out _),
                        Selected(device, select: true, gizmo: true, out _, bounds: true)
                    )
                );
            }

            var changed = Changed(plain, marked, out var region);

            Assert.True(
                changed > 0,
                "a selected object is drawn exactly like an unselected one in a composed pane"
            );

            // ⚠ A floor as well as a shape, because four stray pixels at four corners satisfy the
            // shape and are not an affordance anybody can see.
            Assert.True(
                changed >= 200,
                $"only {changed} pixels changed when the crate was selected, which is not something a person sees"
            );

            var slack = 0.25f * MathF.Max(box.Maximum.X - box.Minimum.X, box.Maximum.Y - box.Minimum.Y);

            Assert.True(
                MathF.Abs(region.Minimum.X - box.Minimum.X) <= slack
                && MathF.Abs(region.Minimum.Y - box.Minimum.Y) <= slack
                && MathF.Abs(region.Maximum.X - box.Maximum.X) <= slack
                && MathF.Abs(region.Maximum.Y - box.Maximum.Y) <= slack,
                $"what changed covers {region.Minimum}..{region.Maximum} and the crate covers "
                + $"{box.Minimum}..{box.Maximum}, so the affordance is not around the object"
            );
        }
    }

    /// <summary>Which pixels two frames disagree about, and the box they fall in.</summary>
    /// <remarks>
    ///     ⚠ <b>Any channel differing by anything at all counts.</b> A threshold here would be a
    ///     second thing to get wrong, and the two frames are the same scene from the same camera on
    ///     the same device — there is no noise floor to clear.
    /// </remarks>
    static int Changed(in Bitmap left, in Bitmap right, out BoundingBox region) {
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        var count = 0;

        for (var y = 0; y < left.Height; y++) {
            for (var x = 0; x < left.Width; x++) {
                var offset = left.Offset(x, y);

                if (left.Pixels[offset] == right.Pixels[offset]
                    && left.Pixels[offset + 1] == right.Pixels[offset + 1]
                    && left.Pixels[offset + 2] == right.Pixels[offset + 2]) {
                    continue;
                }

                low = Vector3.Min(low, new(x, y, 0f));
                high = Vector3.Max(high, new(x, y, 0f));
                count++;
            }
        }

        region = count == 0 ? default : new BoundingBox(low, high);

        return count;
    }

    /// <summary>Where the crate's own extent lands on screen, as a box in pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>Projected from the camera rather than measured off the picture.</b> Measuring it would
    ///     mean segmenting the crate out of a shaded frame, which is a second algorithm to be wrong —
    ///     and the eight corners through the same matrix the frame drew with is the answer the frame
    ///     itself used.
    /// </remarks>
    static BoundingBox Projected(EditorCamera camera, in BoundingBox bounds, in Matrix4x4 transform, int width, int height) {
        var centre = (bounds.Minimum + bounds.Maximum) * 0.5f;
        var extent = (bounds.Maximum - bounds.Minimum) * 0.5f;
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);

        for (var index = 0; index < 8; index++) {
            var local = centre + new Vector3(
                (index & 1) == 0 ? -extent.X : extent.X,
                (index & 2) == 0 ? -extent.Y : extent.Y,
                (index & 4) == 0 ? -extent.Z : extent.Z
            );

            Assert.True(
                camera.TryProject(Matrix4x4.TransformPosition(local, transform), width, height, out var point),
                "a corner of the crate is behind the camera, so the pane is not looking at it"
            );

            low = Vector3.Min(low, new(point.X, point.Y, 0f));
            high = Vector3.Max(high, new(point.X, point.Y, 0f));
        }

        return new(low, high);
    }

    /// <summary>One composed pane of the seeded scene, framed on the crate, selected or not.</summary>
    static Bitmap Selected(
        VulkanDevice device,
        bool select,
        bool gizmo,
        out BoundingBox crateOnScreen,
        bool bounds = false
    ) {
        using var session = EditorSession.Start();

        session.Application.GraphicsDevice = device;
        session.Frame();
        session.Settle();

        var panes = session.Application.Viewports;

        Assert.NotEmpty(panes);

        var scene = session.Application.Scene;

        // ⚠ By name, because the seed's order is not a contract. `Seed` puts a cube called "Crate" at
        // (1.5, 0.5, 0) under "Ground", and it is the one entity in the default scene with an extent
        // big enough on screen for a rim, a cage or a tint to be visible in a picture at all.
        var crate = scene.Entities.FirstOrDefault(entity => scene.NameOf(entity) == "Crate");

        Assert.NotEqual(default, crate);

        // ⚠ And settled afterwards, because half of what a selection does in this editor happens in
        // the *next* update rather than in the setter: the transform gizmo learns its target from
        // `EditorApplication.Update`, so a picture taken without that frame is a picture of a pane
        // whose gizmo has not appeared yet — which understates what the editor already draws and
        // would make any affordance added here look better than it is.
        if (select) {
            scene.Selection.Set(crate);
            session.Settle();
        }

        // ⚠ The camera is aimed identically in both runs, and last, after everything that could
        // move it. `SceneViewport.OrbitAround` defaults to the selection, so a camera set before the
        // selection and then settled is a camera the two runs can disagree about — and the
        // comparison below would then pass on the parallax alone.
        var pane = panes[0];

        pane.Camera.Focus(new(new Vector3(0.9f, -0.1f, -0.6f), new Vector3(2.1f, 1.1f, 0.6f)));
        pane.Camera.Yaw = 0.75f;
        pane.Camera.Pitch = -0.3f;

        if (!gizmo) {
            pane.Show &= ~SceneShow.Gizmos;
        }

        if (bounds) {
            pane.Show |= SceneShow.Bounds;
        }

        var world = session.Application.Frame!;

        Assert.Null(world.MissingBinding);

        var shaders = new Shaders(device);
        var renderer = new UiRenderer(device, shaders.Ui, new RenderOutput([PixelFormat.Bgra8UNorm]));

        var presenter = new FramePresenter(
            device,
            world,
            shaders.Lines,
            shaders.Meshes,
            FramePresenter.ColourFormat,
            2
        );

        var presenters = new List<FramePresenter> { presenter };
        var targets = new List<Target>();

        var pool = new TransientResourcePool(device);
        var graph = new RenderGraph(device, pool);

        try {
            // Four, for the reason the quad capture gives: the set-1 layout is adopted off the first
            // shader to resolve, which happens inside the first build.
            for (var pass = 0; pass < 4; pass++) {
                Frame(device, graph, world, session, renderer, [pane], presenters, targets, capture: pass == 3);
                graph.Reset();
            }

            Assert.True(world.IsComplete, $"set 0 never bound; nothing filled: {world.MissingBinding}");
            Assert.Single(targets);

            // ⚠ Measured off the presenter's size rather than the control's, because a pane rounds to
            // render pixels through `RenderScale` and the picture is the presenter's target.
            crateOnScreen = Projected(
                pane.Camera,
                MeshPrimitives.Create(PrimitiveKind.Cube).Bounds,
                scene.World.Read<WorldTransform>(crate).Value,
                presenter.Width,
                presenter.Height
            );

            return targets[0].Read(device);
        } finally {
            foreach (var target in targets) {
                target.Dispose(device);
            }

            presenter.Dispose();
            renderer.Dispose();
            pool.Dispose();
            shaders.Dispose(device);
        }
    }

    /// <summary>Panes side by side, with a rule between them.</summary>
    static Bitmap Row(params Bitmap[] panes) {
        const int Gap = 4;

        var width = panes.Sum(pane => pane.Width) + (Gap * (panes.Length - 1));
        var height = panes.Max(pane => pane.Height);
        var pixels = new byte[width * height * 4];

        for (var index = 3; index < pixels.Length; index += 4) {
            pixels[index] = byte.MaxValue;
        }

        var composite = new Bitmap(width, height, pixels);
        var x = 0;

        foreach (var pane in panes) {
            Blit(composite, pane, x, 0);
            x += pane.Width + Gap;
        }

        return composite;
    }

    /// <summary>Renders one arrangement and writes it out as a two-by-two composite.</summary>
    static void Capture(VulkanDevice device, string directory, string file, ViewMode[]? modes) {
        using var session = EditorSession.Start();

        session.Application.GraphicsDevice = device;
        session.Frame();
        session.Run("scene.panes-quad");
        session.Settle();

        var panes = session.Application.Viewports;

        Assert.Equal(EditorWorldRenderer.MaxPanes, panes.Count);

        // ⚠ Four cameras that disagree, because four panes at one orbit are four panes whose views
        // hold the same matrix — which is exactly the picture a single-view frame would also produce.
        for (var index = 0; index < panes.Count; index++) {
            panes[index].Camera.Yaw = 0.9f * index;
            panes[index].Camera.Pitch = -0.35f - (0.1f * index);
            panes[index].Camera.Distance = 7f + (index * 1.5f);

            if (modes is null) {
                continue;
            }

            // ⚠ Registered rather than merely set. `ViewModes.Resolve` falls back to the shaded tree
            // for a mode nothing authored one for, so a device without `fillModeNonSolid` would draw
            // four shaded panes and this capture would call them four modes.
            Assert.Contains(modes[index], panes[index].Modes.Registered);

            panes[index].Modes.Current = modes[index];
        }

        var world = session.Application.Frame!;

        Assert.Null(world.MissingBinding);

        var shaders = new Shaders(device);
        var renderer = new UiRenderer(device, shaders.Ui, new RenderOutput([PixelFormat.Bgra8UNorm]));
        var presenters = new List<FramePresenter>();
        var targets = new List<Target>();

        var pool = new TransientResourcePool(device);
        var graph = new RenderGraph(device, pool);

        try {
            for (var index = 0; index < panes.Count; index++) {
                presenters.Add(
                    new FramePresenter(
                        device,
                        world,
                        shaders.Lines,
                        shaders.Meshes,
                        FramePresenter.ColourFormat,
                        ((ulong) index * 2) + 2,
                        index
                    )
                );
            }

            // ⚠ Several frames, and the number is not decoration. The set-1 layout is adopted off the
            // first shader to resolve, which happens inside the first build — so a one-frame capture
            // is a picture of a frame that has not bound a per-view set yet, and the pane is black for
            // a reason that has nothing to do with the arrangement.
            for (var pass = 0; pass < 4; pass++) {
                Frame(device, graph, world, session, renderer, panes, presenters, targets, capture: pass == 3);
                graph.Reset();
            }

            Assert.True(world.IsComplete, $"set 0 never bound; nothing filled: {world.MissingBinding}");
            Assert.Equal(EditorWorldRenderer.MaxPanes, targets.Count);

            // ⚠ The claim the pixels below cannot make, asserted on the driver that drew them.
            // Under a one-view arrangement the four panes still come back as four *different*
            // images — the grid, the axes and the gizmo are drawn from each pane's own camera by the
            // tool pass whatever the frame did — so "no two panes are the same pixels" is satisfied
            // by a frame whose geometry is all one camera's. The picture shows it plainly to a
            // person, because the objects stop standing on the grid; nothing in a byte comparison
            // does. What does is four views in the frame's list, carrying four indices.
            var views = world.Compositor.Views;

            Assert.Equal(EditorWorldRenderer.MaxPanes, views.Count);
            Assert.Equal(views.Count, views.Select(view => view.Index).Distinct().Count());

            for (var index = 0; index < panes.Count; index++) {
                Assert.Contains(world.ViewOf(index), views);
            }

            var images = targets.Select(target => target.Read(device)).ToList();

            for (var index = 0; index < images.Count; index++) {
                Assert.True(
                    Interesting(images[index]),
                    $"pane {index} came back a single flat colour, so it drew nothing at all"
                );
            }

            // ⚠ The four have to differ from each other, which is the claim the arrangement is about.
            for (var left = 0; left < images.Count; left++) {
                for (var right = left + 1; right < images.Count; right++) {
                    Assert.False(
                        images[left].Pixels.AsSpan().SequenceEqual(images[right].Pixels),
                        $"panes {left} and {right} are the same pixels, so they drew one camera"
                    );
                }
            }

            Directory.CreateDirectory(directory);
            PngCodec.Save(Path.Combine(directory, file), Quad(images));
        } finally {
            foreach (var target in targets) {
                target.Dispose(device);
            }

            foreach (var presenter in presenters) {
                presenter.Dispose();
            }

            renderer.Dispose();
            pool.Dispose();
            shaders.Dispose(device);
        }
    }

    /// <summary>One frame, in <c>EditorHost.Record</c>'s order.</summary>
    static void Frame(
        VulkanDevice device,
        RenderGraph graph,
        EditorWorldRenderer world,
        EditorSession session,
        UiRenderer renderer,
        IReadOnlyList<SceneViewport> panes,
        IReadOnlyList<FramePresenter> presenters,
        List<Target> targets,
        bool capture
    ) {
        device.BeginFrame();

        using var commands = device.BeginCommandList(QueueKind.Graphics, "panes");

        var composing = new List<(FramePresenter Presenter, SceneViewport Viewport)>();

        for (var index = 0; index < panes.Count; index++) {
            if (presenters[index].Resize(panes[index], renderer)) {
                composing.Add((presenters[index], panes[index]));
            }
        }

        world.Begin(commands);

        var trees = new List<SceneRenderer>();
        var reference = Int2.Zero;

        foreach (var (presenter, viewport) in composing) {
            presenter.Upload(commands, session.Application.Scene, viewport);

            if (presenter.Prepare(viewport, out var tree)) {
                trees.Add(tree);
            }

            reference = new(Math.Max(reference.X, presenter.Width), Math.Max(reference.Y, presenter.Height));
        }

        // ⚠ After every pane has prepared and before the one build, because a build is a snapshot of
        // the imports. See this file's remarks for why the harness lends its own colour at all.
        if (capture) {
            for (var index = 0; index < composing.Count; index++) {
                var target = new Target(device, composing[index].Presenter.Width, composing[index].Presenter.Height);

                targets.Add(target);

                world.Compositor.Imports[FramePresenter.Colour(index)] = new(
                    target.Texture,
                    target.View,
                    target.Description,
                    ResourceState.Undefined,
                    ResourceState.CopySource
                );
            }
        }

        if (trees.Count > 0) {
            world.Compose(graph, trees, reference, device.WaitIdle);
        }

        graph.Execute(commands);

        if (capture) {
            foreach (var target in targets) {
                target.Copy(commands);
            }
        }

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        device.EndFrame();
        device.WaitIdle();
    }

    /// <summary>Whether a pane is more than one flat colour.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion a clear-colour frame fails and every counter in it passes.</b> A pane
    ///     that composed, culled, sorted and recorded without a surviving fragment comes back as the
    ///     pass's clear colour, uniformly — which is a picture, and therefore the kind of failure
    ///     somebody attributes to the lighting.
    /// </remarks>
    static bool Interesting(Bitmap image) {
        for (var index = 4; index < image.Pixels.Length; index += 4) {
            if (image.Pixels[index] != image.Pixels[0]
                || image.Pixels[index + 1] != image.Pixels[1]
                || image.Pixels[index + 2] != image.Pixels[2]) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lays the four panes out the way the scene panel does, with a rule between them.</summary>
    static Bitmap Quad(IReadOnlyList<Bitmap> panes) {
        const int Gap = 4;

        var left = Math.Max(panes[0].Width, panes[2].Width);
        var right = Math.Max(panes[1].Width, panes[3].Width);
        var top = Math.Max(panes[0].Height, panes[1].Height);
        var bottom = Math.Max(panes[2].Height, panes[3].Height);

        var width = left + Gap + right;
        var height = top + Gap + bottom;
        var pixels = new byte[width * height * 4];

        for (var index = 3; index < pixels.Length; index += 4) {
            pixels[index] = byte.MaxValue;
        }

        var composite = new Bitmap(width, height, pixels);

        Blit(composite, panes[0], 0, 0);
        Blit(composite, panes[1], left + Gap, 0);
        Blit(composite, panes[2], 0, top + Gap);
        Blit(composite, panes[3], left + Gap, top + Gap);

        return composite;
    }

    static void Blit(in Bitmap into, in Bitmap from, int x, int y) {
        for (var row = 0; row < from.Height; row++) {
            var source = from.Offset(0, row);
            var destination = into.Offset(x, y + row);

            Array.Copy(from.Pixels, source, into.Pixels, destination, from.Width * 4);
        }
    }

    /// <summary>A colour target the harness lends the frame, and the buffer it comes back through.</summary>
    sealed class Target {
        public Target(IGraphicsDevice device, int width, int height) {
            Width = width;
            Height = height;

            Description = new(
                FramePresenter.ColourFormat,
                width,
                height,
                TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource,
                Name: "capture colour"
            );

            Texture = device.CreateTexture(Description);
            View = device.CreateTextureView(Texture);

            Readback = device.CreateBuffer(
                new(width * height * 4, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "capture readback")
            );
        }

        public int Width { get; }
        public int Height { get; }
        public TextureDescription Description { get; }
        public TextureHandle Texture { get; }
        public TextureViewHandle View { get; }
        public BufferHandle Readback { get; }

        public void Copy(ICommandList commands) =>
            commands.CopyTextureToBuffer(new(Texture), new(Width, Height, 1), Readback, 0);

        public Bitmap Read(IGraphicsDevice device) {
            var pixels = new byte[Width * Height * 4];

            device.Read(Readback, 0, pixels);

            return new(Width, Height, pixels);
        }

        public void Dispose(IGraphicsDevice device) {
            device.Destroy(Readback);
            device.Destroy(View);
            device.Destroy(Texture);
        }
    }

    /// <summary>The editor's own tool modules, read from beside the test binary.</summary>
    sealed class Shaders {
        readonly List<ShaderHandle> made = [];

        public Shaders(IGraphicsDevice device) {
            Lines = new(
                Load(device, ShaderStage.Vertex, "LineVertex.vert.spv"),
                Load(device, ShaderStage.Fragment, "LineFragment.frag.spv")
            ) {
                Locations = new(LineVertexKeys.PositionLocation, LineVertexKeys.VertexColourLocation)
            };

            Meshes = new(
                Load(device, ShaderStage.Vertex, "Mesh.vert.spv"),
                Load(device, ShaderStage.Fragment, "Mesh.frag.spv")
            ) {
                Locations = new(MeshKeys.PositionLocation, MeshKeys.NormalLocation, MeshKeys.VertexColourLocation)
            };

            // ⚠ **The library, exactly as `EditorHost` does — including its `Locations`.** Left at
            // the default every attribute is at location zero, which a recording device accepts and
            // a driver refuses: `vkCreateGraphicsPipelines` with `ErrorInitializationFailed`, which
            // is how that was found. The numbers come out of Raven's reflection inside
            // `UiShaderLibrary` rather than being written down here.
            //
            // These eight handles are the library's to make and this class's to destroy, so they go
            // on the same list the tool modules do.
            Ui = UiShaderLibrary.Load(device);

            made.AddRange([Ui.Vertex, Ui.Box, Ui.Text, Ui.Solid, Ui.Image, Ui.Blur, Ui.Colour, Ui.Mask]);
        }

        public LineShaders Lines { get; }
        public MeshShaders Meshes { get; }
        public UiShaders Ui { get; }

        ShaderHandle Load(IGraphicsDevice device, ShaderStage stage, string name) {
            var path = Path.Combine(AppContext.BaseDirectory, "ToolShaders", name);

            Assert.True(File.Exists(path), $"the capture needs {name} beside the test binary");

            var handle = device.CreateShader(stage, File.ReadAllBytes(path), name);

            made.Add(handle);

            return handle;
        }

        public void Dispose(IGraphicsDevice device) {
            foreach (var handle in made) {
                device.Destroy(handle);
            }
        }
    }
}
