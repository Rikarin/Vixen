// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary><see cref="UiTransform" /> as a homography, and as the affine it has to stay.</summary>
/// <remarks>
///     <para>
///         <b>Doc 43 § A7, issue #547.</b> The type grew a third column so that <c>rotateX</c>,
///         <c>rotateY</c> and <c>perspective</c> can be expressed: a planar element under a 3D
///         transform and a perspective projects to a plane, and that map is exactly a 2D homography.
///         Nothing draws one yet — no shader reads a <c>w</c> and no utility emits a 3D function — so
///         what this file can assert is the arithmetic, and it has to assert both halves of it.
///     </para>
///     <para>
///         ⚠ <b>The first half is that nothing affine moved, and it is asserted as <i>exact float
///         equality</i> rather than as a tolerance.</b> Every reference image in
///         <c>Vixen.Graphics.Golden.Tests.UiCompositingTests</c> was rendered through the six-float
///         arithmetic. A generalisation that agreed to six decimal places would still have to be
///         accepted into every one of those images, and "both executors changed together" is exactly
///         what a bug in a shared specification looks like — so the bar is that the new code returns
///         the same <c>float</c>, and the old expressions are written out below to compare against.
///     </para>
///     <para>
///         ⚠ <b>The second half is that the type is <i>actually</i> projective, which is the claim a
///         file of round-trips and compositions cannot make.</b> Nine fields, a bigger matrix multiply
///         and a division by <c>w</c> are all satisfied by an affine wearing three extra numbers:
///         <c>Then</c> would still compose, <c>Invert</c> would still round-trip, and every associativity
///         test would still pass. What separates the two is a property no affine has —
///         <see cref="The_image_of_a_square_s_centre_is_the_diagonal_intersection_and_not_the_centroid" />
///         is that property, and it is the load-bearing test in this file.
///     </para>
/// </remarks>
public class UiTransformProjectiveTests {
    /// <summary>The viewport every geometry below is built against.</summary>
    static readonly Rectangle Viewport = new(0, 0, 800, 600);

    /// <summary>A perspective-shaped homography: <c>w</c> grows with <i>y</i>, so far edges shrink.</summary>
    /// <remarks>
    ///     ⚠ <b>Written as nine numbers rather than composed from a <c>rotateX</c> and a
    ///     <c>perspective</c>, because those functions do not exist yet</b> — they are #550, and a
    ///     factory invented here to make a test readable would be API this issue has not earned. The
    ///     shape is the one they will produce: an unrotated element seen at an angle, whose far edge
    ///     is nearer the vanishing point than its near edge.
    /// </remarks>
    static UiTransform Perspective(float k) => UiTransform.Identity with { M23 = k };

    /// <summary>A representative affine, chosen so no cell is zero, one, or equal to another.</summary>
    /// <remarks>
    ///     ⚠ <b>Every cell distinct, because the equality assertions below are between two expressions
    ///     over the same six numbers.</b> A matrix with a zero in it, or with <c>M12 == M21</c>, makes
    ///     a transposed or dropped term produce the identical answer — and the whole point of comparing
    ///     against the old expression is to catch a term that moved.
    /// </remarks>
    static UiTransform Affine => new(1.7f, -0.35f, 0.62f, 2.4f, 13.5f, -7.25f);

    /// <summary>A second one, so composition has two different matrices to get wrong.</summary>
    static UiTransform Other => new(0.45f, 1.15f, -2.05f, 0.8f, -3.75f, 21.5f);

    /// <summary>A zeroed <see cref="UiTransform" /> is affine, and behaves as it always did.</summary>
    /// <remarks>
    ///     ⚠ <b>The reason the third column is stored as <c>M33 − 1</c>, and the assertion that says
    ///     so.</b> A homography's identity has <c>M33 = 1</c>, so a field added the obvious way would
    ///     make every zeroed struct divide by zero — turning a defined-but-wrong collapse to the
    ///     origin into a <c>NaN</c> that propagates into a vertex buffer and paints nothing, on a value
    ///     the type's own remark says is reachable and must not be read as the identity.
    /// </remarks>
    [Fact]
    public void A_default_transform_is_affine_and_collapses_to_the_origin_as_it_always_did() {
        var zero = default(UiTransform);

        Assert.True(zero.IsAffine);
        Assert.Equal(1f, zero.M33);

        // What the six-float version did with a zeroed struct, unchanged: every point to the origin.
        var landed = zero.Apply(new Vector2(37f, -19f));

        Assert.Equal(0f, landed.X);
        Assert.Equal(0f, landed.Y);
    }

    /// <summary>Every affine operation returns the float the six-float arithmetic returned.</summary>
    /// <remarks>
    ///     ⚠ <b>Exact, and the old expressions are inlined rather than referenced</b> — the point is
    ///     to compare against arithmetic that no longer exists in the tree, so it has to be written
    ///     here. Each added term is <c>× 0</c> or <c>× 1</c> on an affine, and IEEE-754 leaves
    ///     <c>a + 0</c> and <c>a × 1</c> exactly alone; this is that reasoning made falsifiable.
    /// </remarks>
    [Fact]
    public void The_affine_arithmetic_is_unchanged_to_the_last_bit() {
        var m = Affine;
        var n = Other;

        // Apply
        var point = new Vector2(11.25f, -4.5f);
        var landed = m.Apply(point);

        Assert.Equal((m.M11 * point.X) + (m.M21 * point.Y) + m.Dx, landed.X);
        Assert.Equal((m.M12 * point.X) + (m.M22 * point.Y) + m.Dy, landed.Y);

        // Determinant
        Assert.Equal((m.M11 * m.M22) - (m.M12 * m.M21), m.Determinant);

        // Then
        var composed = m.Then(n);

        Assert.Equal((m.M11 * n.M11) + (m.M12 * n.M21), composed.M11);
        Assert.Equal((m.M11 * n.M12) + (m.M12 * n.M22), composed.M12);
        Assert.Equal((m.M21 * n.M11) + (m.M22 * n.M21), composed.M21);
        Assert.Equal((m.M21 * n.M12) + (m.M22 * n.M22), composed.M22);
        Assert.Equal((m.Dx * n.M11) + (m.Dy * n.M21) + n.Dx, composed.Dx);
        Assert.Equal((m.Dx * n.M12) + (m.Dy * n.M22) + n.Dy, composed.Dy);
        Assert.True(composed.IsAffine);

        // About
        var origin = new Vector2(64f, 18f);
        var centred = m.About(origin);

        Assert.Equal(m.M11, centred.M11);
        Assert.Equal(m.M12, centred.M12);
        Assert.Equal(m.M21, centred.M21);
        Assert.Equal(m.M22, centred.M22);
        Assert.Equal(m.Dx + origin.X - ((m.M11 * origin.X) + (m.M21 * origin.Y)), centred.Dx);
        Assert.Equal(m.Dy + origin.Y - ((m.M12 * origin.X) + (m.M22 * origin.Y)), centred.Dy);
        Assert.True(centred.IsAffine);

        // Invert, which is the one that takes a branch to stay exact.
        var undo = m.Invert();

        Assert.NotNull(undo);

        var inverse = 1f / ((m.M11 * m.M22) - (m.M12 * m.M21));
        var a11 = m.M22 * inverse;
        var a12 = -m.M12 * inverse;
        var a21 = -m.M21 * inverse;
        var a22 = m.M11 * inverse;

        Assert.Equal(a11, undo!.Value.M11);
        Assert.Equal(a12, undo.Value.M12);
        Assert.Equal(a21, undo.Value.M21);
        Assert.Equal(a22, undo.Value.M22);
        Assert.Equal(-((m.Dx * a11) + (m.Dy * a21)), undo.Value.Dx);
        Assert.Equal(-((m.Dx * a12) + (m.Dy * a22)), undo.Value.Dy);
        Assert.True(undo.Value.IsAffine);

        // Bounds
        var box = m.Bounds(new Rectangle(4f, 6f, 20f, 12f));

        Assert.True(m.TryBounds(new Rectangle(4f, 6f, 20f, 12f), out var same));
        Assert.Equal(box, same);
    }

    /// <summary>
    ///     A homography sends a square's centre to where its image's diagonals cross, which is not
    ///     where an affine would send it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one test here that a nine-field affine could not pass, and therefore the only
    ///         one that says this type is projective at all.</b> An affine map preserves ratios along
    ///         every line, so it sends a midpoint to a midpoint and a square's centre to its image's
    ///         <i>centroid</i>. A projective map preserves only cross-ratio: it sends the centre to
    ///         the intersection of the image quad's diagonals, which for anything but a parallelogram
    ///         is a different point. Round-trips, compositions and associativity are all satisfied by
    ///         an affine wearing three spare numbers; this is not.
    ///     </para>
    ///     <para>
    ///         The map is <c>w = 1 + y</c>, so the unit square's far edge is halved and the image is a
    ///         trapezoid with corners <c>(0,0) (1,0) (½,½) (0,½)</c>. Its diagonals cross at
    ///         <c>(⅓, ⅓)</c> — solved by hand, not by the code under test — and its corners average to
    ///         <c>(0.375, 0.25)</c>. Both are asserted: the first is where the centre must land, and
    ///         the second is where it must <i>not</i>, because an affine implementation would put it
    ///         there and a test that checked only the first would be much weaker than it looks.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_image_of_a_square_s_centre_is_the_diagonal_intersection_and_not_the_centroid() {
        var perspective = Perspective(1f);

        // The image quad, which the assertions below are about rather than merely alongside.
        Assert.Equal(new Vector2(0f, 0f), perspective.Apply(new Vector2(0f, 0f)));
        Assert.Equal(new Vector2(1f, 0f), perspective.Apply(new Vector2(1f, 0f)));
        Assert.Equal(new Vector2(0.5f, 0.5f), perspective.Apply(new Vector2(1f, 1f)));
        Assert.Equal(new Vector2(0f, 0.5f), perspective.Apply(new Vector2(0f, 1f)));

        var centre = perspective.Apply(new Vector2(0.5f, 0.5f));

        // Where the trapezoid's diagonals cross.
        Assert.Equal(1f / 3f, centre.X, 5);
        Assert.Equal(1f / 3f, centre.Y, 5);

        // ⚠ And not where its corners average, which is where every affine in the world would put it.
        // Without this line the assertion above is satisfied by an implementation that is projective
        // in its fields and affine in its arithmetic only by coincidence of these numbers; with it,
        // the two answers are 0.042 and 0.083 apart and nothing can satisfy both.
        Assert.NotEqual(0.375f, centre.X, 3);
        Assert.NotEqual(0.25f, centre.Y, 3);
    }

    /// <summary>A homography's inverse undoes it, and is itself a homography.</summary>
    /// <remarks>
    ///     ⚠ <b>The inverse of a projective map has a projective part, and an implementation that
    ///     inverted the linear block and copied the third column through would still round-trip on the
    ///     <i>origin</i>.</b> So the points probed are off-origin and off-axis, where the perspective
    ///     term actually contributes, and the inverse is asserted to be non-affine in its own right.
    /// </remarks>
    [Fact]
    public void A_homography_inverts_to_a_homography_that_undoes_it() {
        var perspective = Perspective(0.25f).Then(Affine);

        Assert.False(perspective.IsAffine);

        var undo = perspective.Invert();

        Assert.NotNull(undo);
        Assert.False(undo!.Value.IsAffine);

        foreach (var point in new[] { new Vector2(3f, 5f), new Vector2(-11f, 2.5f), new Vector2(40f, -6f) }) {
            var back = undo.Value.Apply(perspective.Apply(point));

            Assert.Equal(point.X, back.X, 3);
            Assert.Equal(point.Y, back.Y, 3);
        }
    }

    /// <summary>Composition means the same thing for a homography as applying one map then the other.</summary>
    /// <remarks>
    ///     ⚠ <b>The argument runs second, which is what <see cref="UiTransform.Then" /> is named
    ///     for</b> — and a transposed 3×3 multiply is the way this goes wrong, which on the affine
    ///     block alone can be invisible when only one operand is projective. Both operands here carry
    ///     a perspective term, and they are different ones.
    /// </remarks>
    [Fact]
    public void Composing_two_homographies_is_applying_one_and_then_the_other() {
        var first = Perspective(0.2f).Then(Affine);
        var second = (UiTransform.Identity with { M13 = -0.05f, M23 = 0.15f }).Then(Other);

        var composed = first.Then(second);

        Assert.False(composed.IsAffine);

        foreach (var point in new[] { new Vector2(2f, 9f), new Vector2(-7.5f, 1f), new Vector2(18f, 22f) }) {
            var stepwise = second.Apply(first.Apply(point));
            var together = composed.Apply(point);

            Assert.Equal(stepwise.X, together.X, 3);
            Assert.Equal(stepwise.Y, together.Y, 3);
        }
    }

    /// <summary>Re-centring a homography means what it means for an affine: move, map, move back.</summary>
    /// <remarks>
    ///     ⚠ <b>The projective generalisation of <see cref="UiTransform.About(Vector2)" /> has a trap
    ///     the affine one does not: the translation row is built from the map's <i>original</i> linear
    ///     cells, not the re-centred ones.</b> On an affine those two are equal, so substituting the
    ///     wrong pair is invisible in every existing test — which is exactly why this one has an
    ///     origin far from zero and a perspective term big enough to separate them.
    /// </remarks>
    [Fact]
    public void Re_centring_a_homography_is_moving_it_mapping_and_moving_back() {
        var origin = new Vector2(48f, 32f);
        var centred = Perspective(0.02f).Then(Affine).About(origin);
        var plain = Perspective(0.02f).Then(Affine);

        foreach (var point in new[] { new Vector2(50f, 30f), new Vector2(12f, 60f), new Vector2(90f, 8f) }) {
            var expected = plain.Apply(new Vector2(point.X - origin.X, point.Y - origin.Y));
            var actual = centred.Apply(point);

            Assert.Equal(expected.X + origin.X, actual.X, 3);
            Assert.Equal(expected.Y + origin.Y, actual.Y, 3);
        }
    }

    /// <summary>A perspective a pixel can see is not the identity, however small its number looks.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The trap this bound exists for, and it is a unit error rather than a rounding
    ///         one.</b> <see cref="UiTransform.IsIdentity" /> gates whether a group is opened at all,
    ///         and it judged the linear cells against <c>1e-3</c> — right for a dimensionless number.
    ///         The perspective column is a <i>reciprocal length</i>: <c>perspective(2000px)</c> puts
    ///         <c>5e-4</c> in it, which is under that bound. Reusing it would declare a real
    ///         perspective the identity, open no group, and draw the element flat, with no validation
    ///         error and no counter out of range — the failure mode this repository's transforms keep
    ///         producing.
    ///     </para>
    ///     <para>
    ///         The value asserted below is what that transform does over a hundred points: <c>w</c>
    ///         runs from one to 1.05, so the far edge is five per cent nearer the vanishing point.
    ///         That is not a rounding.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_perspective_of_two_thousand_points_is_not_the_identity() {
        var perspective = Perspective(5e-4f);

        Assert.False(perspective.IsAffine);
        Assert.False(perspective.IsIdentity);

        // What it is worth in pixels, so the assertion above is not a statement about an epsilon.
        var near = perspective.Apply(new Vector2(100f, 0f));
        var far = perspective.Apply(new Vector2(100f, 100f));

        Assert.Equal(100f, near.X, 4);
        Assert.Equal(100f / 1.05f, far.X, 3);

        // And the bound still catches what it is for: a rotation of a twentieth of a degree.
        Assert.True(UiTransform.Rotation(0.02f, Vector2.Zero).IsIdentity);
    }

    /// <summary>A quad with a corner behind the eye has no bound, and says so rather than inventing one.</summary>
    /// <remarks>
    ///     ⚠ <b>The corners project to four <i>finite</i> points, which is what makes this dangerous
    ///     rather than obvious.</b> Under <c>w = 1 − y</c> a corner past <c>y = 1</c> has negative
    ///     <c>w</c> and divides to a point reflected through the vanishing point — so a corner-wise
    ///     box is not loose, it is a bound on a shape that runs to infinity, computed from points on
    ///     the wrong side of the screen. Refusing is the only honest answer from a type that does not
    ///     clip.
    /// </remarks>
    [Fact]
    public void A_rectangle_straddling_the_eye_plane_has_no_bound() {
        var perspective = Perspective(-1f);

        // Entirely in front: an ordinary bound, and the near edge is the wide one.
        Assert.True(perspective.TryBounds(new Rectangle(0f, 0f, 1f, 0.5f), out var bounds));
        Assert.Equal(0f, bounds.X, 4);
        Assert.Equal(2f, bounds.Width, 3);

        // Straddling it: refused, and `Bounds` degrades to an empty rectangle rather than to a
        // plausible one.
        Assert.False(perspective.TryBounds(new Rectangle(0f, 0f, 1f, 2f), out var none));
        Assert.Equal(default, none);
        Assert.Equal(default, perspective.Bounds(new Rectangle(0f, 0f, 1f, 2f)));

        // ⚠ And exactly on it, which is the case a `< 0` test would let through: `w` is zero, the
        // point has no image at all, and dividing would put an infinity into every edge of the box.
        Assert.False(perspective.TryBounds(new Rectangle(0f, 0f, 1f, 1f), out _));
    }

    /// <summary>
    ///     The composite quad's four positions are the homography's, and the texture coordinate
    ///     between them is not — measured, at the one point where the error is largest and has a
    ///     closed form.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Issue #548, and the half of it that needs no device.</b> Every previous pass on
    ///         that issue described this defect and none measured it. What
    ///         <c>UiGeometryBuilder.Quad</c> does under a homography is right about the four corners
    ///         and wrong everywhere between them: it calls <see cref="UiTransform.Apply" />, which
    ///         divides by <c>w</c> and throws it away, so both executors then interpolate the texture
    ///         coordinate <i>linearly</i> across a quad whose correct interpolation is projective.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The oracle is the diagonal, which is where the two answers differ most and where
    ///         the difference is a single number.</b> The quad is two triangles sharing the corner-0
    ///         to corner-2 diagonal, and a homography maps the midpoint of that diagonal in element
    ///         space to the intersection of the <i>image</i> diagonals — the property
    ///         <see cref="The_image_of_a_square_s_centre_is_the_diagonal_intersection_and_not_the_centroid" />
    ///         pins. That image point lies on the drawn diagonal, so what a linear interpolator
    ///         samples there is <c>lerp(t0, t2, λ)</c> for the point's <i>screen</i> parameter λ, and
    ///         what it should sample is the midpoint of the two coordinates. An affine has λ = ½
    ///         exactly; a perspective does not, and the gap between them <b>is</b> the defect.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, so the predicate can be false.</b> The same measurement is taken
    ///         with an affine in the same place, and it has to come out at ½ and zero — otherwise
    ///         this test is measuring the arithmetic of its own oracle rather than the transform.
    ///     </para>
    ///     <para>
    ///         The error is reported in <i>surface pixels</i> rather than in texture units, because
    ///         that is the quantity somebody looking at the picture would see, and because a
    ///         normalised coordinate makes a 30-pixel smear read as 0.04.
    ///     </para>
    ///     <para>
    ///         <b>Measured:</b> λ is <b>0.5604</b> where it should be a half, and what a
    ///         400×300 group's centre samples is <b>30.4 surface pixels</b> away from the texel that
    ///         belongs there. Both bounds are set well under those, because the number that matters is
    ///         "not a rounding difference" and pinning either to four figures would make a change to
    ///         the fringe or to the ink bounds fail this rather than the thing it is about. The
    ///         readings are here so that a later pass can see whether they moved.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_composited_group_under_a_homography_samples_the_wrong_texel_down_its_diagonal() {
        var box = new Rectangle(200, 100, 400, 300);
        var centre = new Vector2(box.X + (box.Width / 2f), box.Y + (box.Height / 2f));

        // Strong enough that the far edge is visibly nearer the vanishing point than the near one,
        // and nowhere near the eye plane — a corner behind it has no finite image at all, which is
        // the separate half of #548 and not what this measures.
        var projective = Perspective(0.0008f).About(centre);
        var affine = new UiTransform(1.2f, 0.15f, -0.2f, 0.9f, 11f, -6f).About(centre);

        var (skew, skewed) = Diagonal(box, projective);
        var (flat, unskewed) = Diagonal(box, affine);

        // The instrument first: the affine case is the control, and it is the reading that says the
        // measurement below is of the transform and not of the arithmetic that takes it.
        Assert.Equal(0.5f, flat, 1e-4f);
        Assert.True(unskewed < 0.05f, $"the affine control smeared by {unskewed:0.###} px, so this measures itself.");

        // And the defect, as a number. λ is well away from a half, and what that costs at the centre
        // of a 400×300 group is tens of pixels — not a rounding difference, and not something a
        // tolerance on a reference image would absorb.
        Assert.True(MathF.Abs(skew - 0.5f) > 0.02f, $"the projective diagonal parameter was {skew:0.####}.");
        Assert.True(skewed > 10f, $"the projective case smeared by only {skewed:0.###} px.");
    }

    /// <summary>
    ///     The vertex format has nowhere to put the <c>w</c>, which is the whole of why the
    ///     measurement above stands.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A tripwire and not a preference.</b> <see cref="UiVertex" /> is
    ///         position/texture/colour/shape, so <c>UiGeometryBuilder.Quad</c> has no field to write
    ///         <see cref="UiTransform.Project" />'s third component into even if it wanted to — and
    ///         until it has one, neither executor can divide per fragment. Naming the four members
    ///         here rather than counting them means a <c>W</c> arriving is reported as the arrival it
    ///         is, by name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The day this goes red is the day a reader has work to do in three other
    ///         places</b>, and it is written down here because a reflection assertion with no
    ///         instructions attached is the one that gets deleted: the measurement above becomes an
    ///         agreement rather than a divergence, <c>SoftwareUiRasterizer.Triangle</c> owes
    ///         <c>u/w</c>, <c>v/w</c>, <c>1/w</c> and a clip against <c>w = 0</c> before it
    ///         rasterises, and <c>RefusalExpiry.txt</c> anchors <c>perspective-*</c>,
    ///         <c>scale-*</c> and <c>translate-*</c> directly on this member's absence with four more
    ///         rows behind them.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_vertex_format_has_no_w_and_three_refusals_rest_on_that() {
        var members = typeof(UiVertex)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Color", "Position", "Shape", "Texture"], members);
    }

    /// <summary>
    ///     Builds one composited group under a transform and measures its diagonal.
    /// </summary>
    /// <param name="box">The group's box.</param>
    /// <param name="placed">What the composite quad is placed under.</param>
    /// <returns>
    ///     The screen parameter of the element centre's image along the drawn diagonal, and how far
    ///     the linear interpolation's answer is from the right one there, in surface pixels.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>The four element-space corners are recovered from the texture coordinates rather than
    ///     from <paramref name="box" />.</b> A layer's composite quad covers the group's <i>ink</i>
    ///     bounds, which the builder outsets for antialiasing and clips to the viewport, so a corner
    ///     assumed from the box would be the wrong point and the measurement would be of that
    ///     mistake. The coordinates are the untransformed position over the viewport by construction,
    ///     which makes the recovery exact and makes the assertion below a check on both.
    /// </remarks>
    static (float Parameter, float Pixels) Diagonal(Rectangle box, UiTransform placed) {
        var list = new DrawList();

        list.BeginFrame();

        list.Add(
            new DrawCommand(
                DrawCommandKind.LayerPush,
                box.X,
                box.Y,
                box.Width,
                box.Height,
                new Color4(1f, 1f, 1f, 0.5f),
                0f,
                0f
            ) {
                Transform = placed
            }
        );

        list.Add(
            new DrawCommand(DrawCommandKind.Rectangle, box.X, box.Y, box.Width, box.Height, Color4.White, 0f, 0f)
        );

        list.Add(new DrawCommand(DrawCommandKind.LayerPop, 0f, 0f, 0f, 0f, Color4.White, 0f, 0f));
        list.EndFrame();

        var geometry = new UiGeometryBuilder().Build(list, new GlyphFieldCache(new GlyphAtlas(512, 512)), Viewport);
        var layer = Assert.Single(geometry.Layers);
        var composite = geometry.Draws[layer.First + layer.Count];

        Assert.Equal(BatchKind.Image, composite.Kind);

        var quad = Enumerable.Range(0, 4)
            .Select(corner => geometry.Vertices[(int)geometry.Indices[composite.First + corner]])
            .ToList();

        // Vertices 0 and 2 are the diagonal the two triangles share; `Quad` winds 0-1-2 and 0-2-3.
        var first = quad[0];
        var third = quad[2];

        var cornerOfFirst = Untransformed(first.Texture);
        var cornerOfThird = Untransformed(third.Texture);

        // The positions are the homography's, exactly — which is what makes the corners right and the
        // middle wrong rather than everything being wrong.
        Assert.Equal(placed.Apply(cornerOfFirst).X, first.Position.X, 1e-2f);
        Assert.Equal(placed.Apply(cornerOfFirst).Y, first.Position.Y, 1e-2f);
        Assert.Equal(placed.Apply(cornerOfThird).X, third.Position.X, 1e-2f);
        Assert.Equal(placed.Apply(cornerOfThird).Y, third.Position.Y, 1e-2f);

        var middle = placed.Apply((cornerOfFirst + cornerOfThird) / 2f);
        var along = third.Position - first.Position;
        var parameter = Vector2.Dot(middle - first.Position, along) / along.LengthSquared();

        var sampled = Vector2.Lerp(first.Texture, third.Texture, parameter);
        var wanted = (first.Texture + third.Texture) / 2f;
        var error = sampled - wanted;

        return (
            parameter,
            new Vector2(error.X * Viewport.Width, error.Y * Viewport.Height).Length()
        );
    }

    /// <summary>Where a composite quad's texture coordinate came from, in document pixels.</summary>
    static Vector2 Untransformed(Vector2 texture) =>
        new(Viewport.X + (texture.X * Viewport.Width), Viewport.Y + (texture.Y * Viewport.Height));
}
