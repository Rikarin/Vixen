// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Ui.Layout;
using Vixen.Ui.Rendering;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>Reads <c>transform</c>, <c>rotate</c> and <c>scale</c> off a computed style.</summary>
/// <remarks>
///     <para>
///         <b>The second half of the engine's transform stage, and it works nothing like the first.</b>
///         <see cref="TranslationReader" /> resolves <c>translate</c> into two scalars that
///         <c>UiDocument.Accumulate</c> adds to a position, so both the draw list and the hit test read
///         one already-translated rectangle and cannot disagree. A rotation and a scale cannot be
///         folded into a position — they change the box's <i>shape</i> — so they arrive as a
///         <see cref="UiTransform" /> that each consumer applies where it applies things: the geometry
///         builder to a composited group's four composite vertices, the hit test to the pointer on the
///         way down.
///     </para>
///     <para>
///         ⚠ <b>That is two consumers and one matrix, not two copies of the arithmetic.</b> The matrix
///         is composed here, once per element per pass, origin already folded in — see
///         <see cref="UiTransform" /> — and the two consumers apply it and its inverse to a point.
///         There is no second place that knows what <c>rotate</c> means.
///     </para>
///     <para>
///         ⚠ <b>Still not layout.</b> Read in the accumulation pass for
///         <see cref="TranslationReader" />'s reason and with a stronger consequence: a scaled element
///         keeps the space layout gave it, so a <c>scale-150</c> button overflows its row rather than
///         widening it. CSS Transforms 1 §3 requires exactly that, and it is also the only reading
///         that avoids re-shaping glyphs — which is what the refusal this replaced was protecting.
///     </para>
/// </remarks>
sealed class TransformReader {
    readonly int rotate;
    readonly int scale;
    readonly int list;
    readonly int origin;

    /// <summary>The <c>perspective</c> property, which an element establishes for its CHILDREN.</summary>
    /// <remarks>
    ///     ⚠ <b>The classic mistake is to read it on the element it is written on, and the picture it
    ///     produces is plausible.</b> Transforms 2 § 6: an element's <c>perspective</c> applies to its
    ///     children, and the <c>perspective()</c> <i>function</i> inside its own <c>transform</c>
    ///     applies to itself. Getting them the same way round makes a card flip under a parent's
    ///     perspective look like a card flip under its own, which is a weaker projection and not an
    ///     obviously wrong one.
    /// </remarks>
    readonly int perspective;

    /// <summary>The vanishing point <see cref="perspective" /> is taken about, in the parent's box.</summary>
    readonly int perspectiveOrigin;

    readonly int none;
    readonly int left;
    readonly int centre;
    readonly int right;
    readonly int top;
    readonly int bottom;
    readonly NameTable values;
    readonly StyleValueParser parser;

    // ⚠ One list for the life of the document, cleared per element, because this runs once per
    // element per pass and a transformed element is usually a transformed element every frame — a
    // drag, a hover, an animation at rest. `TrackListProperty` in `LayoutStyleBuilder` keeps its
    // scratch for the same reason and states it the same way.
    // ⚠ <b>Four-dimensional, and that is the whole shape of #550 rather than nine more names.</b>
    // Reducing a 4×4 to a `UiTransform` keeps rows x, y, w against columns x, y, 1 and throws the z
    // row and the z column away, so `R(A·B) = R(A)·R(B)` holds only where `A` has no z column or `B`
    // no z row — and a `perspective()` is nothing BUT a z column while a `rotateX()` is nothing but a
    // z row, which is the one pair every card flip is written from. A list of `UiTransform`s folded
    // together here is the plausible arrangement and it makes every `perspective()` silently the
    // identity, because every point of an element sits at z = 0 until something has moved it. So the
    // list composes in four dimensions and `Reduce` runs once, at the end.
    readonly List<Matrix4x4> functions = [];

    /// <summary>Interns the four property names, the keyword that means "do not", and the origins.</summary>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table declaration values are interned in.</param>
    /// <param name="keywords">The table identifiers are interned in.</param>
    public TransformReader(NameTable properties, NameTable values, NameTable keywords) {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(keywords);

        rotate = properties.Intern("rotate");
        scale = properties.Intern("scale");
        list = properties.Intern("transform");
        origin = properties.Intern("transform-origin");
        perspective = properties.Intern("perspective");
        perspectiveOrigin = properties.Intern("perspective-origin");
        this.values = values;
        none = values.Intern("none");
        left = keywords.Intern("left");
        centre = keywords.Intern("center");
        right = keywords.Intern("right");
        top = keywords.Intern("top");
        bottom = keywords.Intern("bottom");
        parser = new StyleValueParser(values, keywords);
    }

    /// <summary>The affine an element's style places it under, or null where there is none.</summary>
    /// <param name="element">The element, whose border box the origin and any percentage resolve against.</param>
    /// <param name="metrics">The lengths <c>em</c>, <c>rem</c> and the viewport units resolve against.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Null rather than the identity, and the two misses are checked before anything is
    ///         read.</b> This runs once per element per pass and almost no element carries either
    ///         property, so the pair of <see cref="ComputedStyle.TryGet" /> failures is the whole cost
    ///         for the overwhelming majority of a document —
    ///         <see cref="TranslationReader.Of" />'s argument, doubled because there are two
    ///         properties. Returning an identity instead would be correct and would cost every element
    ///         in the tree a matrix nobody looks at.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Scale first and rotate second, per Transforms 2 §3, which orders the three
    ///         independent properties <c>translate</c>, then <c>rotate</c>, then <c>scale</c> as
    ///         <i>matrix</i> multiplications — so the scale is the innermost and applies to the point
    ///         first.</b> The two commute only when the scale is uniform, which is exactly the case a
    ///         test written casually would use, so the order is asserted in
    ///         <c>Vixen.Ui.Tests.TransformTests</c> against a non-uniform one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An identity composition returns null too.</b> <c>rotate: 0deg</c> and
    ///         <c>scale: 1</c> are the initial values and are written constantly — every
    ///         <c>rotate-0</c>, every animation at rest. Each one that reached
    ///         <c>DrawListBuilder</c> as a transform would open a group and spend a viewport-sized
    ///         surface and a render pass on the identical picture. See <see cref="UiTransform.IsIdentity" />.
    ///     </para>
    /// </remarks>
    public UiTransform? Of(UiElement element, LengthContext metrics) {
        ArgumentNullException.ThrowIfNull(element);

        var hasRotation = element.Style.TryGet(rotate, out var rotation) && rotation != none;
        var hasScale = element.Style.TryGet(scale, out var scaling) && scaling != none;
        var hasList = element.Style.TryGet(list, out var written) && written != none;

        if (!hasRotation && !hasScale && !hasList) {
            return null;
        }

        var about = Origin(element, metrics);
        var composed = UiTransform.Identity;

        // ⚠ <b>The spatial path carries a 4×4 all the way to the end, because the parent's
        // perspective is the LAST factor and the reduction does not commute with it.</b> `rotate` and
        // `scale` are the element's own properties and Transforms 2 §3 puts them inside its
        // `transform`; §6 then projects the whole of that through the parent's vanishing point. So a
        // perspective multiplied in beside the list — before these two — reads as a rotation seen
        // through the parent's eye and THEN scaled, which is a different picture whenever the
        // transform origin and the vanishing point differ. It is exactly right when they coincide,
        // which is why a two-element fixture written the natural way cannot see it.
        var spatial = false;
        var composition = Matrix4x4.Identity;

        if (hasList) {
            // ⚠ <b>The list is innermost, and that is the specification's order rather than the
            // written one.</b> Transforms 2 §3 builds the matrix as translate, then rotate, then
            // scale, then <c>transform</c> — as matrix multiplications, so <c>transform</c> is the
            // last factor and applies to a point <i>first</i>. Reading the four in the order they are
            // listed gives the transpose of the right answer on any element that sets more than one,
            // which is a picture that is right for a uniform scale and wrong for everything else.
            // ⚠ <b>A refused list drops itself and leaves the other two standing</b>, which is CSS's
            // rule for an invalid declaration rather than a convenience: `rotate` and `scale` are
            // separate properties and are not made invalid by their neighbour. Returning nothing at
            // all here would let one `perspective()` somebody pasted in cancel a rotation two lines
            // above it.
            if (Functions(values.NameOf(written), element, metrics, out var read, out var isSpatial)) {
                // ⚠ <b>Two branches over one specification, and the flat one is here so that nothing
                // affine moves by a bit.</b> `UiTransform.About` is a closed form; folding the origin
                // in four dimensions is two matrix products, and the two differ in the last bit — on
                // a picture every committed screenshot in `Vixen.Ui.Controls.Tests` was rendered
                // against. A list with no z in it cannot tell a 4×4 composition from the old one,
                // which is why `spatial` selects rather than a reader deciding.
                spatial = isSpatial;

                if (isSpatial) {
                    composition = Fold(read, about);
                } else {
                    composed = Reduce(read).About(about);
                }
            }
        }

        if (hasScale) {
            Scaling(parser.Parse(scaling), out var x, out var y);
            var step = UiTransform.Scale(x, y, about);

            if (spatial) {
                composition = Matrix4x4.Multiply(composition, Lift(step));
            } else {
                composed = composed.Then(step);
            }
        }

        if (hasRotation) {
            var step = UiTransform.Rotation(Degrees(parser.Parse(rotation)), about);

            if (spatial) {
                composition = Matrix4x4.Multiply(composition, Lift(step));
            } else {
                composed = composed.Then(step);
            }
        }

        if (spatial) {
            composed = Reduce(Matrix4x4.Multiply(composition, Established(element, metrics)));
        }

        return composed.IsIdentity ? null : composed;
    }

    /// <summary>Reads a <c>&lt;transform-list&gt;</c> into one matrix, in the element's own space.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Composed right to left.</b> <c>transform: rotate(45deg) translate(20px)</c> is the
    ///         matrix product <c>R · T</c>, so the <i>last</i> function is applied to a point first —
    ///         the element is moved twenty points along its own rotated x axis rather than twenty
    ///         points across the screen and then spun. The two differ by exactly the rotation, which
    ///         is invisible whenever only one function is written and is the first thing a two-function
    ///         declaration shows.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The whole list is refused if any one function is</b>, and refused means <i>no
    ///         transform</i> rather than a partial one. The three-dimensional functions are the reason
    ///         this matters: <c>rotateX</c>, <c>translate3d</c> and <c>perspective</c> are legal CSS
    ///         and there is no third axis here, so reading the ones that happen to be flat and
    ///         dropping the rest turns a card flip into a card that never moves — which is a picture,
    ///         and a wrong one. Nothing is the honest answer, and it is also what CSS does with a
    ///         declaration it cannot parse.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Percentages are of the element's own border box</b>, per Transforms 1 §8 — x
    ///         against its width, y against its height. That is the same rule <c>translate</c> the
    ///         property follows and the opposite of every percentage in the box model, which resolve
    ///         against the <i>containing block</i>. See <see cref="TranslationReader" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And when the three-dimensional functions do arrive, this loop is the wrong shape
    ///         for them — the reduction to a homography does not commute with the composition.</b>
    ///         <see cref="UiTransform" /> is a 3×3 now, so <see cref="Function" /> returning one per
    ///         function and folding them together here <i>looks</i> like all that is left to do. It is
    ///         not. Reducing a 4×4 to this type keeps rows <c>x</c>, <c>y</c>, <c>w</c> against columns
    ///         <c>x</c>, <c>y</c>, <c>1</c> and throws the <c>z</c> row and the <c>z</c> column away;
    ///         a product's cell sums over <c>k ∈ {x, y, z, 1}</c>, so <c>R(A·B) = R(A)·R(B)</c> only
    ///         where <c>A</c> has no <c>z</c> column or <c>B</c> no <c>z</c> row. A perspective is
    ///         nothing <i>but</i> a <c>z</c> column and a <c>rotateX</c> is nothing but a <c>z</c> row,
    ///         which is the one pair every card flip is written from.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the wrong answer is the plausible one</b>, which is why this is written down
    ///         rather than left to be discovered. <c>perspective(200px) rotateX(60deg)</c> sends a
    ///         point 100 points below the origin to <c>y = 88.19</c> composed in 4×4 and reduced once;
    ///         reduced per function and composed here it lands at <c>y = 50</c> with <c>w = 1</c> —
    ///         exactly <c>rotateX</c> on its own, because <c>R(perspective)</c> <i>is</i> the identity:
    ///         every point of an element sits at <c>z = 0</c> until something has moved it. So the
    ///         perspective silently does nothing, the card flip is a vertical squash, and the picture
    ///         is the one this reader already draws by refusing the list. The list has to compose in
    ///         four dimensions and reduce at the end. #550.
    ///     </para>
    /// </remarks>
    bool Functions(
        string text,
        UiElement element,
        LengthContext metrics,
        out Matrix4x4 result,
        out bool spatial
    ) {
        result = Matrix4x4.Identity;
        spatial = false;
        functions.Clear();

        var span = text.AsSpan().Trim();

        if (span.IsEmpty || span.Equals("none", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var at = 0;

        while (at < span.Length) {
            if (char.IsWhiteSpace(span[at]) || span[at] == ',') {
                at++;
                continue;
            }

            var open = span[at..].IndexOf('(');

            if (open <= 0) {
                return false;
            }

            var name = span.Slice(at, open).Trim();
            at += open + 1;

            var close = span[at..].IndexOf(')');

            if (close < 0) {
                return false;
            }

            var arguments = span.Slice(at, close);
            at += close + 1;

            // ⚠ A nested parenthesis is `calc()`, `min()` or `var()`, none of which this reads. Caught
            // by looking for one inside the arguments rather than by matching depth, because the
            // answer either way is a refusal and a depth counter would only reach it later.
            if (arguments.Contains('(')) {
                return false;
            }

            if (!Function(name, arguments, element, metrics, out var matrix, out var third)) {
                return false;
            }

            spatial |= third;
            functions.Add(matrix);
        }

        if (functions.Count == 0) {
            return false;
        }

        // ⚠ <b>Right to left, which is the same order the reduced version had and for the same
        // reason.</b> `transform: A B` is the product `A · B`, so the LAST function is applied to a
        // point first — `rotate(90deg) translate(40px)` moves the element along its own turned axis
        // rather than across the screen. `Matrix4x4.Multiply` composes "apply the left one, then the
        // right one", exactly as `UiTransform.Then` does, so the loop is unchanged.
        for (var index = functions.Count - 1; index >= 0; index--) {
            result = Matrix4x4.Multiply(result, functions[index]);
        }

        return true;
    }

    /// <summary>The 4×4 a planar element's homography is the reduction of.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Rows x, y, w against columns x, y, 1 — the z row and the z column go, and that is
    ///         exactly what makes the reduction safe only at the end.</b> An element is a plane at
    ///         z = 0, so no point ever reaching this carries a z; what the discarded row and column
    ///         held was how the composition <i>moved</i> a point out of that plane and how the
    ///         perspective turned that movement back into a <c>w</c>, and both are already folded
    ///         into the nine cells kept here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Row-vector, both sides.</b> <see cref="Matrix4x4" /> is ADR-003's row-vector
    ///         convention with the translation in <c>M41..M43</c>, and <see cref="UiTransform" /> is
    ///         the same shape one dimension down — see <see cref="UiTransform.Project" />, which reads
    ///         <c>Dx</c> and <c>Dy</c> as the third row. So the translation comes out of row four and
    ///         the projective column out of column four, and a reader who expects CSS's
    ///         column-vector <c>m34</c> finds it at <c>M34</c> here for that reason rather than by
    ///         accident.
    ///     </para>
    /// </remarks>
    static UiTransform Reduce(in Matrix4x4 matrix) =>
        new(matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M41, matrix.M42) {
            M13 = matrix.M14,
            M23 = matrix.M24,
            M33 = matrix.M44
        };

    /// <summary>A translation, as the 4×4 the two folds below are written from.</summary>
    static Matrix4x4 Translation(float x, float y, float z) =>
        new(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            x, y, z, 1f
        );

    /// <summary>The list performed about <paramref name="origin" /> rather than about zero.</summary>
    /// <remarks>
    ///     ⚠ <b>One origin for the whole list, composed first and re-centred once</b> — the argument
    ///     <see cref="UiTransform.About(Vector2)" /> makes, in four dimensions because a <c>perspective()</c>
    ///     among the functions makes the closed form wrong. <c>transform-origin</c>'s z is not read:
    ///     the third component is a length CSS allows and nothing here can observe, because an
    ///     element is a plane at z = 0 and moving the origin along z moves the plane with it.
    /// </remarks>
    static Matrix4x4 Fold(in Matrix4x4 matrix, Vector2 origin) =>
        Matrix4x4.Multiply(
            Matrix4x4.Multiply(Translation(-origin.X, -origin.Y, 0f), matrix),
            Translation(origin.X, origin.Y, 0f)
        );

    /// <summary>The perspective this element's PARENT establishes for it, or the identity.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The parent's, and this is the half of Transforms 2 § 6 that is easy to get
    ///         backwards.</b> <c>perspective</c> on an element applies to its children; the
    ///         <c>perspective()</c> function inside its own <c>transform</c> applies to itself. Both
    ///         produce a plausible picture and the wrong one is weaker rather than broken, so it is
    ///         asserted with two elements rather than one in <c>TransformTests</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Outermost, after the element's own list and its <c>rotate</c>/<c>scale</c>.</b> A
    ///         child is placed by its own transform and <i>then</i> seen through the parent's
    ///         vanishing point, which is why this multiplies on the right of everything. Reached only
    ///         from the spatial branch, because on a list with no z the whole matrix reduces to the
    ///         identity — every point of a flat element is at z = 0, where <c>w = 1 − z/d</c> is one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not inherited, and not accumulated down the tree.</b> A grandparent's
    ///         <c>perspective</c> does not reach a grandchild in CSS either: what a shared 3D space
    ///         would need is <c>transform-style: preserve-3d</c>, which is a rendering model with its
    ///         own sorting rather than a matrix change and is refused (#550).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And measured in the PARENT's font, because the declaration is the parent's.</b>
    ///         <c>perspective</c> and <c>perspective-origin</c> are the only two properties this
    ///         reader takes off an element other than the one it was called for, so they are the only
    ///         two whose <c>em</c> — and whose <c>perspective-origin: 50%</c>, against the parent's
    ///         box — belongs to a different element. A stage at <c>font-size: 32px</c> declaring
    ///         <c>perspective: 10em</c> means 320 points however small the card inside it is, and
    ///         resolving it in the caller's context gives a plausible number rather than an error.
    ///         ⚠ The caller's context is <i>not</i> the child's own, either: <c>UiDocument.Accumulate</c>
    ///         threads one surface-wide <see cref="LengthContext" /> through the whole tree, so every
    ///         <c>em</c> in every <c>transform</c> resolves against the ROOT font size. That is a
    ///         wider gap than this one and is not fixed here; what is fixed is the one declaration
    ///         whose owner is known to differ from the element being measured.
    ///     </para>
    /// </remarks>
    Matrix4x4 Established(UiElement element, LengthContext metrics) {
        if (element.Parent is not { } parent) {
            return Matrix4x4.Identity;
        }

        if (!parent.Style.TryGet(perspective, out var declared) || declared == none) {
            return Matrix4x4.Identity;
        }

        var context = metrics.WithFontSize(parent.FontSize).WithLineHeight(parent.LineHeight);
        var length = context.ToLength(parser.Parse(declared));

        // ⚠ A non-positive distance is not a flat element, it is an invalid declaration: CSS
        // Transforms 2 § 6 requires a positive length, and a zero would put every point of the plane
        // on the eye plane at once. Dropped, leaving whatever the child's own list says standing.
        if (length.Unit != LayoutUnit.Point || !(length.Value > 0f)) {
            return Matrix4x4.Identity;
        }

        var vanishing = Origin(parent, context, perspectiveOrigin);

        var projection = Projection(length.Value);

        return Matrix4x4.Multiply(
            Matrix4x4.Multiply(Translation(-vanishing.X, -vanishing.Y, 0f), projection),
            Translation(vanishing.X, vanishing.Y, 0f)
        );
    }

    /// <summary>One <c>&lt;transform-function&gt;</c>, in the element's own space.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>skew</c>'s two angles are crossed, and the crossing is the whole of it.</b>
    ///         <c>skewX(a)</c> shifts a point's <i>x</i> by its <i>y</i>, which is the <c>M21</c> cell
    ///         — the y contribution to x — so the first argument writes the second row and the second
    ///         argument writes the first. Written the obvious way round, <c>skewX</c> slants the box
    ///         the other way and only a test with a non-zero y can tell.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every rotation here is the transpose of the matrix CSS prints, because CSS is
    ///         column-vector and this is row-vector</b> — ADR-003, and the same relationship
    ///         <see cref="Reduce" /> describes for the translation. <c>rotate</c> and <c>rotateZ</c>
    ///         come out clockwise on screen from the <i>standard</i> counter-clockwise matrix,
    ///         because y grows downwards; see <see cref="UiTransform.Rotation" />, which says so at
    ///         length and which this has to agree with to the bit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><paramref name="spatial" /> is what selects the four-dimensional path, and it is
    ///         set by the function rather than inferred from the matrix.</b> <c>rotateX(0deg)</c>,
    ///         <c>translateZ(0)</c> and <c>scaleZ(1)</c> are the identity in every cell, so a test on
    ///         the numbers would send them down the flat branch — which is the right picture and the
    ///         wrong rounding, since the two branches fold the origin differently. Naming the
    ///         function makes the choice the author's.
    ///     </para>
    /// </remarks>
    static bool Function(
        ReadOnlySpan<char> name,
        ReadOnlySpan<char> arguments,
        UiElement element,
        LengthContext metrics,
        out Matrix4x4 result,
        out bool spatial
    ) {
        result = Matrix4x4.Identity;
        spatial = false;

        Span<Range> parts = stackalloc Range[17];
        var count = Split(arguments, parts);

        if (count <= 0) {
            return false;
        }

        if (Is(name, "matrix")) {
            Span<float> cells = stackalloc float[6];

            if (count != 6) {
                return false;
            }

            for (var index = 0; index < 6; index++) {
                if (!Number(arguments[parts[index]], out cells[index])) {
                    return false;
                }
            }

            result = Flat(cells[0], cells[1], cells[2], cells[3], cells[4], cells[5]);
            return true;
        }

        // ⚠ <b>Sixteen numbers in CSS's column-major listing, which IS this type's row-major
        // order.</b> `matrix3d(a1 a2 a3 a4, b1 …)` lists four columns of a column-vector matrix, and
        // the transpose of a column-major listing is a row-major one — so the values go straight in,
        // with no transpose anywhere. A reader who transposes "to be safe" gets a matrix that is
        // right for every symmetric argument and wrong for every real one.
        if (Is(name, "matrix3d")) {
            Span<float> cells = stackalloc float[16];

            if (count != 16) {
                return false;
            }

            for (var index = 0; index < 16; index++) {
                if (!Number(arguments[parts[index]], out cells[index])) {
                    return false;
                }
            }

            result = new Matrix4x4(
                cells[0], cells[1], cells[2], cells[3],
                cells[4], cells[5], cells[6], cells[7],
                cells[8], cells[9], cells[10], cells[11],
                cells[12], cells[13], cells[14], cells[15]
            );

            spatial = true;
            return true;
        }

        if (Is(name, "translate") || Is(name, "translateX") || Is(name, "translateY")) {
            var horizontal = !Is(name, "translateY");
            var vertical = Is(name, "translateY");

            if (count > (Is(name, "translate") ? 2 : 1)) {
                return false;
            }

            if (!Distance(arguments[parts[0]], element, metrics, vertical, out var first)) {
                return false;
            }

            var x = horizontal ? first : 0f;
            var y = vertical ? first : 0f;

            if (count == 2) {
                if (!Distance(arguments[parts[1]], element, metrics, vertical: true, out y)) {
                    return false;
                }
            }

            result = Flat(1f, 0f, 0f, 1f, x, y);
            return true;
        }

        // ⚠ <b>A z translation takes no percentage, and that is a rule rather than an omission.</b>
        // Transforms 2 § 12: a percentage in `translate3d`'s third argument, or in `translateZ`, makes
        // the whole function invalid — there is no box dimension along z for it to resolve against.
        // `Distance` would happily resolve one against the element's height, which is a plausible
        // number and the wrong one, so the z argument goes through `Depth` instead.
        if (Is(name, "translateZ") || Is(name, "translate3d")) {
            var wanted = Is(name, "translateZ") ? 1 : 3;

            if (count != wanted) {
                return false;
            }

            var x = 0f;
            var y = 0f;

            if (wanted == 3) {
                if (!Distance(arguments[parts[0]], element, metrics, vertical: false, out x)) {
                    return false;
                }

                if (!Distance(arguments[parts[1]], element, metrics, vertical: true, out y)) {
                    return false;
                }
            }

            if (!Depth(arguments[parts[wanted - 1]], metrics, out var z)) {
                return false;
            }

            result = Translation(x, y, z);
            spatial = true;
            return true;
        }

        if (Is(name, "scale") || Is(name, "scaleX") || Is(name, "scaleY")) {
            if (count > (Is(name, "scale") ? 2 : 1)) {
                return false;
            }

            if (!Number(arguments[parts[0]], out var first)) {
                return false;
            }

            // ⚠ A one-argument `scale()` is uniform, and the two axis forms leave the other axis at
            // one. Same asymmetry `Scaling` documents for the `scale` property, for the same reason:
            // the identity for a scale is one rather than zero.
            var x = Is(name, "scaleY") ? 1f : first;
            var y = Is(name, "scaleX") ? 1f : first;

            if (count == 2 && !Number(arguments[parts[1]], out y)) {
                return false;
            }

            result = Flat(x, 0f, 0f, y, 0f, 0f);
            return true;
        }

        if (Is(name, "scaleZ") || Is(name, "scale3d")) {
            var wanted = Is(name, "scaleZ") ? 1 : 3;

            if (count != wanted) {
                return false;
            }

            var x = 1f;
            var y = 1f;

            if (wanted == 3) {
                if (!Number(arguments[parts[0]], out x) || !Number(arguments[parts[1]], out y)) {
                    return false;
                }
            }

            if (!Number(arguments[parts[wanted - 1]], out var z)) {
                return false;
            }

            result = new Matrix4x4(
                x, 0f, 0f, 0f,
                0f, y, 0f, 0f,
                0f, 0f, z, 0f,
                0f, 0f, 0f, 1f
            );
            spatial = true;
            return true;
        }

        if (Is(name, "rotate") || Is(name, "rotateZ")) {
            if (count != 1 || !Angle(arguments[parts[0]], out var degrees)) {
                return false;
            }

            var (cos, sin) = Turn(degrees);

            result = Flat(cos, sin, -sin, cos, 0f, 0f);
            return true;
        }

        // ⚠ <b>A positive `rotateX` tips the element's TOP away from the viewer</b>, which is what a
        // right-handed rotation about x comes to on a screen whose y points down — the same
        // reconciliation `rotate` makes and the reason neither is written with a negation.
        if (Is(name, "rotateX") || Is(name, "rotateY")) {
            if (count != 1 || !Angle(arguments[parts[0]], out var degrees)) {
                return false;
            }

            var (cos, sin) = Turn(degrees);

            result = Is(name, "rotateX")
                ? new Matrix4x4(
                    1f, 0f, 0f, 0f,
                    0f, cos, sin, 0f,
                    0f, -sin, cos, 0f,
                    0f, 0f, 0f, 1f
                )
                : new Matrix4x4(
                    cos, 0f, -sin, 0f,
                    0f, 1f, 0f, 0f,
                    sin, 0f, cos, 0f,
                    0f, 0f, 0f, 1f
                );

            spatial = true;
            return true;
        }

        // ⚠ <b>A zero-length axis is an invalid function and not a no-op.</b> Transforms 2 § 12 says
        // so outright, and reading it as the identity would make `rotate3d(0, 0, 0, 45deg)` silently
        // do nothing where CSS drops the whole list.
        if (Is(name, "rotate3d")) {
            if (count != 4 || !Angle(arguments[parts[3]], out var degrees)) {
                return false;
            }

            Span<float> axis = stackalloc float[3];

            for (var index = 0; index < 3; index++) {
                if (!Number(arguments[parts[index]], out axis[index])) {
                    return false;
                }
            }

            var length = MathF.Sqrt((axis[0] * axis[0]) + (axis[1] * axis[1]) + (axis[2] * axis[2]));

            if (length < 1e-9f) {
                return false;
            }

            var (x, y, z) = (axis[0] / length, axis[1] / length, axis[2] / length);
            var (cos, sin) = Turn(degrees);
            var t = 1f - cos;

            // Rodrigues, transposed into this type's row-vector layout: the off-diagonal sines carry
            // the opposite sign to the column-vector matrix CSS prints.
            result = new Matrix4x4(
                (t * x * x) + cos, (t * x * y) + (sin * z), (t * x * z) - (sin * y), 0f,
                (t * x * y) - (sin * z), (t * y * y) + cos, (t * y * z) + (sin * x), 0f,
                (t * x * z) + (sin * y), (t * y * z) - (sin * x), (t * z * z) + cos, 0f,
                0f, 0f, 0f, 1f
            );

            spatial = true;
            return true;
        }

        // ⚠ <b>The z column, and the only function in this list that is one.</b> On its own it is the
        // identity on every point of the element — `w = 1 − z/d` and every point is at z = 0 — so a
        // `perspective()` is observable only through a neighbour in the same list that moves a point
        // off the plane. That is exactly why this reader composes in four dimensions: reduced per
        // function, this cell would be thrown away before the `rotateX` beside it could use it.
        if (Is(name, "perspective")) {
            if (count != 1) {
                return false;
            }

            // `none` is the initial value of the PROPERTY and is not a value the function takes.
            if (!Depth(arguments[parts[0]], metrics, out var distance) || !(distance > 0f)) {
                return false;
            }

            result = Projection(distance);
            spatial = true;
            return true;
        }

        if (Is(name, "skew") || Is(name, "skewX") || Is(name, "skewY")) {
            if (count > (Is(name, "skew") ? 2 : 1)) {
                return false;
            }

            if (!Angle(arguments[parts[0]], out var first)) {
                return false;
            }

            var second = 0f;

            if (count == 2 && !Angle(arguments[parts[1]], out second)) {
                return false;
            }

            var horizontal = Is(name, "skewY") ? 0f : first;
            var vertical = Is(name, "skewY") ? first : second;

            result = Flat(1f, Tangent(vertical), Tangent(horizontal), 1f, 0f, 0f);
            return true;
        }

        return false;
    }

    /// <summary>The z column a <c>perspective</c> is, at a distance in points.</summary>
    /// <remarks>
    ///     ⚠ <c>M34</c> and not <c>M43</c>: row-vector, so the z contribution to <c>w</c> is row
    ///     three, column four. CSS names the same cell <c>m34</c> in its column-major listing, which
    ///     is the same number reached from the other side — see <see cref="Reduce" />. One function
    ///     and one property share it, because they are the same matrix about different origins.
    /// </remarks>
    static Matrix4x4 Projection(float distance) =>
        new(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, -1f / distance,
            0f, 0f, 0f, 1f
        );

    /// <summary>A 2D affine as the 4×4 the list composes in.</summary>
    /// <remarks>
    ///     ⚠ Its z row and z column are the identity's, which is what makes <c>R(A·B) = R(A)·R(B)</c>
    ///     hold across any run of these — and is why a list of nothing but these reduces to exactly
    ///     the floats the reduced composition used to produce. See <see cref="Reduce" />.
    /// </remarks>
    static Matrix4x4 Flat(float m11, float m12, float m21, float m22, float dx, float dy) =>
        new(
            m11, m12, 0f, 0f,
            m21, m22, 0f, 0f,
            0f, 0f, 1f, 0f,
            dx, dy, 0f, 1f
        );

    /// <summary>A 2D affine step back up into the four dimensions the spatial path composes in.</summary>
    /// <remarks>
    ///     ⚠ <b>Only ever handed a <see cref="UiTransform.Scale(float, float, Vector2)" /> or a
    ///     <see cref="UiTransform.Rotation" />, both of which are affine by construction</b> — their
    ///     <c>M13</c>, <c>M23</c> are zero and their <c>M33</c> is one, so there is no projective part
    ///     for <see cref="Flat" />'s zeroed z row and column to lose. Lifting the step rather than
    ///     re-deriving the matrix here is what keeps the spatial path's cells bit-identical to the
    ///     flat path's for the same declaration; a hand-written sine beside <c>UiTransform.Rotation</c>
    ///     is two spellings of one rotation that agree until one of them is tuned.
    /// </remarks>
    static Matrix4x4 Lift(in UiTransform transform) =>
        Flat(transform.M11, transform.M12, transform.M21, transform.M22, transform.Dx, transform.Dy);

    /// <summary>A cosine and a sine of an angle in degrees.</summary>
    static (float Cos, float Sin) Turn(float degrees) {
        var radians = degrees * (MathF.PI / 180f);

        return (MathF.Cos(radians), MathF.Sin(radians));
    }

    /// <summary>The unit a transform component's suffix names, or <see cref="StyleUnit.None" />.</summary>
    /// <param name="suffix">Whatever followed the number.</param>
    /// <returns>The unit, or <see cref="StyleUnit.None" /> for one a transform may not take.</returns>
    /// <remarks>
    ///     ⚠ <b>One table, because there were two identical ones — <c>Depth</c>'s and
    ///     <c>Distance</c>'s — and <see cref="StyleValue" />'s own suffix table carries the remark
    ///     about what that costs.</b> They were still in step when the container units arrived, so
    ///     nothing had gone wrong yet; the point of folding them is that the next unit is added once.
    ///     <para>
    ///         ⚠ <b><c>lh</c> is deliberately still absent, and its absence is now visible rather
    ///         than duplicated.</b> A transform resolves against a box and not against a line, and
    ///         nothing in this repository writes <c>translate: 1lh</c> — but the unit parses
    ///         everywhere else, so this is a gap to decide about rather than one to close in passing.
    ///     </para>
    /// </remarks>
    static StyleUnit UnitOf(ReadOnlySpan<char> suffix) => suffix switch {
        var u when u.Equals("px", StringComparison.OrdinalIgnoreCase) => StyleUnit.Pixels,
        var u when u.Equals("em", StringComparison.OrdinalIgnoreCase) => StyleUnit.Em,
        var u when u.Equals("rem", StringComparison.OrdinalIgnoreCase) => StyleUnit.Rem,
        var u when u.Equals("vw", StringComparison.OrdinalIgnoreCase) => StyleUnit.ViewportWidth,
        var u when u.Equals("vh", StringComparison.OrdinalIgnoreCase) => StyleUnit.ViewportHeight,
        var u when u.Equals("vmin", StringComparison.OrdinalIgnoreCase) => StyleUnit.ViewportMin,
        var u when u.Equals("vmax", StringComparison.OrdinalIgnoreCase) => StyleUnit.ViewportMax,
        var u when u.Equals("cqw", StringComparison.OrdinalIgnoreCase) => StyleUnit.ContainerWidth,
        var u when u.Equals("cqh", StringComparison.OrdinalIgnoreCase) => StyleUnit.ContainerHeight,
        var u when u.Equals("cqi", StringComparison.OrdinalIgnoreCase) => StyleUnit.ContainerInline,
        var u when u.Equals("cqb", StringComparison.OrdinalIgnoreCase) => StyleUnit.ContainerBlock,
        var u when u.Equals("cqmin", StringComparison.OrdinalIgnoreCase) => StyleUnit.ContainerMin,
        var u when u.Equals("cqmax", StringComparison.OrdinalIgnoreCase) => StyleUnit.ContainerMax,
        _ => StyleUnit.None
    };

    /// <summary>A length along z, which takes no percentage.</summary>
    /// <remarks>
    ///     ⚠ <b>Transforms 2 § 12 makes a percentage here invalid rather than zero</b>, because there
    ///     is no box dimension along z to resolve one against — and resolving it against the height,
    ///     which is what the two-dimensional reader would do, produces a plausible number that is not
    ///     the one CSS asks for. The element's box is passed nothing, which is what makes that
    ///     impossible rather than merely avoided.
    /// </remarks>
    static bool Depth(ReadOnlySpan<char> text, LengthContext metrics, out float points) {
        points = 0f;

        if (text.EndsWith("%", StringComparison.Ordinal)) {
            return false;
        }

        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare)) {
            return bare == 0f;
        }

        var digits = 0;

        while (digits < text.Length && (char.IsAsciiDigit(text[digits]) || text[digits] is '.' or '-' or '+' or 'e' or 'E')) {
            digits++;
        }

        if (!float.TryParse(text[..digits], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
            return false;
        }

        var unit = UnitOf(text[digits..]);

        if (unit == StyleUnit.None) {
            return false;
        }

        var length = metrics.ToLength(StyleValue.FromLength(number, unit));

        if (length.Unit != LayoutUnit.Point) {
            return false;
        }

        points = length.Value;
        return true;
    }

    static float Tangent(float degrees) => MathF.Tan(degrees * (MathF.PI / 180f));

    static bool Is(ReadOnlySpan<char> name, string expected) =>
        name.Equals(expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Cuts an argument list on its commas, or on whitespace where it has none.</summary>
    /// <remarks>
    ///     ⚠ Returns −1 for more arguments than the caller has room for, so that <c>matrix(…)</c> with
    ///     seven cells is refused rather than silently read as six.
    /// </remarks>
    static int Split(ReadOnlySpan<char> arguments, Span<Range> parts) {
        var count = 0;
        var at = 0;

        while (at < arguments.Length) {
            if (char.IsWhiteSpace(arguments[at]) || arguments[at] == ',') {
                at++;
                continue;
            }

            var start = at;

            while (at < arguments.Length && !char.IsWhiteSpace(arguments[at]) && arguments[at] != ',') {
                at++;
            }

            if (count == parts.Length) {
                return -1;
            }

            parts[count++] = new Range(start, at);
        }

        return count;
    }

    /// <summary>A bare number, or a percentage read as a fraction.</summary>
    static bool Number(ReadOnlySpan<char> text, out float value) {
        if (text.EndsWith("%", StringComparison.Ordinal)) {
            if (!float.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out value)) {
                return false;
            }

            value /= 100f;
            return true;
        }

        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>An angle, in degrees, in any of the four units CSS spells one with.</summary>
    static bool Angle(ReadOnlySpan<char> text, out float degrees) {
        degrees = 0f;

        var (suffix, per) = Suffix(text);

        if (float.IsNaN(per) || suffix >= text.Length) {
            return false;
        }

        if (!float.TryParse(text[..^suffix], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
            return false;
        }

        degrees = number * per;
        return true;

        static (int Length, float Degrees) Suffix(ReadOnlySpan<char> value) {
            if (value.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) {
                return (3, 1f);
            }

            if (value.EndsWith("grad", StringComparison.OrdinalIgnoreCase)) {
                return (4, 0.9f);
            }

            if (value.EndsWith("turn", StringComparison.OrdinalIgnoreCase)) {
                return (4, 360f);
            }

            if (value.EndsWith("rad", StringComparison.OrdinalIgnoreCase)) {
                return (3, 180f / MathF.PI);
            }

            // ⚠ A unitless angle is not zero degrees, it is invalid — CSS admits a bare `0` for a
            // length and not for an angle. Reading it as zero would make `rotate(45)` a typo that
            // silently does nothing rather than one the whole declaration is dropped for.
            return (0, float.NaN);
        }
    }

    /// <summary>A length or a percentage of the element's own border box, in points.</summary>
    static bool Distance(
        ReadOnlySpan<char> text,
        UiElement element,
        LengthContext metrics,
        bool vertical,
        out float points
    ) {
        points = 0f;

        if (text.EndsWith("%", StringComparison.Ordinal)) {
            if (!float.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) {
                return false;
            }

            points = percent / 100f * (vertical ? element.Height : element.Width);
            return true;
        }

        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare)) {
            // A unitless length is only legal as zero, and anything else is a declaration to drop.
            points = 0f;
            return bare == 0f;
        }

        var digits = 0;

        while (digits < text.Length && (char.IsAsciiDigit(text[digits]) || text[digits] is '.' or '-' or '+' or 'e' or 'E')) {
            digits++;
        }

        if (!float.TryParse(text[..digits], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
            return false;
        }

        var unit = UnitOf(text[digits..]);

        if (unit == StyleUnit.None) {
            return false;
        }

        var length = metrics.ToLength(StyleValue.FromLength(number, unit));

        if (length.Unit != LayoutUnit.Point) {
            return false;
        }

        points = length.Value;
        return true;
    }

    /// <summary>The point a transform turns about, in absolute document coordinates.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The border box's centre by default, which is the initial value and is <i>not</i>
    ///         the box's origin.</b> Transforms 1 §6 makes <c>transform-origin</c>
    ///         <c>50% 50%</c>, so <c>rotate-45</c> on a button spins it in place. Defaulting to the top
    ///         left instead would swing every rotated element down and to the right by a distance that
    ///         depends on its size, which reads as a layout bug rather than as a wrong origin.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Absolute rather than relative to the element, because the matrix it goes into is
    ///         absolute.</b> Both consumers work in document space — the geometry builder emits vertices
    ///         there and the hit test receives the pointer there — so the origin is added in here, once,
    ///         rather than at each of them.
    ///     </para>
    ///     <para>
    ///         A percentage is of the element's own border box, like <c>translate</c>'s and unlike
    ///         every percentage in the box model. A length is from the box's top left. The five
    ///         keywords are the positions CSS names, and a component this engine cannot read falls back
    ///         to the centre rather than to zero — the difference between "the author wrote something
    ///         odd" and "the element jumps to its corner".
    ///     </para>
    /// </remarks>
    Vector2 Origin(UiElement element, LengthContext metrics) => Origin(element, metrics, origin);

    /// <summary>The same reading over a named property, so the two origins share one grammar.</summary>
    /// <remarks>
    ///     ⚠ <b><c>transform-origin</c> and <c>perspective-origin</c> take the same
    ///     <c>&lt;position&gt;</c> and default to the same place</b> — the centre of the box —
    ///     which is what makes a second copy of the keyword-by-axis rule below the wrong answer.
    ///     They differ only in whose box: a transform origin is the element's own, and a perspective
    ///     origin is the PARENT's, because it is the parent that establishes the vanishing point.
    ///     The z component <c>transform-origin</c> may carry is not read; see <see cref="Fold" />.
    /// </remarks>
    Vector2 Origin(UiElement element, LengthContext metrics, int property) {
        var x = element.Width / 2f;
        var y = element.Height / 2f;

        if (element.Style.TryGet(property, out var id)) {
            var value = parser.Parse(id);

            if (value.Kind == StyleValueKind.List) {
                var parts = value.Items;

                // ⚠ <b>Two keywords are assigned by <i>axis</i> and not by position, which is the one
                // place this grammar is not positional.</b> Transforms 1 §6 allows
                // <c>transform-origin: top right</c> and <c>right top</c> to mean the same point,
                // because <c>top</c> can only be a y and <c>right</c> can only be an x. Read
                // positionally, <c>top right</c> would ask <c>top</c> for an x and <c>right</c> for a
                // y, get neither, and fall back to the centre on both axes — so <c>origin-top-right</c>
                // and five of its eight siblings would silently be <c>origin-center</c>. A length is
                // always positional, and mixing the two is only legal in that order.
                if (parts.Length > 1 && Axial(parts[0]) && Axial(parts[1])) {
                    x = Keyword(parts[0], element.Width, horizontal: true, x);
                    x = Keyword(parts[1], element.Width, horizontal: true, x);
                    y = Keyword(parts[0], element.Height, horizontal: false, y);
                    y = Keyword(parts[1], element.Height, horizontal: false, y);
                } else {
                    if (parts.Length > 0) {
                        x = Component(parts[0], element.Width, horizontal: true, metrics, x);
                    }

                    if (parts.Length > 1) {
                        y = Component(parts[1], element.Height, horizontal: false, metrics, y);
                    }
                }
            } else {
                // ⚠ One component sets x and leaves y centred, per Transforms 1 §6 — except for the
                // two vertical keywords, which name a y and leave x centred. `Component` returns the
                // fallback unchanged for a keyword of the other axis, which is what makes
                // `transform-origin: top` land at the top *middle* rather than at the top left.
                x = Component(value, element.Width, horizontal: true, metrics, x);
                y = Component(value, element.Height, horizontal: false, metrics, y);
            }
        }

        return new Vector2(element.AbsoluteLeft + x, element.AbsoluteTop + y);
    }

    /// <summary>Whether a component is one of the five keywords an origin can be written with.</summary>
    bool Axial(StyleValue value) =>
        value.Kind == StyleValueKind.Keyword
        && (value.Keyword == left
            || value.Keyword == right
            || value.Keyword == top
            || value.Keyword == bottom
            || value.Keyword == centre);

    /// <summary>One component of an origin, in points from the box's top left.</summary>
    float Component(StyleValue value, float against, bool horizontal, LengthContext metrics, float fallback) {
        if (value.Kind == StyleValueKind.Keyword) {
            return Keyword(value, against, horizontal, fallback);
        }

        var length = metrics.ToLength(value);

        return length.Unit switch {
            LayoutUnit.Point => length.Value,
            LayoutUnit.Percent => length.Value / 100f * against,
            _ => fallback
        };
    }

    /// <summary>One keyword, on one axis, leaving the fallback alone where it names the other.</summary>
    /// <remarks>
    ///     ⚠ <c>center</c> answers on <i>both</i> axes, which is what makes it the value that can be
    ///     written beside any other — <c>center right</c> and <c>right center</c> are both the middle
    ///     of the right edge. The four directional keywords answer on one axis and return the fallback
    ///     on the other, which is what lets the caller ask each of a pair about each axis and take
    ///     whichever answered.
    /// </remarks>
    float Keyword(StyleValue value, float against, bool horizontal, float fallback) {
        var keyword = value.Keyword;

        if (keyword == centre) {
            return against / 2f;
        }

        if (horizontal) {
            return keyword == left ? 0f : keyword == right ? against : fallback;
        }

        return keyword == top ? 0f : keyword == bottom ? against : fallback;
    }

    /// <summary>An angle in degrees, or zero for anything unreadable.</summary>
    /// <remarks>
    ///     ⚠ <b>A list is refused whole rather than having its angle picked out of it.</b> Transforms 2
    ///     §3 also spells <c>rotate</c> as an axis and an angle — <c>rotate: x 45deg</c> — which is a
    ///     rotation out of the plane this engine has no depth for. Reading the angle and ignoring the
    ///     axis would turn every <c>rotate: x 45deg</c> into a <i>z</i> rotation of forty-five degrees,
    ///     which is not a degraded picture but a different one. Zero leaves the element alone, which
    ///     is what an engine with no third axis can honestly do.
    /// </remarks>
    static float Degrees(StyleValue value) =>
        value.Kind == StyleValueKind.Length && value.Unit == StyleUnit.Degrees ? value.Number : 0f;

    /// <summary>The two scale factors, defaulting to one.</summary>
    /// <remarks>
    ///     ⚠ <b>One component scales <i>both</i> axes, which is <c>translate</c>'s rule inverted and is
    ///     the spec's.</b> <c>translate: 8px</c> leaves y alone because the identity for a translation
    ///     is zero; <c>scale: 1.5</c> scales y too because the identity for a scale is one and a
    ///     one-component scale is defined as uniform. Copying the translation reader's shape here would
    ///     make <c>scale-150</c> a horizontal stretch.
    ///     <para>
    ///         Both a bare number and a percentage are accepted, because Transforms 2 §3 allows both
    ///         and Tailwind's <c>scale-*</c> emits the percentage.
    ///     </para>
    /// </remarks>
    static void Scaling(StyleValue value, out float x, out float y) {
        if (value.Kind != StyleValueKind.List) {
            x = y = Factor(value, 1f);
            return;
        }

        var parts = value.Items;
        x = parts.Length > 0 ? Factor(parts[0], 1f) : 1f;
        y = parts.Length > 1 ? Factor(parts[1], 1f) : x;
    }

    /// <summary>One scale factor.</summary>
    static float Factor(StyleValue value, float fallback) => value.Kind switch {
        StyleValueKind.Number => value.Number,
        StyleValueKind.Length when value.Unit == StyleUnit.Percent => value.Number / 100f,
        _ => fallback
    };
}
