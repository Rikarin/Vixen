// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Reflection;

/// <summary>One interstage value with the location it occupies.</summary>
/// <param name="Stream">The stream as the shader declared it.</param>
/// <param name="Location">The location index, in both directions.</param>
public sealed record PlannedStream(IrStream Stream, int Location);

/// <summary>
///     Assigns every interstage value its location.
/// </summary>
/// <remarks>
///     <para>
///         The single place stage-interface locations are decided, for the same reason
///         <see cref="BindingPlan" /> is the single place descriptor bindings are: both emitters and
///         <see cref="ReflectionBuilder" /> read this plan, so the GLSL <c>layout(location = …)</c>,
///         the SPIR-V <c>Location</c> decoration and the numbers the engine builds a vertex layout
///         from cannot drift.
///     </para>
///     <para>
///         <strong>A stream's location is a property of the shader, not of the stage.</strong> It is
///         the stream's index in the shader's declaration order, so the stage that writes it and the
///         stage that reads it arrive at the same number without either knowing about the other —
///         which is the whole reason the feature works. Deriving it from "index among this stage's
///         outputs" instead would have the vertex stage and the fragment stage disagree the moment
///         one of them touches a stream the other does not.
///     </para>
///     <para>
///         The consequence, stated rather than discovered: a stage's own parameters are located
///         <em>after</em> the streams (<see cref="ParameterBase" />), so adding a stream to a shader
///         renumbers its vertex attributes. That is visible in the reflection the engine builds its
///         vertex layout from, which is where a renumbering has to be visible; the alternative —
///         locating streams after the parameters — would make a stream's location depend on which
///         stage was looking at it, and there is no number both stages could agree on.
///     </para>
/// </remarks>
public static class StreamPlan {
    /// <summary>The plan for one shader: every declared stream, in declaration order.</summary>
    public static ImmutableArray<PlannedStream> Of(IrShader shader) {
        ArgumentNullException.ThrowIfNull(shader);
        return [.. shader.Streams.Select((stream, index) => new PlannedStream(stream, index))];
    }

    /// <summary>
    ///     The location a stage's own parameters and return value start at, which is past every
    ///     stream the shader declares.
    /// </summary>
    public static int ParameterBase(IrShader shader) {
        ArgumentNullException.ThrowIfNull(shader);
        return shader.Streams.Count;
    }

    /// <summary>
    ///     The location a stage's return value occupies, or 0 for a fragment stage.
    /// </summary>
    /// <remarks>
    ///     A fragment output is a render-target index rather than an interstage location — location
    ///     0 is target 0 — so it stays at 0 whatever the shader declares. That is also why a stream
    ///     written by a fragment stage has no consumer (<c>RVN3005</c>): there is no downstream
    ///     interface for it to reach.
    /// </remarks>
    public static int OutputBase(IrShader shader, ShaderStage stage) =>
        stage == ShaderStage.Pixel ? 0 : ParameterBase(shader);

    /// <summary>
    ///     The location each of a stage's own parameters occupies, in order — <c>null</c> for one
    ///     the pipeline supplies rather than the host.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A built-in has no location: it arrives as <c>gl_VertexIndex</c> or a
    ///         <c>BuiltIn</c>-decorated variable, and <c>Location</c> and <c>BuiltIn</c> are
    ///         mutually exclusive decorations. So it must not <em>consume</em> one either — a
    ///         <c>SV_VertexID</c> between two attributes would otherwise leave a hole in the vertex
    ///         layout the host binds against.
    ///     </para>
    ///     <para>
    ///         Here rather than counted independently in each of the two emitters and the
    ///         reflection, for the reason the rest of this file exists: three copies of a numbering
    ///         rule is three chances to disagree, and this one is invisible until a mesh renders
    ///         with its normals in the tangent slot.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<int?> InputLocations(IrShader shader, IrEntryPoint entryPoint) {
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentNullException.ThrowIfNull(entryPoint);

        var locations = ImmutableArray.CreateBuilder<int?>(entryPoint.Inputs.Count);
        var next = ParameterBase(shader);

        foreach (var input in entryPoint.Inputs) {
            locations.Add(StageBuiltIns.Of(input.Semantic, entryPoint.Stage) is null ? next++ : null);
        }

        return locations.ToImmutable();
    }

    /// <summary>The location assigned to one stream, or -1 when the shader does not declare it.</summary>
    public static int LocationOf(IrShader shader, IrStream stream) {
        ArgumentNullException.ThrowIfNull(shader);

        for (var i = 0; i < shader.Streams.Count; i++) {
            if (ReferenceEquals(shader.Streams[i], stream)) {
                return i;
            }
        }

        return -1;
    }
}
