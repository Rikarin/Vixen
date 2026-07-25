// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>Knobs for the SPIR-V backend.</summary>
public sealed class SpirvOptions {
    /// <summary>
    ///     The version word written into the header. 1.0 is the default because it is
    ///     what Vulkan 1.0 accepts, and nothing this backend emits needs a later one.
    /// </summary>
    public uint Version { get; init; } = 0x00010000;

    /// <summary>
    ///     The descriptor set every binding is decorated with. Raven has no syntax for
    ///     sets yet, so everything lands in one.
    /// </summary>
    public uint DescriptorSet { get; init; }
}
