// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Engine.Renderer;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
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

    EnvironmentTexture? environment;
    TextureHandle opaqueTexture;
    TextureViewHandle opaqueView;
    SamplerHandle opaqueSampler;
    BufferHandle bindPose;
    BufferHandle clusters;
    BufferHandle opacityStaging;

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
        Position = new(-3.4f, 2.9f, 4.6f),
        Forward = Vector3.Normalize(new(0.78f, -0.49f, -1f)),
        AspectRatio = 1f,
        FieldOfView = MathF.PI / 3f,
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

        scene.Supply();

        return scene;
    }

    /// <summary>
    ///     Fills what the frame declares and nothing in it produces.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Sample 13's <c>ApplySky</c> and <c>ApplyCaster</c>, and the same three reasons.</b> A
    ///         permutation folds <em>code</em> and not bindings, so every set in the frame is declared
    ///         whole whatever is switched off — and a set written short of one binding is a set that is
    ///         never bound at all. Three of them have no producer anywhere in a Standard Frame:
    ///     </para>
    ///     <list type="bullet">
    ///         <item><description>
    ///             the sky node's <b>environment cube</b>, which is baked before the graph exists and
    ///             outlives every frame, so no document can name it;
    ///         </description></item>
    ///         <item><description>
    ///             the caster stage's <b>opacity map, its sampler and a bone palette</b>, which no
    ///             material in any project has an opinion about — white, because white is "this texel
    ///             is solid" and undefined is a shadow with holes in it on one driver;
    ///         </description></item>
    ///         <item><description>
    ///             the <b>cluster list</b>, which <c>ForwardPlus</c> declares whether or not the
    ///             clustered permutation is on.
    ///         </description></item>
    ///     </list>
    ///     <para>
    ///         ⚠ After every <c>Load</c> and not once: the nodes are made by the builder, so a rebuilt
    ///         document is a different sky node — and one that never got its cube draws black, which is
    ///         exactly what a missing background looks like.
    ///     </para>
    /// </remarks>
    void Supply() {
        var device = fixture.Device;

        foreach (var node in Renderer.Host.Builder.Nodes.Values) {
            if (node is SkyRenderer sky) {
                sky.Environment = environment!.View;
                sky.EnvironmentSampler = environment.Sampler;
                sky.MipCount = environment.MipCount;
            }

            // ⚠ The one thing this fixture pins that a game would not, and the whole of its
            // determinism argument for exposure. The meter eases towards its target at
            // `1 - exp(-dt·rate)` per frame, which at the node's own 3 e-folds per second and 1/60 of
            // a second is 4.9% of the way — so a golden rendered after N frames is a picture of N,
            // and moving the frame count by one moves every pixel. A delta of ten seconds makes that
            // fraction 1 to thirteen decimal places: the meter reaches its target on the first frame
            // and stays there, so what is committed is the exposure the scene resolves to rather than
            // the exposure it was passing through. The rate itself is untouched, because the rate is
            // what a regression in the adaptation would move.
            if (node is AutoExposureRenderer meter) {
                meter.DeltaTime = 10f;
            }
        }

        Renderer.SceneBlock.Parameters.Set(ForwardPlusKeys.Clusters, clusters);

        if (!Renderer.Host.Builder.Stages.TryGetValue("Shadow", out var caster)) {
            return;
        }

        // Named rather than taken from a generated `ShadowCasterKeys`, because there is no such class:
        // the key generator reads a shader's committed `.reflect.json` and `ShadowCaster` has none.
        caster.Parameters.Set(ParameterKeys.New<TextureViewHandle>("ShadowCaster.opacityMap"), opaqueView);
        caster.Parameters.Set(ParameterKeys.New<SamplerHandle>("ShadowCaster.opacitySampler"), opaqueSampler);
        caster.Parameters.Set(ParameterKeys.New<BufferHandle>("ShadowCaster.bones"), bindPose);
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

                    // ⚠ Photometric, in cd/m², and not a 0–1 colour. The frame meters itself and the
                    // tonemap's curve is calibrated in real units, so a sky of 0.4 is a scene twelve
                    // stops under anything the exposure machinery was built for — which comes out as
                    // a frame the meter drags up until it is white. Warm ground and cool zenith, with
                    // unequal channels, so a swizzle in the cube's upload is a colour change rather
                    // than a shade change.
                    sky.At(image, x, y) = Vector3.Lerp(
                        new(760f, 560f, 380f),
                        new(1_100f, 1_600f, 2_600f),
                        height
                    );
                }
            }
        }

        environment = EnvironmentTexture.Bake(device, sky, mipCount: 4, samples: 8);

        cleanup.Add(environment.Dispose);
        Renderer.Environment = environment;

        // The ambient term the surfaces not facing the sun are lit by, projected off the same cube
        // the background is drawn from — two opinions about one sky is the drift `SkyRenderer`'s
        // remarks describe.
        var light = new EnvironmentLight {
            MipCount = environment.MipCount,
            Intensity = 1f,
            Irradiance = SphericalHarmonics.Project(sky)
        };

        environment.Apply(light);
        Renderer.SceneEnvironment.Environment = light;

        StandIns(device);

        // The sun, as a light the lighting feature owns rather than a constant: the shadow node fits
        // its cascades along `CompositorBuilder.Sun`, which is this same feature, so a fixture that
        // set a direction on set 0 by hand would light the scene down one vector and shadow it down
        // another.
        // ⚠ Lux, and a low sun's rather than noon's. Twelve thousand is late afternoon: bright enough
        // that the shadow is the darkest thing in frame and dim enough that the lamp below is visible
        // at all, which at noon's ninety thousand it would not be.
        Renderer.Lighting.Lights.Add(
            RenderLight.Directional(SunDirection, new(1f, 0.94f, 0.82f), 12_000f)
        );

        // One lamp, off to the side and low, so the punctual atlas has a tile to fill and the smooth
        // slab has a highlight that is not the sun's.
        Renderer.Lighting.Lights.Add(
            RenderLight.Point(new(2.9f, 1.15f, 1.6f), 7f, new(1f, 0.45f, 0.2f), 9_000f)
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

    /// <summary>The four resources the frame declares and no pass in it writes.</summary>
    /// <remarks>
    ///     See <see cref="Supply" /> for why each exists. The opacity texture's four bytes are staged
    ///     rather than written, because a texture is device memory and only a copy reaches it — and
    ///     the copy cannot be recorded inside a render pass, which is what
    ///     <see cref="Upload" /> is for.
    /// </remarks>
    void StandIns(VulkanDevice device) {
        clusters = device.CreateBuffer(
            new(ClusterGrid.BufferSize, BufferUsage.Storage, MemoryAccess.HostUpload, "Clusters.StandIn")
        );

        device.Write(clusters, 0, new byte[ClusterGrid.BufferSize]);

        var opacity = fixture.Owned(
            "Opacity.Opaque",
            TextureUsage.Sampled | TextureUsage.CopyDestination,
            PixelFormat.Rgba8UNorm,
            1,
            1
        );

        opaqueView = opacity.View;
        opaqueSampler = fixture.Sampler(SamplerDescription.LinearClamp with { Name = "Opacity.Opaque" });
        opaqueTexture = opacity.Texture;

        opacityStaging = device.CreateBuffer(
            new(4, BufferUsage.CopySource, MemoryAccess.HostUpload, "Opacity.Staging")
        );

        device.Write(opacityStaging, 0, [byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue]);

        bindPose = device.CreateBuffer(
            new(64, BufferUsage.Storage, MemoryAccess.HostUpload, "Bones.BindPose")
        );

        var identity = Matrix4x4.Identity;

        device.Write(bindPose, 0, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref identity, 1)));

        cleanup.Add(() => {
            device.Destroy(clusters);
            device.Destroy(opacityStaging);
            device.Destroy(bindPose);
        });
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

            using (var commands = device.BeginCommandList(QueueKind.Graphics, "tier")) {
                if (frame == 0) {
                    // ⚠ Before the passes and outside any of them: a copy into a texture cannot be
                    // recorded inside a render pass, and everything the graph executes is inside one.
                    commands.Barrier(
                        new([], [new(opaqueTexture, ResourceState.Undefined, ResourceState.CopyDestination)])
                    );

                    commands.CopyBufferToTexture(opacityStaging, 0, new(opaqueTexture), new(1, 1, 1));

                    commands.Barrier(
                        new([], [new(opaqueTexture, ResourceState.CopyDestination, ResourceState.ShaderRead)])
                    );
                }

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
