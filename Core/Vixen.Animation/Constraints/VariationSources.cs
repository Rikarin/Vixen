// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Rendering;

namespace Vixen.Animation.Constraints;

/// <summary>The body, the props and the ground one cell of the harness matrix runs against.</summary>
/// <remarks>
///     <para>
///         Mutable and handed to every variation source in turn, so two axes compose without knowing
///         about each other: a body range and a ground slope are applied to the same subject and the
///         cell is the pair. A source that returned a fresh subject would make the last one win.
///     </para>
///     <para>
///         ⚠ <b>The skeleton is a field, not a fixed input.</b> Varying body proportions means a
///         different skeleton, and a harness that could only vary numbers on one skeleton would miss
///         the failure the whole document is about.
///     </para>
/// </remarks>
public sealed class HarnessSubject {
    /// <summary>The rig this cell poses.</summary>
    public required Skeleton Skeleton { get; set; }

    /// <summary>Its proxy shapes, or <see langword="null" /> if it carries none.</summary>
    public ProxyShapeSet? Shapes { get; set; }

    /// <summary>Where each named entity slot is, for the goals expressed against one.</summary>
    public Dictionary<Symbol, BoneTransform> Slots { get; } = [];

    /// <summary>What the ground is doing under the character, as a plane's frame.</summary>
    public BoneTransform Ground { get; set; } = BoneTransform.Identity;

    /// <summary>What this configuration is called, built up by the sources that made it.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Adds a word to the label.</summary>
    /// <param name="part">The word.</param>
    public void Describe(string part) => Label = Label.Length == 0 ? part : $"{Label} · {part}";
}

/// <summary>One thing the harness varies, and the values it takes.</summary>
/// <remarks>
///     ⚠ <b>An axis, not a scenario.</b> Two sources are a grid and not two runs, because the
///     failures this exists to find are the ones at a corner — a short body <em>and</em> a wide prop —
///     and running each axis alone would never visit one.
/// </remarks>
public interface IVariationSource {
    /// <summary>What it varies.</summary>
    string Name { get; }

    /// <summary>How many values it takes.</summary>
    int Count { get; }

    /// <summary>What one of them is called.</summary>
    /// <param name="index">Which.</param>
    /// <returns>The name.</returns>
    string Label(int index);

    /// <summary>Sets a subject up for one of them.</summary>
    /// <param name="index">Which.</param>
    /// <param name="subject">The subject, as the sources before it left it.</param>
    void Apply(int index, HarnessSubject subject);
}

/// <summary>Body proportions across a range, which is the axis the whole document is about.</summary>
/// <remarks>
///     <para>
///         Scales every joint's bind offset and rebuilds the skeleton, so a clip authored on one body
///         is genuinely played on another rather than on the same one with a number changed. The
///         proxy shapes go with it — a shape set left at the original size on a body half the height
///         is a set that no longer touches the body, and every contact would read as a miss for a
///         reason that is the harness's fault rather than the clip's.
///     </para>
///     <para>
///         ⚠ <b>Uniform, and that is a real limitation rather than a simplification.</b> A long-armed
///         short character is the interesting case and this cannot make one; doc 33's character
///         creator produces those, and this takes whatever bodies it is given through
///         <see cref="Bodies" />. What this source is for is the cheap sweep somebody runs before
///         they have a body range at all.
///     </para>
/// </remarks>
public sealed class BodyVariation : IVariationSource {
    readonly Skeleton skeleton;
    readonly float[] scales;

    /// <summary>Varies a rig's size across a set of factors.</summary>
    /// <param name="skeleton">The rig as authored.</param>
    /// <param name="scales">The factors, where one is the authored size.</param>
    public BodyVariation(Skeleton skeleton, params ReadOnlySpan<float> scales) {
        ArgumentNullException.ThrowIfNull(skeleton);

        this.skeleton = skeleton;
        this.scales = scales.Length > 0 ? scales.ToArray() : [1f];
    }

    /// <inheritdoc />
    public string Name => "body";

    /// <inheritdoc />
    public int Count => scales.Length;

    /// <inheritdoc />
    public string Label(int index) => string.Create(CultureInfo.InvariantCulture, $"body ×{scales[index]:0.##}");

    /// <inheritdoc />
    public void Apply(int index, HarnessSubject subject) {
        ArgumentNullException.ThrowIfNull(subject);

        var scale = scales[index];

        subject.Skeleton = Resize(skeleton, scale);
        subject.Shapes = subject.Shapes?.Resized($"{subject.Shapes.Name}×{scale:0.##}", new Vector3(scale));

        subject.Describe(Label(index));
    }

    /// <summary>The same rig at a different size.</summary>
    /// <param name="skeleton">The rig.</param>
    /// <param name="scale">How much bigger.</param>
    /// <returns>The resized rig.</returns>
    /// <remarks>
    ///     ⚠ <b>The offsets are scaled and the rotations are not.</b> Scaling a joint's rotation is
    ///     meaningless and scaling its local scale would compound down the hierarchy, so a rig twice
    ///     the size is one whose bones are twice as long in the same directions — which is what
    ///     "twice the size" means to everybody who is not writing the code.
    /// </remarks>
    public static Skeleton Resize(Skeleton skeleton, float scale) {
        ArgumentNullException.ThrowIfNull(skeleton);

        if (MathF.Abs(scale - 1f) < 1e-4f) {
            return skeleton;
        }

        var bind = skeleton.BindPose;
        var joints = new SkeletonJoint[skeleton.JointCount];
        var model = new Matrix4x4[skeleton.JointCount];

        for (var index = 0; index < skeleton.JointCount; index++) {
            var local = bind[index];
            var scaled = Matrix4x4.Compose(local.Scale, local.Rotation, local.Translation * scale);
            var parent = skeleton.ParentOf(index);

            model[index] = parent >= 0 ? scaled * model[parent] : scaled;

            Matrix4x4.Invert(model[index], out var inverse);

            joints[index] = new() {
                Name = skeleton.NameOf(index),
                Parent = parent,
                InverseBindPose = inverse
            };
        }

        return Skeleton.Create(new() { Name = $"{skeleton.Name}×{scale:0.##}", Joints = joints });
    }
}

/// <summary>A set of bodies somebody actually has, rather than one body at several sizes.</summary>
/// <remarks>
///     What doc 33's range feeds in, and what a project with three hand-built characters uses. It is
///     the same axis as <see cref="BodyVariation" /> from the harness's point of view, which is the
///     point of the interface being an interface.
/// </remarks>
public sealed class Bodies : IVariationSource {
    readonly (string Name, Skeleton Skeleton, ProxyShapeSet? Shapes)[] bodies;

    /// <summary>Varies across a set of rigs.</summary>
    /// <param name="bodies">The rigs, each with the shape set built against it.</param>
    public Bodies(params ReadOnlySpan<(string Name, Skeleton Skeleton, ProxyShapeSet? Shapes)> bodies) {
        if (bodies.Length == 0) {
            throw new ArgumentException("A body axis with no bodies varies nothing.", nameof(bodies));
        }

        this.bodies = bodies.ToArray();
    }

    /// <inheritdoc />
    public string Name => "body";

    /// <inheritdoc />
    public int Count => bodies.Length;

    /// <inheritdoc />
    public string Label(int index) => bodies[index].Name;

    /// <inheritdoc />
    public void Apply(int index, HarnessSubject subject) {
        ArgumentNullException.ThrowIfNull(subject);

        var (name, skeleton, shapes) = bodies[index];

        subject.Skeleton = skeleton;

        if (shapes is not null) {
            subject.Shapes = shapes;
        }

        subject.Describe(name);
    }
}

/// <summary>A prop across a class of interchangeable ones: same slot, different dimensions.</summary>
/// <remarks>
///     A rail of two radii, a mug of three sizes, a ledge at four heights. The prop is expressed as
///     the transform an <see cref="EntityFrame" /> slot resolves to, because that is how a goal
///     reaches it — a harness that modelled props any other way would be testing something the
///     runtime does not do.
/// </remarks>
public sealed class PropVariation : IVariationSource {
    readonly Symbol slot;
    readonly (string Name, BoneTransform Where)[] props;

    /// <summary>Varies what a slot resolves to.</summary>
    /// <param name="slot">The binding slot.</param>
    /// <param name="props">The props, each with where and how big it is.</param>
    public PropVariation(string slot, params ReadOnlySpan<(string Name, BoneTransform Where)> props) {
        if (props.Length == 0) {
            throw new ArgumentException("A prop axis with no props varies nothing.", nameof(props));
        }

        this.slot = Symbol.Intern(slot);
        this.props = props.ToArray();
        Name = slot;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public int Count => props.Length;

    /// <inheritdoc />
    public string Label(int index) => props[index].Name;

    /// <inheritdoc />
    public void Apply(int index, HarnessSubject subject) {
        ArgumentNullException.ThrowIfNull(subject);

        subject.Slots[slot] = props[index].Where;
        subject.Describe(props[index].Name);
    }
}

/// <summary>Ground slope and height, which is what breaks a foot plant.</summary>
/// <remarks>
///     ⚠ <b>Slope and height together rather than two axes.</b> They are not independent in practice
///     — a character on a slope is also standing higher or lower than the clip assumed — and treating
///     them as two axes would spend most of the matrix on combinations nobody stands in.
/// </remarks>
public sealed class GroundVariation : IVariationSource {
    readonly (float Degrees, float Height)[] steps;

    /// <summary>Varies the ground under the character.</summary>
    /// <param name="steps">Each a slope in degrees and a height in metres.</param>
    public GroundVariation(params ReadOnlySpan<(float Degrees, float Height)> steps) {
        if (steps.Length == 0) {
            throw new ArgumentException("A ground axis with no steps varies nothing.", nameof(steps));
        }

        this.steps = steps.ToArray();
    }

    /// <summary>The slot the ground plane is bound to.</summary>
    public const string Slot = "ground";

    /// <inheritdoc />
    public string Name => "ground";

    /// <inheritdoc />
    public int Count => steps.Length;

    /// <inheritdoc />
    public string Label(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{steps[index].Degrees:0.#}° at {steps[index].Height:0.##} m");

    /// <inheritdoc />
    public void Apply(int index, HarnessSubject subject) {
        ArgumentNullException.ThrowIfNull(subject);

        var (degrees, height) = steps[index];

        var ground = new BoneTransform(
            new Vector3(0f, height, 0f),
            Quaternion.FromAxisAngle(Vector3.UnitZ, MathUtil.DegreesToRadians(degrees)),
            Vector3.One
        );

        subject.Ground = ground;
        subject.Slots[Symbol.Intern(Slot)] = ground;

        subject.Describe(Label(index));
    }
}
