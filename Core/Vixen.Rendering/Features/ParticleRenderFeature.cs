// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Graphics;
using Vixen.Shaders;
using Vixen.Vfx;

namespace Vixen.Rendering.Features;

/// <summary>Where one effect's quads ended up in this frame's vertex buffer.</summary>
/// <remarks>
///     Not a <see cref="MeshDraw" />, even though the fields rhyme. A mesh's buffers are its own and
///     outlive the frame; an effect's geometry is a run inside one buffer everybody shares, rebuilt
///     every frame — so what is recorded is where the run is rather than what it is in.
/// </remarks>
public struct ParticleDraw {
    /// <summary>
    ///     The first vertex of this effect's run in the shared buffer, or its first instance for a
    ///     mesh renderer.
    /// </summary>
    public int FirstVertex;

    /// <summary>How many particles are in it.</summary>
    public int ParticleCount;

    /// <summary>What the particles are drawn as, which decides which buffers the draw binds.</summary>
    public VfxRendererKind Kind;

    /// <summary>For a ribbon: how many indices its strips needed.</summary>
    /// <remarks>
    ///     Carried per effect because a ribbon's index count is not a function of its particle count —
    ///     it depends on how those particles divide into strips, and a strip of one contributes none.
    /// </remarks>
    public int IndexCount;

    /// <summary>For a ribbon: where its indices start in the frame's index buffer.</summary>
    public int FirstIndex;

    /// <summary>Whether there is anything to draw.</summary>
    public readonly bool IsDrawable => ParticleCount > 0 && (Kind != VfxRendererKind.Ribbon || IndexCount > 0);
}

/// <summary>
///     Draws the particles of <see cref="VfxSystem" />s: the second concrete renderable, and the
///     first one whose geometry is rebuilt every frame.
/// </summary>
/// <remarks>
///     <para>
///         A mesh arrives as buffers that already exist and the feature's job is to bind them. An
///         effect arrives as a few thousand positions that were different last frame, so the feature's
///         job is to <em>make</em> the geometry — expand each particle into a camera-facing quad,
///         append it to one buffer shared by every effect in the frame, and record the run. One
///         upload, one vertex buffer, one index buffer, and a draw per effect.
///     </para>
///     <para>
///         <strong>The expansion is on the CPU, and that is the temporary half of this.</strong> Doc
///         06 wants the simulation on the GPU with an indirect draw, at which point the vertex shader
///         expands from a particle buffer and this uploads a quarter of the bytes. What is here works
///         everywhere, needs no compute, and — because <c>Vixen.Vfx</c> owns the expansion and knows
///         nothing about graphics — is checked against numbers rather than against a screenshot.
///     </para>
///     <para>
///         <strong>One expansion, one view.</strong> A camera-facing quad faces <em>a</em> camera, so
///         an effect drawn into two views would need expanding twice. It is expanded once, against
///         <see cref="View" /> or the first view if that is not set, and every other view draws the
///         same quads — which is right for a reflection nobody inspects closely and wrong for a
///         shadow caster, so particles should not be in a shadow stage. The GPU path removes the
///         limitation rather than working around it, which is why it is not worked around here.
///     </para>
/// </remarks>
public sealed class ParticleRenderFeature : RootRenderFeature, IDisposable {
    readonly List<VfxSystem?> systems = [];
    readonly UploadBuffer<ParticleVertex> vertices = new("Particle vertices", BufferUsage.Vertex);
    readonly UploadBuffer<ParticleInstance> instances = new("Particle instances", BufferUsage.Vertex);
    readonly UploadBuffer<uint> strips = new("Ribbon indices", BufferUsage.Index);
    readonly VfxGeometryBuilder builder = new();

    ParticleVertex[] scratch = [];
    ParticleInstance[] transforms = [];
    uint[] segments = [];
    BufferHandle indices;
    int indexCapacity;
    int quads;
    bool disposed;

    /// <inheritdoc />
    public override string Name => "Particle";

    /// <summary>Where each effect's run is. One entry per object.</summary>
    public RenderDataKey<ParticleDraw> Draws { get; private set; }

    /// <summary>Each object's index into <see cref="Systems" />, or -1 for none.</summary>
    public RenderDataKey<int> SystemIndices { get; private set; }

    /// <summary>The mesh a <see cref="VfxRendererKind.Mesh" /> effect draws an instance of, per object.</summary>
    /// <remarks>
    ///     The same <see cref="MeshDraw" /> a mesh feature uses, and deliberately: a particle mesh is
    ///     an ordinary mesh drawn many times, so it arrives as buffers and a range like any other. What
    ///     the effect contributes is the per-instance data and the instance count.
    /// </remarks>
    public RenderDataKey<MeshDraw> Meshes { get; private set; }

    /// <summary>The effects this feature knows about.</summary>
    public IReadOnlyList<VfxSystem?> Systems => systems;

    /// <summary>The device the buffers live on. Set before the first frame that draws.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>Where pipelines come from. Set before the first frame that draws.</summary>
    public PipelineCache? Pipelines { get; set; }

    /// <summary>How a pipeline is described for a given effect, stage and output.</summary>
    public IPipelineDescriber? Describer { get; set; }

    /// <summary>The view the quads are built to face, or null for the first one.</summary>
    public RenderView? View { get; set; }

    /// <summary>
    ///     Which vertex layout <see cref="ParticleVertex" /> is, as an index into the describer's
    ///     table.
    /// </summary>
    /// <remarks>
    ///     A particle vertex is its own format — position, texture, colour — so it is its own layout
    ///     and its own pipelines. Sharing a mesh's layout index would put a particle's vertices
    ///     through a shader expecting normals and tangents.
    /// </remarks>
    public int VertexLayout { get; set; } = 1;

    /// <summary>How many particles the last frame expanded, across every effect.</summary>
    public int LastParticleCount { get; private set; }

    /// <summary>Attaches an effect to an object.</summary>
    /// <param name="id">The object.</param>
    /// <param name="system">The effect, or null to detach.</param>
    /// <exception cref="InvalidOperationException">The feature has not been added to a system yet.</exception>
    public void SetSystem(RenderObjectId id, VfxSystem? system) {
        if (System is null) {
            throw new InvalidOperationException("The feature has to be added to a RenderSystem before it can be given effects.");
        }

        var indicesData = System.Objects.Data.Data(SystemIndices);

        if (system is null) {
            indicesData[id.Index] = -1;

            return;
        }

        var slot = systems.IndexOf(system);

        if (slot < 0) {
            slot = systems.Count;
            systems.Add(system);
        }

        indicesData[id.Index] = slot;
    }

    /// <summary>Says which mesh an instanced effect draws.</summary>
    /// <param name="id">The object.</param>
    /// <param name="mesh">Its mesh. <see cref="MeshDraw.InstanceCount" /> is ignored — the particles say.</param>
    /// <exception cref="InvalidOperationException">The feature has not been added to a system yet.</exception>
    public void SetMesh(RenderObjectId id, MeshDraw mesh) {
        if (System is null) {
            throw new InvalidOperationException("The feature has to be added to a RenderSystem before it can be given meshes.");
        }

        System.Objects.Data.Data(Meshes)[id.Index] = mesh;
    }

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) {
        Draws = system.Objects.Data.Register<ParticleDraw>();
        SystemIndices = system.Objects.Data.Register<int>();
        Meshes = system.Objects.Data.Register<MeshDraw>();

        vertices.Device = Device;
        instances.Device = Device;
        strips.Device = Device;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Everything happens here rather than in <c>Extract</c>, because expanding a particle needs
    ///     to know where the camera is and extraction runs before there is a view to ask. It is also
    ///     the stage that runs over what survived culling, which for effects is the whole point: an
    ///     effect behind the viewer costs nothing rather than a few thousand quads nobody sees.
    /// </remarks>
    protected internal override void Prepare(RenderSystem system) {
        LastParticleCount = 0;
        quads = 0;

        var draws = system.Objects.Data.Data(Draws);
        var indicesData = system.Objects.Data.Data(SystemIndices);

        for (var index = 0; index < system.Objects.Count; index++) {
            draws[index] = default;
        }

        if (Camera(system) is not { } camera) {
            return;
        }

        vertices.Device ??= Device;
        instances.Device ??= Device;
        strips.Device ??= Device;

        vertices.Begin();
        instances.Begin();
        strips.Begin();

        for (var index = 0; index < system.Objects.Count; index++) {
            var id = new RenderObjectId(index);

            if (!system.Objects[id].IsAlive || system.Objects[id].FeatureIndex != Index) {
                continue;
            }

            var slot = indicesData[index];

            if ((uint)slot >= (uint)systems.Count || systems[slot] is not { Count: > 0 } effect) {
                continue;
            }

            var kind = effect.Graph.Renderer?.Kind ?? VfxRendererKind.Billboard;

            draws[index] = kind switch {
                VfxRendererKind.Mesh => Instanced(effect, camera),
                VfxRendererKind.Ribbon => Ribbon(effect, camera),
                _ => Quads(effect, camera)
            };

            LastParticleCount += draws[index].ParticleCount;

            // Counted apart from the rest, because it is what sizes the shared quad index buffer.
            // Sizing that from every particle would build a pattern for the mesh instances too — a
            // frame of five thousand instanced rocks would allocate thirty thousand indices no
            // billboard was ever going to use.
            if (draws[index].Kind == VfxRendererKind.Billboard) {
                quads += draws[index].ParticleCount;
            }
        }

        vertices.Upload();
        instances.Upload();
        strips.Upload();

        EnsureIndices();
    }

    /// <summary>One effect's particles as camera-facing quads in the shared vertex buffer.</summary>
    ParticleDraw Quads(VfxSystem effect, in VfxCamera camera) {
        var wanted = effect.Count * VfxGeometryBuilder.VerticesPerParticle;

        if (scratch.Length < wanted) {
            scratch = new ParticleVertex[Math.Max(wanted, scratch.Length * 2)];
        }

        var count = builder.Build(effect, camera, scratch.AsSpan(0, wanted));

        if (count == 0) {
            return default;
        }

        return new() {
            FirstVertex = vertices.Add(scratch.AsSpan(0, count * VfxGeometryBuilder.VerticesPerParticle)),
            ParticleCount = count,
            Kind = VfxRendererKind.Billboard
        };
    }

    /// <summary>One effect's particles as strips, with the indices this frame needed.</summary>
    /// <remarks>
    ///     The indices go into an upload buffer rather than the shared static one, because a strip's
    ///     triangles depend on where each ribbon ends and a ribbon ends wherever a particle died.
    ///     They are also written <em>relative to the effect's own first vertex</em> and reached
    ///     through the draw call's vertex offset, which is the same trick the quads use and the reason
    ///     one buffer can hold every effect in the frame.
    /// </remarks>
    ParticleDraw Ribbon(VfxSystem effect, in VfxCamera camera) {
        var wantedVertices = effect.Count * VfxGeometryBuilder.VerticesPerRibbonParticle;
        var wantedIndices = effect.Count * VfxGeometryBuilder.IndicesPerRibbonSegment;

        if (scratch.Length < wantedVertices) {
            scratch = new ParticleVertex[Math.Max(wantedVertices, scratch.Length * 2)];
        }

        if (segments.Length < wantedIndices) {
            segments = new uint[Math.Max(wantedIndices, segments.Length * 2)];
        }

        var count = builder.BuildRibbons(
            effect,
            camera,
            scratch.AsSpan(0, wantedVertices),
            segments.AsSpan(0, wantedIndices),
            out var indexCount
        );

        if (count == 0 || indexCount == 0) {
            return default;
        }

        return new() {
            FirstVertex = vertices.Add(scratch.AsSpan(0, count * VfxGeometryBuilder.VerticesPerRibbonParticle)),
            ParticleCount = count,
            Kind = VfxRendererKind.Ribbon,
            FirstIndex = strips.Add(segments.AsSpan(0, indexCount)),
            IndexCount = indexCount
        };
    }

    /// <summary>One effect's particles as instance transforms for a mesh.</summary>
    ParticleDraw Instanced(VfxSystem effect, in VfxCamera camera) {
        if (transforms.Length < effect.Count) {
            transforms = new ParticleInstance[Math.Max(effect.Count, transforms.Length * 2)];
        }

        var count = builder.BuildInstances(effect, camera, transforms.AsSpan(0, effect.Count));

        if (count == 0) {
            return default;
        }

        // FirstVertex is the first *instance* here, and it is what the draw call's firstInstance
        // takes — the same field doing the same job for a different kind of run.
        return new() {
            FirstVertex = instances.Add(transforms.AsSpan(0, count)),
            ParticleCount = count,
            Kind = VfxRendererKind.Mesh
        };
    }

    /// <inheritdoc />
    protected internal override void Draw(RenderSystem system, RenderDrawContext context, ReadOnlySpan<RenderNode> nodes) {
        // Not `!vertices.Buffer.IsValid` here. That buffer holds quads and strips, and a frame of
        // nothing but instanced meshes never puts a byte in it — so testing it up front was a whole
        // renderer that silently drew nothing.
        if (Pipelines is null || Describer is null || context.Stage is null) {
            return;
        }

        var stage = context.Stage;
        var output = context.Output;
        var draws = system.Objects.Data.Data(Draws);
        var materials = SubFeatures.OfType<MaterialRenderFeature>().FirstOrDefault();

        var meshes = system.Objects.Data.Data(Meshes);
        var boundPipeline = default(PipelineHandle);
        var boundDescriptors = default(DescriptorSetHandle);
        var boundView = false;

        // Which shape's buffers are currently bound, rather than a flag. Three kinds of draw need
        // three sets of bindings, and the saving is still real: a frame of nothing but billboards
        // binds once, exactly as before, and only a frame that mixes kinds pays to switch.
        var bound = default(VfxRendererKind?);
        var boundMesh = default(BufferHandle);

        foreach (var node in nodes) {
            var draw = draws[node.Object.Index];

            if (!draw.IsDrawable) {
                continue;
            }

            if (materials?.EffectOf(system, node.Object, stage) is not { } effect) {
                continue;
            }

            var mesh = meshes[node.Object.Index];

            // Each kind needs its own buffers to exist, and a frame may hold one kind and not
            // another. A mesh effect with no mesh attached is the ordinary case rather than an error:
            // the mesh arrives by a separate call, so a frame between the two has to be quiet.
            var ready = draw.Kind switch {
                VfxRendererKind.Mesh => mesh.IsDrawable && instances.Buffer.IsValid,
                VfxRendererKind.Ribbon => vertices.Buffer.IsValid && strips.Buffer.IsValid,
                _ => vertices.Buffer.IsValid && indices.IsValid
            };

            if (!ready) {
                continue;
            }

            // A mesh particle goes through the mesh's own vertex layout, because its vertices are the
            // mesh's and only the instance stream is this feature's. Sharing the billboard layout
            // would put a mesh's normals through a shader expecting a texture coordinate.
            var layout = draw.Kind == VfxRendererKind.Mesh ? mesh.VertexLayout : VertexLayout;
            var key = new PipelineKey(effect, stage.Index, layout, output);

            if (!Pipelines.TryGet(key, out var pipeline)) {
                pipeline = Pipelines.GetOrCreate(key, () => Describer.Describe(effect, stage, output, layout));
            }

            if (pipeline != boundPipeline) {
                context.CommandList.BindPipeline(pipeline);
                boundPipeline = pipeline;
            }

            if (!boundView && context.ViewConstants is { } view && context.View is { } from) {
                boundView = view.Bind(context.CommandList, from);
            }

            if (materials.DescriptorsOf(system, node.Object) is { IsValid: true } descriptors && descriptors != boundDescriptors) {
                context.CommandList.BindDescriptorSet(DescriptorSetSlot.PerMaterial, descriptors);
                boundDescriptors = descriptors;
            }

            switch (draw.Kind) {
                case VfxRendererKind.Mesh: {
                    // The mesh's buffers change per effect and the instance stream does not, so the
                    // mesh handle is what the rebinding is keyed on.
                    if (bound != VfxRendererKind.Mesh || boundMesh != mesh.VertexBuffer) {
                        context.CommandList.BindVertexBuffer(0, mesh.VertexBuffer, 0);
                        context.CommandList.BindVertexBuffer(1, instances.Buffer, instances.Offset);
                        context.CommandList.BindIndexBuffer(mesh.IndexBuffer, mesh.IndexFormat);
                        boundMesh = mesh.VertexBuffer;
                        bound = VfxRendererKind.Mesh;
                    }

                    // One draw for the whole effect: the mesh once, instanced by however many
                    // particles are alive, reached through firstInstance.
                    context.CommandList.DrawIndexed(
                        mesh.Count,
                        draw.ParticleCount,
                        mesh.FirstIndex,
                        mesh.VertexOffset,
                        draw.FirstVertex
                    );

                    break;
                }

                case VfxRendererKind.Ribbon: {
                    if (bound != VfxRendererKind.Ribbon) {
                        context.CommandList.BindVertexBuffer(0, vertices.Buffer, vertices.Offset);
                        context.CommandList.BindIndexBuffer(strips.Buffer, IndexFormat.UInt32);
                        bound = VfxRendererKind.Ribbon;
                    }

                    // The indices are the effect's own, written from zero, so the run is reached the
                    // same way a quad run is — through the vertex offset — and the index range picks
                    // out which effect's triangles these are.
                    context.CommandList.DrawIndexed(draw.IndexCount, 1, draw.FirstIndex, draw.FirstVertex, 0);

                    break;
                }

                default: {
                    // Once for every billboard effect in the frame. Every one of them is a run of the
                    // same buffer at the same offset, and the run is reached through the draw's first
                    // vertex instead — which is what makes a hundred effects a hundred draws and not
                    // a hundred rebindings.
                    if (bound != VfxRendererKind.Billboard) {
                        context.CommandList.BindVertexBuffer(0, vertices.Buffer, vertices.Offset);
                        context.CommandList.BindIndexBuffer(indices, IndexFormat.UInt32);
                        bound = VfxRendererKind.Billboard;
                    }

                    context.CommandList.DrawIndexed(
                        draw.ParticleCount * VfxGeometryBuilder.IndicesPerParticle,
                        1,
                        0,
                        draw.FirstVertex,
                        0
                    );

                    break;
                }
            }
        }
    }

    /// <summary>Frees the buffers.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        vertices.Dispose();
        instances.Dispose();
        strips.Dispose();

        if (indices.IsValid) {
            Device?.Destroy(indices);
            indices = default;
        }
    }

    /// <summary>The camera the quads are built to face.</summary>
    VfxCamera? Camera(RenderSystem system) {
        var view = View ?? (system.Views.Count > 0 ? system.Views[0] : null);

        if (view?.Camera is not { } camera) {
            return null;
        }

        return VfxCamera.Looking(camera.Position, camera.Forward, camera.Up);
    }

    /// <summary>Builds the quad index pattern, once, for as many particles as the frame needed.</summary>
    /// <remarks>
    ///     The pattern never depends on anything but the count, so it is device memory written when
    ///     the high-water mark rises and never again — unlike the vertices, which are rewritten every
    ///     frame because they are different every frame.
    /// </remarks>
    void EnsureIndices() {
        if (Device is null || quads <= indexCapacity) {
            return;
        }

        if (indices.IsValid) {
            Device.Destroy(indices);
        }

        indexCapacity = Math.Max(quads, Math.Max(indexCapacity * 2, 256));

        var pattern = new uint[indexCapacity * VfxGeometryBuilder.IndicesPerParticle];
        VfxGeometryBuilder.WriteQuadIndices(pattern, indexCapacity);

        indices = Device.CreateBuffer(
            new((long)pattern.Length * sizeof(uint), BufferUsage.Index, MemoryAccess.HostUpload, "Particle indices")
        );

        Device.Write(indices, 0, MemoryMarshal.AsBytes(pattern.AsSpan()));
    }
}
