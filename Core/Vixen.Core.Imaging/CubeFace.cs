// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Imaging;

/// <summary>One face of a cube map, in the order a cube texture's layers are.</summary>
/// <remarks>
///     The order is the one every API agrees on — +X, −X, +Y, −Y, +Z, −Z — because it is the layer
///     order of a cube texture, and a shader that looks a direction up in a cube map gets the face
///     the hardware picked rather than the one an engine numbered.
/// </remarks>
public enum CubeFace {
    /// <summary>Looking down +X.</summary>
    PositiveX = 0,

    /// <summary>Looking down −X.</summary>
    NegativeX = 1,

    /// <summary>Looking down +Y.</summary>
    PositiveY = 2,

    /// <summary>Looking down −Y.</summary>
    NegativeY = 3,

    /// <summary>Looking down +Z.</summary>
    PositiveZ = 4,

    /// <summary>Looking down −Z.</summary>
    NegativeZ = 5
}
