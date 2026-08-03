// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using Vixen.Core.Mathematics;
using Vixen.Rendering;

namespace Vixen.Animation;

/// <summary>
///     A skeleton as the runtime uses it: parents, inverse bind poses, a bind pose to start from,
///     and a name lookup that costs nothing after load.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="SkeletonData" /> is what an import writes and what the object database stores.
///         This is what a frame reads. The difference is small and it is all precomputation: the
///         local bind transform of every joint, which the data only holds inverted and in model
///         space, and a name-to-index map, which a clip needs once when it is baked and which
///         nothing should ever build in a frame.
///     </para>
///     <para>
///         <b>Parents precede children</b>, which <see cref="TryCreate" /> checks rather than
///         assumes. Every model-space pass in this assembly is a single forward loop that reads its
///         parent's already-composed transform; an out-of-order skeleton would silently read last
///         frame's value for one joint and produce a limb that lags by a frame, which is a far worse
///         failure than refusing to load.
///     </para>
///     <para>
///         Immutable once built. A skeleton is shared by every character wearing it — a hundred
///         instances of the same enemy hold one of these and a pose each — so nothing about it may
///         depend on who is looking.
///     </para>
/// </remarks>
public sealed class Skeleton {
    readonly string[] names;
    readonly int[] parents;
    readonly Matrix4x4[] inverseBindPoses;
    readonly BoneTransform[] bindPose;
    readonly JointLimit[]? limits;
    readonly FrozenDictionary<string, int> byName;

    Skeleton(
        string name,
        string[] names,
        int[] parents,
        Matrix4x4[] inverseBindPoses,
        BoneTransform[] bindPose,
        JointLimit[]? limits,
        FrozenDictionary<string, int> byName
    ) {
        Name = name;
        this.names = names;
        this.parents = parents;
        this.inverseBindPoses = inverseBindPoses;
        this.bindPose = bindPose;
        this.limits = limits;
        this.byName = byName;
    }

    /// <summary>What the skeleton is called.</summary>
    public string Name { get; }

    /// <summary>How many joints it has.</summary>
    public int JointCount => parents.Length;

    /// <summary>Every joint's parent index, in joint order. −1 for a root.</summary>
    /// <returns>The parents.</returns>
    public ReadOnlySpan<int> Parents => parents;

    /// <summary>Every joint's name, in joint order.</summary>
    /// <returns>The names.</returns>
    public ReadOnlySpan<string> Names => names;

    /// <summary>
    ///     Every joint's model-space-to-joint-space transform at bind time, in joint order.
    /// </summary>
    /// <returns>The inverse bind poses.</returns>
    public ReadOnlySpan<Matrix4x4> InverseBindPoses => inverseBindPoses;

    /// <summary>
    ///     The pose a character is in when nothing is playing: every joint's local transform at bind
    ///     time.
    /// </summary>
    /// <returns>The bind pose, in joint order.</returns>
    /// <remarks>
    ///     Derived rather than stored, because <see cref="SkeletonData" /> only carries the inverted
    ///     model-space form that skinning wants. A pose has to start somewhere, and starting from
    ///     zero puts a character at the origin folded into a point; starting from the bind pose puts
    ///     it in the shape the artist modelled, which is also the correct value for any joint no
    ///     clip in the graph happens to drive.
    /// </remarks>
    public ReadOnlySpan<BoneTransform> BindPose => bindPose;

    /// <summary>Whether any joint on this rig has a range of motion.</summary>
    /// <remarks>
    ///     ⚠ <b>What the arbiter checks before it does anything about limits.</b> Almost no rig has
    ///     them and the clamp costs a decomposition per joint per solve, so a rig that declares none
    ///     pays one boolean.
    /// </remarks>
    public bool HasLimits => limits is not null;

    /// <summary>How far a joint may turn from where it was modelled.</summary>
    /// <param name="index">The joint.</param>
    /// <returns>Its limit, or <see cref="JointLimit.Free" /> if it has none.</returns>
    public JointLimit LimitOf(int index) =>
        limits is not null && (uint)index < (uint)limits.Length ? limits[index] : JointLimit.Free;

    /// <summary>The index of a joint, or −1 if the skeleton has no joint by that name.</summary>
    /// <param name="jointName">The joint's name.</param>
    /// <returns>Its index, or −1.</returns>
    public int IndexOf(string jointName) => byName.TryGetValue(jointName, out var index) ? index : -1;

    /// <summary>The name of a joint.</summary>
    /// <param name="index">The joint's index.</param>
    /// <returns>Its name.</returns>
    public string NameOf(int index) => names[index];

    /// <summary>The parent of a joint, or −1 if it is a root.</summary>
    /// <param name="index">The joint's index.</param>
    /// <returns>The parent's index, or −1.</returns>
    public int ParentOf(int index) => parents[index];

    /// <summary>Whether <paramref name="joint" /> is <paramref name="ancestor" /> or hangs off it.</summary>
    /// <param name="joint">The joint to test.</param>
    /// <param name="ancestor">The joint it might descend from.</param>
    /// <returns><see langword="true" /> if it does, and for a joint against itself.</returns>
    /// <remarks>
    ///     Walks up rather than down, so it is O(depth) with no allocation. Masks are built out of
    ///     this: "the upper body" is authored as one joint name and means everything below it.
    /// </remarks>
    public bool IsDescendantOf(int joint, int ancestor) {
        for (var current = joint; current >= 0; current = parents[current]) {
            if (current == ancestor) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds the runtime skeleton from what a content build produced.</summary>
    /// <param name="data">The imported skeleton.</param>
    /// <param name="skeleton">The runtime skeleton, or <see langword="null" /> if the data is not usable.</param>
    /// <param name="error">Why it is not usable, or <see langword="null" />.</param>
    /// <returns><see langword="true" /> if a skeleton was built.</returns>
    /// <remarks>
    ///     A <c>TryCreate</c> and not a constructor that throws, because the input is content: it
    ///     arrives from a file somebody exported, and an importer or an editor wants to report a
    ///     broken rig against the asset that holds it rather than take an exception through a load.
    ///     <see cref="Create" /> is the same thing for code that built the data itself and would
    ///     rather fail loudly.
    /// </remarks>
    public static bool TryCreate(SkeletonData data, out Skeleton? skeleton, out string? error) {
        ArgumentNullException.ThrowIfNull(data);

        skeleton = null;
        error = null;

        var joints = data.Joints;
        var count = joints.Length;
        var names = new string[count];
        var parents = new int[count];
        var inverseBindPoses = new Matrix4x4[count];

        for (var index = 0; index < count; index++) {
            var joint = joints[index];
            var parent = joint.Parent;

            if (parent >= index || parent < -1) {
                error = parent == index
                    ? $"Joint {index} ('{joint.Name}') is its own parent."
                    : $"Joint {index} ('{joint.Name}') has parent {parent}; parents must precede children.";

                return false;
            }

            if (string.IsNullOrEmpty(joint.Name)) {
                error = $"Joint {index} has no name. Animation channels address joints by name.";
                return false;
            }

            names[index] = joint.Name;
            parents[index] = parent;
            inverseBindPoses[index] = joint.InverseBindPose;
        }

        // ⚠ Allocated only if some joint declares one. A rig with no limits carries no array and
        // answers `HasLimits` false, which is the check every consumer makes before paying for a
        // swing–twist decomposition per joint per solve.
        JointLimit[]? limits = null;

        for (var index = 0; index < count; index++) {
            if (!joints[index].Limited) {
                continue;
            }

            if (limits is null) {
                limits = new JointLimit[count];
                Array.Fill(limits, JointLimit.Free);
            }

            limits[index] = JointLimit.Of(joints[index].Swing, joints[index].Twist, joints[index].TwistAxis);
        }

        var byName = new Dictionary<string, int>(count, StringComparer.Ordinal);

        for (var index = 0; index < count; index++) {
            // First wins. A duplicate is a broken export rather than a fatal one — the joints are
            // still addressable by index, and refusing to load the rig over it would block a fix
            // that is one rename away.
            byName.TryAdd(names[index], index);
        }

        skeleton = new(
            data.Name,
            names,
            parents,
            inverseBindPoses,
            DeriveBindPose(parents, inverseBindPoses),
            limits,
            byName.ToFrozenDictionary(StringComparer.Ordinal)
        );

        return true;
    }

    /// <summary>Builds the runtime skeleton, or throws if the data is not usable.</summary>
    /// <param name="data">The imported skeleton.</param>
    /// <returns>The runtime skeleton.</returns>
    /// <exception cref="ArgumentException">The skeleton is malformed; the message says how.</exception>
    public static Skeleton Create(SkeletonData data) =>
        TryCreate(data, out var skeleton, out var error)
            ? skeleton!
            : throw new ArgumentException(error, nameof(data));

    static BoneTransform[] DeriveBindPose(int[] parents, Matrix4x4[] inverseBindPoses) {
        var count = parents.Length;
        var model = new Matrix4x4[count];
        var local = new BoneTransform[count];

        for (var index = 0; index < count; index++) {
            // The bind pose is the inverse of what the data stores, which is stored inverted because
            // that is the direction skinning multiplies in. One inversion per joint at load is the
            // price of never doing one in a frame.
            if (!Matrix4x4.Invert(inverseBindPoses[index], out model[index])) {
                model[index] = Matrix4x4.Identity;
            }
        }

        for (var index = 0; index < count; index++) {
            var parent = parents[index];

            if (parent < 0) {
                local[index] = BoneTransform.FromMatrix(model[index]);
                continue;
            }

            // world = local * parent, so local = world * parent⁻¹. The parent's inverse bind pose is
            // exactly that inverse and is already to hand, which is why this loop does no second
            // inversion.
            local[index] = BoneTransform.FromMatrix(model[index] * inverseBindPoses[parent]);
        }

        return local;
    }
}
