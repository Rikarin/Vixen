// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;

namespace Vixen.Rendering.IrradianceFields.Tests;

/// <summary>Scenes whose distance is arithmetic, so a filler is measured on its own.</summary>
/// <remarks>
///     A baked field and a filler tested together disagree with a closed form for two possible
///     reasons and cannot say which. These have no sampling error at all, so anything the filler gets
///     wrong is the filler — the same argument that made <c>DistanceFieldTracer</c>'s tests march an
///     analytic sphere rather than a baked one.
/// </remarks>
static class AnalyticFields {
    /// <summary>A world with nothing in it.</summary>
    public static IDistanceField Empty { get; } = new Analytic(_ => 1e6f);

    /// <summary>A solid sphere.</summary>
    /// <param name="centre">Where it is.</param>
    /// <param name="radius">How big.</param>
    /// <returns>The field.</returns>
    public static IDistanceField Sphere(Vector3 centre, float radius) =>
        new Analytic(position => (position - centre).Length() - radius);

    /// <summary>A closed box with a cavity in it — walls, and air inside them.</summary>
    /// <param name="outer">Half the width of the outside.</param>
    /// <param name="inner">Half the width of the cavity.</param>
    /// <returns>The field.</returns>
    /// <remarks>
    ///     Subtraction is a maximum against a negated field, which is exact here because both are
    ///     exact — the usual caveat that a CSG of distance fields under-reports near the join does not
    ///     bite when the two surfaces are this far apart.
    /// </remarks>
    public static IDistanceField HollowBox(float outer, float inner) =>
        new Analytic(position => MathF.Max(Box(position, outer), -Box(position, inner)));

    /// <summary>The signed distance to an axis-aligned cube centred on the origin.</summary>
    static float Box(Vector3 position, float extent) {
        var q = new Vector3(
            MathF.Abs(position.X) - extent,
            MathF.Abs(position.Y) - extent,
            MathF.Abs(position.Z) - extent
        );

        var outside = Vector3.Max(q, Vector3.Zero).Length();
        var inside = MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);

        return outside + inside;
    }

    /// <summary>A field that is a function, with its gradient differenced from it.</summary>
    sealed class Analytic(Func<Vector3, float> distance) : IDistanceField {
        public float Sample(Vector3 position) => distance(position);

        public Vector3 SampleGradient(Vector3 position) {
            const float Step = 1e-3f;

            var gradient = new Vector3(
                Sample(position + new Vector3(Step, 0, 0)) - Sample(position - new Vector3(Step, 0, 0)),
                Sample(position + new Vector3(0, Step, 0)) - Sample(position - new Vector3(0, Step, 0)),
                Sample(position + new Vector3(0, 0, Step)) - Sample(position - new Vector3(0, 0, Step))
            );

            var length = gradient.Length();

            return length > MathUtil.ZeroTolerance ? gradient / length : Vector3.Zero;
        }
    }
}

/// <summary>Lighting a test can state in one line.</summary>
/// <param name="sky">What a ray that hit nothing sees.</param>
/// <param name="surface">What a ray that hit something sees.</param>
sealed class Radiance(Func<Vector3, Vector3> sky, Func<Vector3, Vector3>? surface = null) : IRadianceSource {
    /// <summary>A sky of one radiance everywhere, over surfaces that give back nothing.</summary>
    /// <param name="radiance">How bright the sky is.</param>
    /// <returns>The source.</returns>
    public static Radiance Uniform(float radiance) => new(_ => new(radiance));

    public Vector3 Sky(Vector3 direction) => sky(direction);

    public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) =>
        surface?.Invoke(position) ?? Vector3.Zero;
}
