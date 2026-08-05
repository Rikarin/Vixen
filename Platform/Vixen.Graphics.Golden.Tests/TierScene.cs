// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Engine.Renderer;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     One scene, staged through the host path, rendered by whatever a <c>!StandardFrame</c> expands
///     into.
/// </summary>
/// <remarks>
///     <para>
///         <b>Deliberately not a hand-built compositor.</b> Every other fixture in this suite assembles
///         its own nodes, which tests the nodes and leaves the expansion — the thing that decides what a
///         tier <em>does</em> — asserted only against its own structure. What runs here is
///         <see cref="StandardFrameAsset" /> through <see cref="PostEffectFactory" />, built by
///         <see cref="CompositorBuilder" />, drawn by <see cref="WorldRenderer" />: the same four calls
///         a game makes, so a knob that stops reaching a pass fails here and nowhere else.
///     </para>
///     <para>
///         <b>The scene is asymmetric on every axis, and that is the fixture's first decision.</b> A
///         mirror-symmetric scene is what let the screen-space y-fold survive its own tests for months
///         — the marches read a mirrored screen and no assertion could tell. So nothing here is
///         centred: the camera looks at a point that is not the origin, the caster is off to one side
///         and behind, the shadow it throws crosses the frame diagonally, and the three surfaces sit at
///         three different heights. Flipping the picture in either axis changes it.
///     </para>
/// </remarks>
sealed class TierScene : IDisposable {
    /// <summary>What one vertex of everything here is.</summary>
    /// <remarks>
    ///     <see cref="SurfaceVertex" /> itself, because the schema the renderer describes pipelines
    ///     against is that struct's — a local copy with the same fields would agree with itself and
    ///     not with <c>ForwardPlus</c>.
    /// </remarks>
    readonly List<SurfaceVertex> vertices = [];
    readonly List<ushort> indices = [];

    readonly Fixture fixture;
    readonly List<Action> cleanup = [];

    (TextureHandle Texture, TextureViewHandle View, TextureDescription Description) owned;

    TierScene(Fixture fixture, WorldRenderer renderer) {
        this.fixture = fixture;
        Renderer = renderer;
    }

    /// <summary>The renderer everything is staged into.</summary>
    public WorldRenderer Renderer { get; }

    /// <summary>The camera the frame draws from, and the view a document's <c>view:</c> names.</summary>
    public RenderView View { get; private set; } = null!;

    /// <summary>The stages the built document declared, by name.</summary>
    public IReadOnlyDictionary<string, RenderStage> Stages => Renderer.Host.Builder.Stages;

    /// <summary>Where the picture lands.</summary>
    public TextureHandle Output { get; private set; }

    /// <summary>
    ///     The sun, low and off-axis so its shadow crosses the frame rather than falling behind
    ///     what casts it.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not down any axis. A sun straight overhead puts the caster's shadow directly under it,
    ///     where the caster already hides it — a shadow fixture that cannot see its own shadow. The
    ///     three components are also all different, so a transposed or swizzled light direction moves
    ///     the shadow instead of leaving it where it was.
    /// </remarks>
    public static Vector3 SunDirection => Vector3.Normalize(new(0.62f, -0.55f, 0.42f));

    /// <summary>The camera, placed so the shadow lands across the near half of the floor.</summary>
    /// <remarks>
    ///     Square, because the target is: an aspect ratio that disagreed with the target would still
    ///     produce a plausible picture, and the frame's screen-space passes tile the screen.
    /// </remarks>
    public static RenderCamera Camera => RenderCamera.Default with {
        Position = new(-3.1f, 2.35f, 5.6f),
        Forward = Vector3.Normalize(new(0.46f, -0.29f, -1f)),
        AspectRatio = 1f,
        FieldOfView = MathF.PI / 3.2f,
        NearPlane = 0.2f,
        FarPlane = 120f
    };

    /// <summary>Stages the scene on a device and builds the frame a document describes.</summary>
    /// <param name="fixture">The device.</param>
    /// <param name="effects">Where variants are compiled.</param>
    /// <param name="document">The document, whose <c>!StandardFrame</c> this expands.</param>
    /// <param name="tier">The platform's pick, which the frame node inherits.</param>
    /// <remarks>
    ///     ⚠ The order is the host's and every step of it is load bearing. The view is registered
    ///     before the build because the builder binds a document's <c>view:</c> by name as it creates
    ///     each node, and a view added afterwards is one nothing refers to. The factory is registered
    ///     before it too, because a node kind nothing has bound is not a warning — it is a
    ///     <see cref="CompositorBindingException" /> out of the middle of the build. And the tier is
    ///     set before both, because the expansion runs inside the build and reads it.
    /// </remarks>
    public static TierScene Open(
        Fixture fixture,
        EffectSystem effects,
        GraphicsCompositorAsset document,
        QualityTier tier
    ) {
        var renderer = new WorldRenderer(fixture.Device, effects, 1 << 14, 1 << 14);
        var scene = new TierScene(fixture, renderer);

        scene.Build();

        renderer.Host.Builder.Views["Camera"] = scene.View;
        renderer.Host.Builder.Factories.Add(new PostEffectFactory());
        renderer.Host.Builder.Quality = tier;
        renderer.Host.Load(document);
        renderer.Host.FrameSize = new(Fixture.Side, Fixture.Side);

        // The frame's last target has to belong to somebody outside the graph, or the graph is right
        // to cull the pass that writes it — and the import wins over the resource the expansion
        // declares by the same name, which is the rule that lets one document write a window in a
        // game and a scratch texture here.
        renderer.Host.Import(
            document.Game is StandardFrameAsset { Output: { Length: > 0 } named } ? named : "SceneColour",
            new(scene.owned.Texture, scene.owned.View, scene.owned.Description, ResourceState.Undefined, ResourceState.CopySource)
        );

        return scene;
    }

    void Build() {
        var device = fixture.Device;

        // Set 0's environment, and it is not decoration: `ForwardPlus` declares `environment`,
        // `probes` and their samplers whatever the permutations say, a set writes whole or not at
        // all, and a frame with no cube therefore binds no set 0 and draws nothing. Eight texels a
        // face at four levels and eight samples is a bake of milliseconds, which is what a fixture
        // can afford; the gradient is the same one the sky node would produce and is what makes the
        // smooth surface show a horizon rather than a constant.
        var sky = new CubeImage(8);

        for (var face = 0; face < 6; face++) {
            var image = (CubeFace)face;

            for (var y = 0; y < sky.Size; y++) {
                for (var x = 0; x < sky.Size; x++) {
                    var direction = sky.DirectionOf(image, x, y);
                    var height = Math.Clamp((direction.Y * 0.5f) + 0.5f, 0f, 1f);

                    // Warm ground, cool zenith — and unequal channels, so a swizzle in the cube's
                    // upload is a colour change rather than a shade change.
                    sky.At(image, x, y) = Vector3.Lerp(new(0.32f, 0.24f, 0.16f), new(0.30f, 0.44f, 0.72f), height);
                }
            }
        }

        var environment = EnvironmentTexture.Bake(device, sky, mipCount: 4, samples: 8);

        cleanup.Add(environment.Dispose);
        Renderer.Environment = environment;

        var light = new EnvironmentLight { MipCount = environment.MipCount, Intensity = 1f };

        environment.Apply(light);
        Renderer.SceneEnvironment.Environment = light;

        // The sun, as a light the lighting feature owns rather than a constant: the shadow node fits
        // its cascades along `CompositorBuilder.Sun`, which is this same feature, so a fixture that
        // set a direction on set 0 by hand would light the scene down one vector and shadow it down
        // another.
        Renderer.Lighting.Lights.Add(
            RenderLight.Directional(SunDirection, new(1f, 0.94f, 0.82f), 3.6f)
        );

        // One lamp, off to the side and low, so the punctual atlas has a tile to fill and the smooth
        // slab has a highlight that is not the sun's.
        Renderer.Lighting.Lights.Add(
            RenderLight.Point(new(2.9f, 1.15f, 1.6f), 7f, new(1f, 0.45f, 0.2f), 26f)
        );

        // ⚠ Rgba8UNormSrgb where the expansion declares Bgra8UNormSrgb, because the readback is
        // this fixture's and `Bitmap` is RGBA — a BGRA target would come back with red and blue
        // swapped and every reference would record the swap. The import decides the format, so this
        // is a statement about the harness rather than about the frame.
        owned = fixture.Owned(
            "TierOutput",
            TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.Storage | TextureUsage.CopySource,
            PixelFormat.Rgba8UNormSrgb
        );

        Output = owned.Texture;
        View = new("Camera") { Camera = Camera };
    }

    /// <summary>Adds one box to the scene.</summary>
    public void Box(Vector3 centre, Vector3 half, Material material, RenderStageMask stages) {
        var first = vertices.Count;
        var start = indices.Count;

        foreach (var axis in (int[])[0, 1, 2]) {
            foreach (var sign in (float[])[-1f, 1f]) {
                var normal = axis switch {
                    0 => new Vector3(sign, 0f, 0f),
                    1 => new Vector3(0f, sign, 0f),
                    _ => new Vector3(0f, 0f, sign)
                };

                var u = axis switch {
                    0 => new Vector3(0f, 0f, -sign),
                    1 => new Vector3(sign, 0f, 0f),
                    _ => new Vector3(sign, 0f, 0f)
                };

                var v = Vector3.Cross(normal, u);
                var origin = centre + (normal * Vector3.Dot(half, Abs(normal)));
                var baseIndex = vertices.Count - first;

                foreach (var corner in (Vector2[])[new(-1f, -1f), new(1f, -1f), new(-1f, 1f), new(1f, 1f)]) {
                    var offset = (u * corner.X * Vector3.Dot(half, Abs(u)))
                        + (v * corner.Y * Vector3.Dot(half, Abs(v)));

                    vertices.Add(
                        new() {
                            Position = origin + offset,
                            Normal = normal,
                            Tangent = new(u.X, u.Y, u.Z, 1f),
                            TexCoord = new((corner.X * 0.5f) + 0.5f, (corner.Y * 0.5f) + 0.5f)
                        }
                    );
                }

                indices.AddRange(
                    [
                        (ushort)baseIndex, (ushort)(baseIndex + 1), (ushort)(baseIndex + 2),
                        (ushort)(baseIndex + 2), (ushort)(baseIndex + 1), (ushort)(baseIndex + 3)
                    ]
                );
            }
        }

        Pending.Add(new(first, start, indices.Count - start, centre, Vector3.Distance(Vector3.Zero, half), material, stages));
    }

    /// <summary>What has been described and not yet handed to the render system.</summary>
    public List<PendingDraw> Pending { get; } = [];

    /// <summary>One described object, waiting for the buffers to exist.</summary>
    public readonly record struct PendingDraw(
        int FirstVertex,
        int FirstIndex,
        int IndexCount,
        Vector3 Centre,
        float Radius,
        Material Material,
        RenderStageMask Stages
    );

    /// <summary>Uploads the geometry and adds every described object to the render system.</summary>
    public void Commit(RenderStageMask viewStages) {
        var system = Renderer.Host.System;

        var vertexBuffer = fixture.Buffer<SurfaceVertex>(vertices.ToArray(), BufferUsage.Vertex);
        var indexBuffer = fixture.Buffer<ushort>(indices.ToArray(), BufferUsage.Index);

        View.Stages = viewStages;
        View.Frustum = new(View.ViewProjection);
        system.SetViews([View]);

        foreach (var draw in Pending) {
            var id = system.Objects.Add(
                new() {
                    Bounds = new(draw.Centre, draw.Radius),
                    Stages = draw.Stages,
                    FeatureIndex = Renderer.Meshes.Index
                }
            );

            system.Objects.Data.Data(Renderer.Meshes.Draws)[id.Index] = new() {
                VertexBuffer = vertexBuffer,
                IndexBuffer = indexBuffer,
                IndexFormat = IndexFormat.UInt16,
                Count = draw.IndexCount,
                FirstIndex = draw.FirstIndex,
                VertexOffset = draw.FirstVertex,
                InstanceCount = 1
            };

            // ⚠ Identity and not skippable: the default is the *zero* matrix, which collapses every
            // vertex onto the origin — a frame that clears and draws nothing, which is the same
            // picture as a frame that never ran. The geometry carries its own placement, so identity
            // is what the world matrix should be here.
            system.Objects.Data.Data(Renderer.Transforms.World)[id.Index] = Matrix4x4.Identity;

            Renderer.Materials.Assign(system, id, draw.Material);
        }
    }

    /// <summary>Records <paramref name="frames" /> frames and reads the last one back.</summary>
    /// <param name="frames">
    ///     How many. More than one for a frame with history in it — see the remarks on
    ///     <see cref="StandardFrameTierImageTests" /> for what settles and what is pinned.
    /// </param>
    /// <remarks>
    ///     ⚠ The validation check is before the submit as well as after it, for
    ///     <see cref="Fixture.Render" />'s reason: everything the layer says about <em>recording</em>
    ///     it has already said by then, and submitting anyway is a GPU fault that takes every later
    ///     test in the collection with it.
    /// </remarks>
    public Bitmap Frames(int frames) {
        var device = fixture.Device;
        const int Bytes = Fixture.Side * Fixture.Side * 4;

        var readback = device.CreateBuffer(
            new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "tier readback")
        );

        VulkanDiagnostics.Reset();

        for (var frame = 0; frame < frames; frame++) {
            device.BeginFrame();
            Renderer.MaterialDescriptors.BeginFrame();

            using (var commands = device.BeginCommandList(QueueKind.Graphics, "tier")) {
                Renderer.Draw(commands);

                if (frame == frames - 1) {
                    commands.CopyTextureToBuffer(new(Output), new(Fixture.Side, Fixture.Side, 1), readback, 0);
                }

                commands.Finish();
                Fail(device, readback);
                device.GraphicsQueue.Submit([commands]);
            }

            device.EndFrame();
            device.WaitIdle();
        }

        var pixels = new byte[Bytes];

        device.Read(readback, 0, pixels);
        device.Destroy(readback);

        Fail(device, default);

        return new(Fixture.Side, Fixture.Side, pixels);
    }

    static void Fail(VulkanDevice device, BufferHandle readback) {
        if (VulkanDiagnostics.ErrorCount == 0) {
            return;
        }

        if (readback.IsValid) {
            device.Destroy(readback);
        }

        throw new InvalidOperationException(
            "The tier frame produced validation errors, so its picture is meaningless: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );
    }

    static Vector3 Abs(Vector3 value) => new(MathF.Abs(value.X), MathF.Abs(value.Y), MathF.Abs(value.Z));

    /// <inheritdoc />
    public void Dispose() {
        foreach (var action in cleanup) {
            action();
        }

        Renderer.Dispose();
    }
}
