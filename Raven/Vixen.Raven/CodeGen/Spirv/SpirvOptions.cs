// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>Knobs for the SPIR-V backend.</summary>
internal sealed class SpirvOptions {
    /// <summary>
    ///     The version word written into the header. 1.0 is the default because it is
    ///     what Vulkan 1.0 accepts, and nothing this backend emits needs a later one.
    /// </summary>
    public uint Version { get; init; } = 0x00010000;
}
