// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.Rendering.RayTracing;
using Vixen.Rendering.ScreenProbes;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>Doc 19 § L6 on a device: the two-level build, and the query against its referee.</summary>
/// <remarks>
///     <para>
///         The whole path in one submission — vertex and index buffers by GPU address, the
///         bottom-level build, one identity instance, the top-level build, and a probe-trace
///         dispatch whose <c>distanceField</c> slot is <c>RayQueryField</c> — with
///         <see cref="QueriedField" /> over the same triangles as the referee, which is the same
///         two-sided arrangement every dispatch comparison here uses.
///     </para>
///     <para>
///         "Nothing above it changes" is asserted by construction: the kernel dispatched is
///         <c>ScreenProbeTrace</c>, unmodified, and only the composition names the tracer.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class AccelerationStructureDeviceTests {
    [Fact]
    public void TheQueryAnswersTheRefereesRays() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        if (!device.Features.HasRayTracing) {
            // Not a failure. MoltenVK exposes neither VK_KHR_acceleration_structure nor
            // VK_KHR_ray_query, so a Mac is a legitimate "no" — the distance-field tracer is the
            // configuration that runs here, and the VulkanFeatures tests hold the detection.
            return;
        }

        // Four large, well-separated triangles around a probe at the origin: broad cones of hit
        // and of sky, so almost every octahedral texel is decisively one or the other. The edges
        // are where the CPU's Möller–Trumbore and the hardware's watertight traversal may
        // honestly disagree, which is what the mismatch allowance below is for.
        Span<Vector3> vertices = [
            new(-6f, 3f, -6f), new(6f, 3f, -6f), new(0f, 3f, 8f),
            new(4f, -6f, -6f), new(4f, 6f, -6f), new(4f, 0f, 8f),
            new(-6f, -6f, -4f), new(6f, -6f, -4f), new(0f, 8f, -4f),
            new(-6f, -3f, 6f), new(6f, -3f, 6f), new(0f, -3f, -8f)
        ];

        Span<int> indices = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

        var referee = new QueriedField(new(vertices, indices));

        // The geometry, uploaded host-visible and addressed by the builds.
        var positions = new float[vertices.Length * 3];

        for (var i = 0; i < vertices.Length; i++) {
            positions[i * 3] = vertices[i].X;
            positions[(i * 3) + 1] = vertices[i].Y;
            positions[(i * 3) + 2] = vertices[i].Z;
        }

        const BufferUsage Input =
            BufferUsage.AccelerationStructureInput | BufferUsage.ShaderDeviceAddress;

        var vertexBuffer = device.CreateBuffer(
            new(positions.Length * 4L, Input, MemoryAccess.HostUpload, "as-vertices")
        );

        var indexBuffer = device.CreateBuffer(
            new(indices.Length * 4L, Input, MemoryAccess.HostUpload, "as-indices")
        );

        owned.Owns(() => device.Destroy(vertexBuffer));
        owned.Owns(() => device.Destroy(indexBuffer));
        device.Write(vertexBuffer, 0, MemoryMarshal.AsBytes(positions.AsSpan()));
        device.Write(indexBuffer, 0, MemoryMarshal.AsBytes(indices));

        var bottomInput = new AccelerationStructureBuildInput(
            AccelerationStructureKind.BottomLevel,
            Triangles: new(vertexBuffer, 0, vertices.Length, 12, indexBuffer, 0, indices.Length)
        );

        var bottomSizes = device.GetAccelerationStructureSizes(bottomInput);
        var bottom = device.CreateAccelerationStructure(new(AccelerationStructureKind.BottomLevel, bottomSizes.Structure, "as-bottom"));

        owned.Owns(() => device.Destroy(bottom));

        // One identity instance, referring to the bottom level by the address the device names.
        var instance = AccelerationStructureInstance.Identity(device.GetAccelerationStructureAddress(bottom));
        var instances = device.CreateBuffer(new(64, Input, MemoryAccess.HostUpload, "as-instances"));

        owned.Owns(() => device.Destroy(instances));
        device.Write(instances, 0, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref instance, 1)));

        var topInput = new AccelerationStructureBuildInput(
            AccelerationStructureKind.TopLevel,
            Instances: new(instances, 0, 1)
        );

        var topSizes = device.GetAccelerationStructureSizes(topInput);
        var top = device.CreateAccelerationStructure(new(AccelerationStructureKind.TopLevel, topSizes.Structure, "as-top"));

        owned.Owns(() => device.Destroy(top));

        var scratch = device.CreateBuffer(
            new(
                Math.Max(bottomSizes.Scratch, topSizes.Scratch),
                BufferUsage.Storage | BufferUsage.ShaderDeviceAddress,
                MemoryAccess.DeviceLocal,
                "as-scratch"
            )
        );

        owned.Owns(() => device.Destroy(scratch));

        // The probe whose sixty-four rays are the comparison: standing at the origin, biased up.
        var traced = new ScreenProbeAtlas(new(new(16, 16)));

        traced.SetSurface(new(0, 0), Vector3.Zero, new(0f, 1f, 0f));

        using var allocator = new DescriptorAllocator(device);
        using var texture = new ScreenProbeTexture(traced) { AtlasIsWritten = true };

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(
            new Compiling(loader, _ => RavenEffects.Only(["Core", "DistanceFields", "IrradianceFields", "ScreenProbes", "SurfaceCache"]))
        );

        const float Bias = 0.01f;

        using var trace = new ScreenProbeTraceFill(device) {
            Effects = effects,
            Pipelines = new ComputePipelineCache(device),
            Descriptors = allocator,
            Source = MaterialCompiler.RayQueryFieldShader,
            SkyColour = new(0.6f, 0.45f, 0.3f),
            SkyGradient = new(0.2f),
            MaxDistance = 8f,
            SurfaceBias = Bias
        };

        trace.Parameters.Set(
            ParameterKeys.New<AccelerationStructureHandle>(
                $"{ScreenProbeTraceFill.ShaderName}.{MaterialCompiler.RayQueryFieldShader}.sceneStructure"
            ),
            top
        );

        var texels = new Vector4[traced.Layout.AtlasSize.X * traced.Layout.AtlasSize.Y];

        allocator.BeginFrame();
        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "ray-query")) {
            // Bottom before top on one queue — the build's own trailing barrier is the ordering,
            // exactly as ICommandList promises.
            commands.BuildAccelerationStructure(bottom, bottomInput, scratch);
            commands.BuildAccelerationStructure(top, topInput, scratch);

            texture.Upload(device, commands);

            Assert.Equal(1, trace.Record(commands, texture));
            Assert.True(texture.RecordReadback(commands));

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        Assert.Null(trace.Skipped);
        Assert.Empty(effects.Misses);
        Assert.True(texture.TryRead(texels));
        AssertClean();

        // The referee: a hit is the cache's black with a valid alpha, a miss is the sky. Edge
        // grazers may differ — watertightness is the hardware's own rule — so up to two of the
        // sixty-four texels may flip, and the fixture keeps the count honest by keeping the
        // triangles few and large.
        var layout = traced.Layout;
        var origin = new Vector3(0f, Bias, 0f);
        var mismatches = 0;
        var hits = 0;

        for (var y = 0; y < layout.MapResolution; y++) {
            for (var x = 0; x < layout.MapResolution; x++) {
                var direction = OctahedralMap.Direction(new(x, y), layout.MapResolution);
                var expectedHit = referee.TraceField(origin, direction, 8f).Hit;

                var atlasOrigin = layout.AtlasOrigin(new(0, 0));
                var texel = texels[((atlasOrigin.Y + y) * layout.AtlasSize.X) + atlasOrigin.X + x];
                var sky = new Vector3(0.6f, 0.45f, 0.3f) + (new Vector3(0.2f) * direction.Y);
                var actualHit = (new Vector3(texel.X, texel.Y, texel.Z) - sky).Length() > 0.05f;

                if (expectedHit != actualHit) {
                    mismatches++;
                } else if (expectedHit) {
                    Assert.True(texel.X < 1e-4f && texel.Y < 1e-4f && texel.Z < 1e-4f, $"a hit texel holds {texel}");
                    hits++;
                }

                Assert.Equal(1f, texel.W, 1e-4f);
            }
        }

        Assert.True(mismatches <= 2, $"{mismatches} texels disagree with the referee — that is not edge noise");
        Assert.True(hits >= 16, $"only {hits} rays hit — the fixture referees too little");
    }

    static void AssertClean() {
        if (VulkanDiagnostics.ErrorCount > 0) {
            Assert.Fail(
                "The run produced validation errors, so what it wrote is meaningless: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }
    }

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan device is available");

        return false;
    }
}
