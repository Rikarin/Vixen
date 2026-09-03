// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Raven.Transpile;

/// <summary>
///     Which GLSL dialect SPIRV-Cross is asked for.
/// </summary>
/// <remarks>
///     <para>
///         <b>Only ESSL 3.00 is claimed.</b> The version number is a knob on SPIRV-Cross's GLSL
///         backend rather than a separate code path, so <c>Essl310</c> and <c>Essl320</c> cost
///         nothing to offer and are genuinely produced — but the thing this project is <em>tested</em>
///         against, shader by shader over the whole of <c>Raven/Library</c>, is
///         <see cref="Essl300" />, because that is the floor: WebGL 2 and the GLES deny-list device
///         are both exactly ES 3.0, and every profile above it is a superset.
///     </para>
///     <para>
///         ⚠ <b>ES 3.0 has no compute stage and no storage buffer.</b> That is not a gap in this
///         translator, it is the version: <c>GL_ES_VERSION_3_1</c> is where
///         <c>OpTypeRuntimeArray</c>-in-a-block and a local workgroup first exist. A compute entry
///         point asked for <see cref="Essl300" /> is refused at the backend with <c>RVN4001</c>
///         rather than transpiled into something a driver will reject at load, which is the
///         difference between a message naming the shader and a blank frame.
///     </para>
/// </remarks>
enum GlslDialect {
    /// <summary>
    ///     <c>#version 300 es</c> — WebGL 2, GLES 3.0. Vertex and fragment only.
    /// </summary>
    Essl300,

    /// <summary>
    ///     <c>#version 310 es</c> — GLES 3.1. Adds compute, storage buffers and explicit bindings.
    /// </summary>
    Essl310,

    /// <summary>
    ///     <c>#version 320 es</c> — GLES 3.2. Adds geometry and tessellation.
    /// </summary>
    Essl320
}

/// <summary>What each dialect is, in the two terms SPIRV-Cross and the stage check need.</summary>
static class GlslDialects {
    /// <summary>The <c>#version</c> number, as SPIRV-Cross's <c>GlslVersion</c> option wants it.</summary>
    public static uint Version(this GlslDialect dialect) =>
        dialect switch {
            GlslDialect.Essl300 => 300,
            GlslDialect.Essl310 => 310,
            GlslDialect.Essl320 => 320,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "No version for this dialect.")
        };

    /// <summary>The name to put in a diagnostic — <c>RVN4001</c>'s <c>{1}</c>.</summary>
    public static string Describe(this GlslDialect dialect) => $"ESSL {dialect.Version() / 100}.{dialect.Version() % 100 / 10:0}0";
}
