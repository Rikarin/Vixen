// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Vixen.Graphics.Vulkan;

public sealed unsafe partial class VulkanDevice {
    /// <inheritdoc />
    public AccelerationStructureSizes GetAccelerationStructureSizes(in AccelerationStructureBuildInput input) {
        var extension = RequireRayTracing("Acceleration-structure sizes were");

        // Zero addresses, deliberately: sizing reads the counts and the shapes, never the memory,
        // and Vulkan permits null addresses here for exactly that reason. Everything else about the
        // geometry comes from the same helper the build uses — sizing and building describing
        // different inputs is the Vulkan bug class the shared helper exists to kill.
        var geometry = DescribeGeometry(input, false, out var primitiveCount);
        var build = DescribeBuild(input.Kind, &geometry);

        var sizes = new AccelerationStructureBuildSizesInfoKHR {
            SType = StructureType.AccelerationStructureBuildSizesInfoKhr
        };

        extension.GetAccelerationStructureBuildSizes(
            device,
            AccelerationStructureBuildTypeKHR.DeviceKhr,
            &build,
            &primitiveCount,
            &sizes
        );

        return new((long)sizes.AccelerationStructureSize, (long)sizes.BuildScratchSize);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The backing buffer is created here rather than taken from the caller, because nothing a
    ///     caller could do with it is legal: Vulkan owns the layout of the memory behind a structure,
    ///     and the one fact about it anyone needs — the GPU address — comes from
    ///     <see cref="GetAccelerationStructureAddress" />, not from the buffer.
    /// </remarks>
    public AccelerationStructureHandle CreateAccelerationStructure(
        in AccelerationStructureDescription description
    ) {
        var extension = RequireRayTracing($"Acceleration structure '{description.Name}' was");

        // The size the device itself answered is never zero, so a zero here is a caller that
        // invented the number — the mistake the description's own docs call corruption.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.Size, nameof(description));

        var backing = CreateBuffer(new BufferDescription(
            description.Size,
            BufferUsage.AccelerationStructureStorage | BufferUsage.ShaderDeviceAddress,
            MemoryAccess.DeviceLocal,
            string.IsNullOrEmpty(description.Name) ? "" : $"{description.Name} storage"
        ));

        var create = new AccelerationStructureCreateInfoKHR {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = Resolve(backing).Handle,
            Offset = 0,
            Size = (ulong)description.Size,
            Type = VulkanEnums.ToVulkan(description.Kind)
        };

        AccelerationStructureKHR handle;
        var result = extension.CreateAccelerationStructure(device, &create, null, &handle);

        if (result != Result.Success) {
            // The buffer was made for this structure alone, so a refusal must return it or the
            // failure is also a leak. Destroy defers, which is more than a never-used buffer needs
            // and exactly as safe.
            Destroy(backing);
            throw new VulkanException($"vkCreateAccelerationStructureKHR failed with {result}.");
        }

        Name(ObjectType.AccelerationStructureKhr, handle.Handle, description.Name);

        var addressInfo = new AccelerationStructureDeviceAddressInfoKHR {
            SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
            AccelerationStructure = handle
        };

        var address = extension.GetAccelerationStructureDeviceAddress(device, &addressInfo);

        lock (gate) {
            return new(accelerationStructures.Add(new VulkanAccelerationStructure {
                Handle = handle,
                Buffer = backing,
                Address = address,
                Kind = description.Kind
            }));
        }
    }

    /// <inheritdoc />
    public ulong GetAccelerationStructureAddress(AccelerationStructureHandle handle) {
        RequireRayTracing("An acceleration-structure address was");
        return Resolve(handle).Address;
    }

    /// <inheritdoc />
    public void Destroy(AccelerationStructureHandle handle) {
        if (Take(accelerationStructures, handle.Value) is not VulkanAccelerationStructure structure) {
            return;
        }

        Retire(() => khrAccelerationStructure?.DestroyAccelerationStructure(device, structure.Handle, null));

        // After the structure's own retirement is queued, so the deferred actions run in this
        // order: the structure first, then the buffer it lives in.
        Destroy(structure.Buffer);
    }

    internal VulkanAccelerationStructure Resolve(AccelerationStructureHandle handle) {
        lock (gate) {
            if (accelerationStructures.TryGet(handle.Value, out var structure)
                && structure is VulkanAccelerationStructure resolved) {
                return resolved;
            }
        }

        throw new ArgumentException("An acceleration-structure handle referred to nothing.");
    }

    /// <summary>One build input as Vulkan geometry, for sizing and for building.</summary>
    /// <param name="input">What the caller described.</param>
    /// <param name="addressed">
    ///     Whether to resolve the buffers to GPU addresses. Sizing passes false — it reads counts,
    ///     and its buffers may not even exist yet; a build passes true.
    /// </param>
    /// <param name="primitiveCount">Triangles for a bottom level, instances for a top.</param>
    /// <remarks>
    ///     One method used by both callers, deliberately: Vulkan requires the geometry that sized a
    ///     structure and the geometry that builds it to describe the same input, and two descriptions
    ///     drift the day one of them is edited. This is the single place the translation exists.
    /// </remarks>
    internal AccelerationStructureGeometryKHR DescribeGeometry(
        in AccelerationStructureBuildInput input,
        bool addressed,
        out uint primitiveCount
    ) {
        if (input.Kind == AccelerationStructureKind.TopLevel) {
            var instances = input.Instances;
            primitiveCount = (uint)Math.Max(0, instances.Count);

            return new() {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.InstancesKhr,
                Geometry = new() {
                    Instances = new() {
                        SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,

                        // Packed records, not pointers to records — the layout
                        // AccelerationStructureInstance mirrors byte for byte.
                        ArrayOfPointers = false,
                        Data = new() {
                            DeviceAddress = addressed
                                ? AddressOf(instances.Buffer, "instance") + (ulong)instances.Offset
                                : 0
                        }
                    }
                }
            };
        }

        var triangles = input.Triangles;
        primitiveCount = (uint)Math.Max(0, triangles.IndexCount / 3);

        return new() {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.TrianglesKhr,

            // Opaque, so no any-hit shading is ever invoked — the ray queries here want the nearest
            // hit and nothing else, and opaque geometry is the fast path on every vendor.
            Flags = GeometryFlagsKHR.OpaqueBitKhr,
            Geometry = new() {
                Triangles = new() {
                    SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,

                    // One vertex format and one index width, the AccelerationStructureTriangles
                    // contract: float3 positions, uint32 indices.
                    VertexFormat = VkFormat.R32G32B32Sfloat,
                    VertexData = new() {
                        DeviceAddress = addressed
                            ? AddressOf(triangles.VertexBuffer, "vertex") + (ulong)triangles.VertexOffset
                            : 0
                    },
                    VertexStride = (ulong)triangles.VertexStride,

                    // The highest index the build may read, not the count — Vulkan's fencepost.
                    MaxVertex = (uint)Math.Max(0, triangles.VertexCount - 1),
                    IndexType = IndexType.Uint32,
                    IndexData = new() {
                        DeviceAddress = addressed
                            ? AddressOf(triangles.IndexBuffer, "index") + (ulong)triangles.IndexOffset
                            : 0
                    }
                }
            }
        };
    }

    /// <summary>The build description both sizing and building start from.</summary>
    /// <param name="kind">Which level is being built.</param>
    /// <param name="geometry">The geometry, already described.</param>
    /// <remarks>
    ///     A pointer parameter rather than <c>in</c>, because the address has to outlive the return
    ///     — Vulkan reads <c>PGeometries</c> when the structure is consumed, so the caller must own
    ///     the geometry's storage and say so by handing over its address.
    /// </remarks>
    internal static AccelerationStructureBuildGeometryInfoKHR DescribeBuild(
        AccelerationStructureKind kind,
        AccelerationStructureGeometryKHR* geometry
    ) =>
        new() {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = VulkanEnums.ToVulkan(kind),

            // Trace speed over build speed: structures here are built rarely and queried every
            // frame, which is the trade this flag names.
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1,
            PGeometries = geometry
        };

    /// <summary>A buffer's GPU address, checked before it is taken.</summary>
    /// <param name="handle">The buffer.</param>
    /// <param name="role">What the build wants it for, for the refusal.</param>
    /// <remarks>
    ///     Core from 1.2, which <see cref="VulkanFeatures.HasRayTracingExtensions" /> made part of
    ///     the capability — so there is exactly one spelling of the entry point. The usage check is
    ///     here because taking an address the buffer was not created for is undefined rather than
    ///     refused, and the validation layers name the buffer while this names the mistake.
    /// </remarks>
    internal ulong AddressOf(BufferHandle handle, string role) {
        var buffer = Resolve(handle);

        if ((buffer.Description.Usage & BufferUsage.ShaderDeviceAddress) == 0) {
            throw new ArgumentException(
                $"The {role} buffer '{buffer.Description.Name}' was created without ShaderDeviceAddress, "
                + "and a build addresses its memory by GPU address — the usage has to be declared at "
                + "creation, like every other."
            );
        }

        var info = new BufferDeviceAddressInfo {
            SType = StructureType.BufferDeviceAddressInfo,
            Buffer = buffer.Handle
        };

        return Api.GetBufferDeviceAddress(device, &info);
    }

    /// <summary>The extension, or the refusal every ray-tracing entry point owes.</summary>
    /// <param name="what">The sentence's subject, e.g. <c>"Acceleration structure 'x' was"</c>.</param>
    KhrAccelerationStructure RequireRayTracing(string what) {
        if (khrAccelerationStructure is { } extension && Features.HasRayTracing) {
            return extension;
        }

        throw new NotSupportedException(
            $"{what} asked for on a device that reports no ray tracing. Ask Features.HasRayTracing "
            + "and take the distance-field tracer."
        );
    }
}
