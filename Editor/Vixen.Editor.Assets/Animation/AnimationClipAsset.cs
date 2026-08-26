// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.Curves;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Rendering;

namespace Vixen.Editor.Assets.Animation;

/// <summary>Which of a joint's ten numbers a curve drives.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Ten scalar curves rather than three vector tracks, and that is the whole reason this
///         format exists.</b> <see cref="AnimationChannel" /> — what an import writes — holds arrays
///         of <c>Vector3</c> and <c>Quaternion</c>, which is exactly right for a file produced by a
///         DCC and exactly wrong for an editor: a curve editor edits <i>one</i> number against time,
///         and a dope sheet's row is one number's keys. A vector track cannot express "X has a key
///         here and Y does not", which is most of what hand animation is.
///     </para>
///     <para>
///         ⚠ <b>Rotation is stored as a quaternion's four components and not as Euler angles.</b>
///         Doc 08's own note on <see cref="AnimationChannel" /> gives the reason and it survives the
///         change of shape: a track sampled between two Euler triples takes a different path from one
///         sampled between the rotations they represent, and the difference is the gimbal artefact
///         everybody has seen. Showing Euler angles in the <i>editor</i> is a display decision and is
///         a thing this format leaves open; storing them is not.
///     </para>
/// </remarks>
public enum AnimationProperty {
    /// <summary>Position, X.</summary>
    PositionX,

    /// <summary>Position, Y.</summary>
    PositionY,

    /// <summary>Position, Z.</summary>
    PositionZ,

    /// <summary>Rotation, X.</summary>
    RotationX,

    /// <summary>Rotation, Y.</summary>
    RotationY,

    /// <summary>Rotation, Z.</summary>
    RotationZ,

    /// <summary>Rotation, W.</summary>
    RotationW,

    /// <summary>Scale, X.</summary>
    ScaleX,

    /// <summary>Scale, Y.</summary>
    ScaleY,

    /// <summary>Scale, Z.</summary>
    ScaleZ,

    /// <summary>A blend shape's weight.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one property that is not one of a joint's ten, and the one that needs
    ///         <see cref="AnimationCurveData.Shape" /> beside it.</b> The nine above are components
    ///         of a transform and a target has at most one curve for each; a morphed mesh has as many
    ///         weight curves as it has shapes, all on the same node, so the pair
    ///         <c>(Property, Shape)</c> is what identifies a curve and not the property alone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Its target names the morphed mesh's <em>node</em>, which is not a joint.</b> That
    ///         is <see cref="AnimationChannel.Shape" />'s own rule — <c>AnimationClip.Create</c>
    ///         resolves a weight channel before it looks a joint up, precisely so a face's curves do
    ///         not land in <c>UnresolvedChannels</c>.
    ///     </para>
    ///     <para>
    ///         Appended last so that every value above keeps its ordinal. The YAML writes the name
    ///         and not the number, but a numeric form of this enum exists wherever one is cast, and
    ///         renumbering <c>ScaleZ</c> would be the kind of change that shows up as a clip driving
    ///         the wrong component.
    ///     </para>
    /// </remarks>
    Weight
}

/// <summary>One key of one curve.</summary>
/// <remarks>
///     The same five numbers <see cref="CurveSample" /> holds, because that is what evaluates them —
///     a second key type that had to be translated at both ends would be a second place for the
///     tangent convention to drift.
/// </remarks>
[DataContract("AnimationKey")]
public sealed class AnimationKeyData {
    /// <summary>When it is, in seconds.</summary>
    public float Time { get; set; }

    /// <summary>What the number is there.</summary>
    public float Value { get; set; }

    /// <summary>The slope coming in.</summary>
    public float InTangent { get; set; }

    /// <summary>And going out.</summary>
    public float OutTangent { get; set; }

    /// <summary>How the two are decided.</summary>
    public TangentMode Mode { get; set; } = TangentMode.Auto;

    /// <summary>The key as the evaluator reads it.</summary>
    /// <returns>The sample.</returns>
    public CurveSample ToSample() => new(Time, Value, InTangent, OutTangent, Mode);
}

/// <summary>One number of one joint, over time.</summary>
[DataContract("AnimationCurve")]
public sealed class AnimationCurveData {
    /// <summary>Which number it drives.</summary>
    public AnimationProperty Property { get; set; }

    /// <summary>
    ///     Which blend shape a <see cref="AnimationProperty.Weight" /> curve drives, by name, or
    ///     empty for any other property.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A name and not a slot, and that is load-bearing rather than stylistic.</b> The
    ///         ordinal a source file addresses a morph target by is not
    ///         <c>MeshData.MorphTargets</c>' — the import drops a shape that moves nothing above
    ///         <c>ModelImportSettings.BlendShapeThreshold</c> and deduplicates the names of the rest —
    ///         so a curve stored against an index re-targets itself the next time the mesh is
    ///         exported, silently. The imported half of the format made the same choice for the same
    ///         reason, and the authored half has to agree with it or a hand-keyed clip and an
    ///         imported one would bind differently.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is half of a curve's identity.</b> A target holds at most one curve per
    ///         property for the nine transform components and one per <em>shape</em> for
    ///         <see cref="AnimationProperty.Weight" />, because a face's node carries every shape it
    ///         has. Everything that looks a curve up takes both.
    ///     </para>
    /// </remarks>
    public string Shape { get; set; } = string.Empty;

    /// <summary>Its keys, in time order.</summary>
    public List<AnimationKeyData> Keys { get; set; } = [];
}

/// <summary>What one clip does to one joint.</summary>
[DataContract("AnimationTarget")]
public sealed class AnimationTargetData {
    /// <summary>Which joint or node it drives, by name.</summary>
    /// <remarks>
    ///     By name for <see cref="AnimationClip" />'s own reason: an editor has no skeleton to
    ///     resolve against and no guarantee that the skeleton it would resolve against is the one the
    ///     clip will be played on. Resolution happens once, at load, against the rig it is baked for.
    /// </remarks>
    public string Target { get; set; } = string.Empty;

    /// <summary>The curves it carries, at most one per property.</summary>
    public List<AnimationCurveData> Curves { get; set; } = [];
}

/// <summary>One event on the clip's timeline.</summary>
[DataContract("AnimationEvent")]
public sealed class AnimationEventData {
    /// <summary>What the event is called. A game reads this and decides.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When it fires, in seconds.</summary>
    public float Time { get; set; }

    /// <summary>Its float payload.</summary>
    public float Float { get; set; }

    /// <summary>Its integer payload.</summary>
    public int Int { get; set; }

    /// <summary>Its string payload.</summary>
    public string String { get; set; } = string.Empty;
}

/// <summary>A whole <c>.vxanim</c> document: the format doc 20's E5 says did not exist.</summary>
/// <remarks>
///     <para>
///         <b>Authored clips and imported clips are the same runtime object and not the same
///         file.</b> An FBX produces <see cref="AnimationClipData" /> through the model importer, and
///         nothing about that changes; what was missing is a clip somebody can <i>write</i> — a
///         camera move, a door, a UI wobble, a hand-keyed idle — and that wants keys with tangents
///         rather than baked samples. <see cref="ToContent" /> is the one-way door between them, so
///         everything downstream of an import stays unchanged.
///     </para>
///     <para>
///         ⚠ <b>The frame rate is a property of the editor, not of the clip.</b> It decides where the
///         timeline's snap lands and what "next frame" means; the keys are in seconds and sampling is
///         continuous, so changing it moves no key and re-times nothing. An editor that stored frames
///         would make a clip authored at 30 play at the wrong speed on a project that later chose 60.
///     </para>
///     <para>
///         <b>Why this is in the importer assembly and not in <c>Vixen.Animation</c>.</b> It is the
///         authored form, and nothing at run time reads it: the pipeline bakes it to
///         <see cref="AnimationClipContent" /> and a build loads that. Keeping it here is what lets
///         <see cref="AnimationClipImporter" /> see it — the editor's asset-editor assembly depends
///         on this one and not the other way round — and it keeps the YAML parser out of a runtime
///         assembly, which is the line <c>Vixen.Rendering</c> draws for the same reason.
///     </para>
/// </remarks>
[DataContract("AnimationClipAsset")]
public sealed class AnimationClipAsset {
    /// <summary>
    ///     ⚠ <b>The maths scalars, registered by the type that needs them.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A clip carries <see cref="Vector3" /> members — a constraint tag's region, its offsets,
    ///         its pole — and the converter table those are written through is process-wide and starts
    ///         empty. Until this ran, the only thing that filled it was <c>SceneSerializer</c>'s static
    ///         constructor, so whether a <c>.vxanim</c> round-tripped its vectors depended on whether
    ///         anything in the process had touched a <i>scene</i> first. An editor that opened a clip
    ///         before ever opening a level read every one of those members back as zero, and said
    ///         nothing.
    ///     </para>
    ///     <para>
    ///         Found as a test that passed in its assembly and failed alone: <c>AuthoringTests</c>'
    ///         constraint round trip asserts a region of <c>(0.05, 0.01, 0.05)</c> and got
    ///         <c>(0, 0, 0)</c> whenever it ran before anything scene-shaped. It failed the Windows
    ///         leg one run and the macOS leg the next, which is what an ordering dependency looks like
    ///         from CI.
    ///     </para>
    ///     <para>
    ///         Tied to this type rather than to a module initializer, which is
    ///         <c>SceneScalars.Register</c>'s reasoning and is right: the table is global, so
    ///         registering merely because an assembly is referenced would make the blast radius every
    ///         document in the process instead of the formats that share the convention.
    ///     </para>
    /// </remarks>
    static AnimationClipAsset() => MathScalars.Register();

    /// <summary>The version this reader and writer speak.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>2 since a curve may drive a blend shape's weight</b> —
    ///         <see cref="AnimationProperty.Weight" /> and <see cref="AnimationCurveData.Shape" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A genuine compatibility fence, unlike <c>ModelImporter.Version</c> 10 and
    ///         <c>AnimationClipContent.Current</c> 2, which were re-import triggers.</b> Those two are
    ///         binary chunks written by the generated serializer, which stores a member count and
    ///         refuses only <c>count &gt; MemberCount</c> — so an appended member reads back out of
    ///         older bytes as its default and an older build answers "no weight track", which is true.
    ///         This is YAML bound by name, and the value that moved is inside an <b>enum</b>:
    ///         <c>YamlSerializer</c> binds one with <c>Enum.Parse</c>, which <em>throws</em> on a name
    ///         it does not have. An older build meeting <c>property: Weight</c> fails with a parse
    ///         error naming a value rather than a file. The fence turns that into the sentence
    ///         <see cref="FromYaml" /> writes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is why the number a file carries is <see cref="MinimumVersion" /> and not
    ///         this constant.</b> A fence that stamped every clip would make every clip in the project
    ///         unreadable by an older build the first time anybody saved one, for a member none of
    ///         them uses. A clip carrying no weight curve is a version-1 clip and says so.
    ///     </para>
    /// </remarks>
    public const int Current = 2;

    /// <summary>What an authored clip is written as.</summary>
    public const string Extension = ".vxanim";

    /// <summary>Which version of the format this file is.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the clip is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>How long it plays for, in seconds.</summary>
    /// <remarks>
    ///     Stored rather than derived from the last key, because a clip whose last two seconds are a
    ///     deliberate hold has no key in them — and a duration that shrank to the last key would
    ///     silently delete that hold every time the file was saved.
    /// </remarks>
    public float Duration { get; set; } = 1f;

    /// <summary>What the timeline snaps to, in frames a second.</summary>
    public float FrameRate { get; set; } = 30f;

    /// <summary>What happens when it runs past the end.</summary>
    public WrapMode Wrap { get; set; } = WrapMode.Loop;

    /// <summary>The sequence this clip was marked up against, by asset path, or empty.</summary>
    /// <remarks>
    ///     <para>
    ///         Assisted authoring needs to know what the clip was authored <em>against</em>: which
    ///         actors were in the scene, which clip each was playing, what props were attached to whom.
    ///         A clip on its own does not carry that, and without it a proposal engine has nothing to
    ///         measure proximity between. Vixen already has a sequencer, so this is a reference to one
    ///         rather than a second format.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Authoring-time only, and <see cref="ToContent" /> deliberately drops it.</b> It is
    ///         not loaded by a build, not shipped, and nothing at runtime may depend on it — a
    ///         constraint that cannot be resolved from the live game alone is a bug and not a feature.
    ///         Carrying it into the artefact would make that bug easy to write and impossible to
    ///         notice, so the one-way door is where it stops. There is a test for exactly this.
    ///     </para>
    /// </remarks>
    public string AuthoringContext { get; set; } = string.Empty;

    /// <summary>What it moves.</summary>
    public List<AnimationTargetData> Targets { get; set; } = [];

    /// <summary>What it raises, in time order.</summary>
    public List<AnimationEventData> Events { get; set; } = [];

    /// <summary>The constraints marked up on it, in the order they were placed.</summary>
    /// <remarks>
    ///     ⚠ <b>The runtime record, used verbatim, and not an authored twin of it.</b> A clip's curves
    ///     are authored as tangents and shipped as samples, so those genuinely need two types and a
    ///     compile step between them. A constraint is authored as exactly what it ships as — names,
    ///     numbers and a discriminator — and the only work the pipeline does is <em>checking</em> it.
    ///     Inventing a second identical type to have somewhere to do that would be ceremony, and would
    ///     be one more place a new field has to be added.
    /// </remarks>
    public List<ConstraintTagRecord> Constraints { get; set; } = [];

    /// <summary>Metadata this build did not interpret, by kind, exactly as it was written.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The open half of the format, and the reason it is a reserved block rather than
    ///         tolerated stray keys.</b> The binder ignores a key it does not know — deliberately, so
    ///         that an older editor can open a newer project — which means an unknown key at the root
    ///         is <i>silently deleted</i> the next time the file is saved. That is the wrong failure
    ///         for markup somebody spent a day authoring.
    ///     </para>
    ///     <para>
    ///         So everything a build might not understand goes under one key it always understands,
    ///         bound as a raw node and written back out unchanged. <b>Unrecognised is preserved,
    ///         never dropped</b> — and a round trip through a build that knows nothing about a kind
    ///         produces the same file it read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A kind is a name, not a type tag.</b> Nothing resolves it against the type
    ///         registry, because the whole point is to carry what this build has no type for.
    ///     </para>
    /// </remarks>
    public Dictionary<string, YamlNode> Extensions { get; set; } = [];

    /// <summary>Reads YAML into a clip.</summary>
    /// <param name="yaml">The text.</param>
    /// <returns>The clip.</returns>
    /// <exception cref="NotSupportedException">The file is from a newer editor.</exception>
    public static AnimationClipAsset FromYaml(string yaml) {
        ArgumentNullException.ThrowIfNull(yaml);

        if (yaml.Trim().Length == 0) {
            // An empty file is a new clip rather than an error, for `AssetFile.Read`'s reason: the
            // ordinary way to make one of these is to create the file and open it.
            return new();
        }

        var clip = YamlSerializer.Parse<AnimationClipAsset>(yaml);

        return clip.Version <= Current
            ? clip
            : throw new NotSupportedException(
                $"This clip is version {clip.Version} and this build reads {Current}. Reading it would bind "
                + "the parts it recognises and drop the rest."
            );
    }

    /// <summary>The lowest version of this format that can read this clip back.</summary>
    /// <remarks>
    ///     ⚠ <b>What the clip <em>uses</em>, not what wrote it.</b> See <see cref="Current" />: the
    ///     only thing version 2 added is a value an older reader's <c>Enum.Parse</c> throws on, so a
    ///     clip that carries no weight curve is readable by a version-1 build and stamping it 2 would
    ///     lock every clip in a project out of one for nothing.
    /// </remarks>
    public int MinimumVersion =>
        Targets.Any(target => target.Curves.Any(curve => curve.Property == AnimationProperty.Weight)) ? 2 : 1;

    /// <summary>Writes it as YAML.</summary>
    /// <returns>The text.</returns>
    /// <remarks>
    ///     ⚠ <b>Stamps <see cref="Version" /> on the way out</b>, because the number in the file is a
    ///     claim about what is in the file and only this method knows what is about to be in it. A
    ///     clip read at version 1, given a weight curve and saved comes back out at 2; one that lost
    ///     its last weight curve comes back out at 1.
    /// </remarks>
    public string ToYaml() {
        Version = MinimumVersion;

        return YamlSerializer.ToYaml(this);
    }

    /// <summary>The events, as the runtime's own record, in time order.</summary>
    /// <returns>The events.</returns>
    public AnimationEvent[] ToEvents() =>
        [.. Events.OrderBy(entry => entry.Time)
            .Select(entry => new AnimationEvent(entry.Name, entry.Time, entry.Float, entry.Int, entry.String))];

    /// <summary>The whole clip as the artefact a build loads.</summary>
    /// <returns>The compiled clip.</returns>
    public AnimationClipContent ToContent() => new() {
        Name = Name,
        Wrap = Wrap,
        Data = ToClipData(),
        Events = ToEvents(),
        Constraints = [.. Constraints],

        // Re-emitted rather than carried as nodes, because the runtime type has no parser and no
        // wish for one. A consumer that knows a kind parses its own block.
        Extensions = Extensions.ToDictionary(
            entry => entry.Key,
            entry => YamlWriter.Write(entry.Value),
            StringComparer.Ordinal
        )
    };

    /// <summary>Bakes the curves into the sampled channels an import would have produced.</summary>
    /// <returns>The clip data.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The union of key times per group, not a fixed sample rate.</b> Baking at the frame
    ///         rate would turn a two-key linear slide into sixty keys and would still be wrong for a
    ///         curve whose interesting moment falls between two frames. Sampling the curve at every
    ///         time <i>any</i> of the group's components has a key keeps the result exact wherever the
    ///         author put a key and interpolated everywhere else — which is what the curves say.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A group with no curves at all produces no keys</b>, rather than one key holding
    ///         the identity. A channel that writes an identity scale is not the same as one that does
    ///         not write scale: the first overwrites whatever a layer underneath set.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A weight curve becomes a channel of its own, and a target whose curves are
    ///         <em>all</em> weight curves emits no transform channel at all.</b> Emitting an empty one
    ///         beside them would be free everywhere except in the one number that matters: a weight
    ///         curve names the morphed mesh's node, which is not a joint, so
    ///         <c>AnimationClip.Create</c> would put that empty channel in
    ///         <c>AnimationClip.UnresolvedChannels</c> — the count somebody watches to notice a clip
    ///         playing on the wrong rig — and a correct facial clip would report one unresolved
    ///         channel per face. The imported path avoids this by construction, because Assimp keeps
    ///         node transforms and morph weights in different lists; the authored path has to avoid
    ///         it on purpose.
    ///     </para>
    /// </remarks>
    public AnimationClipData ToClipData() {
        List<AnimationChannel> channels = [];

        foreach (var target in Targets) {
            var channel = new AnimationChannel { Target = target.Target };

            // Weight first, so that the transform channel below can be skipped when there was nothing
            // else on this target — see the note above about UnresolvedChannels.
            var weighted = 0;

            foreach (var curve in target.Curves) {
                if (curve.Property != AnimationProperty.Weight) {
                    continue;
                }

                weighted++;

                if (curve.Shape.Length == 0 || curve.Keys.Count == 0) {
                    // A weight curve with no shape names nothing and a curve with no keys says
                    // nothing — and "says nothing" is a different fact from "holds at zero", which is
                    // AnimationChannel.WeightTimes' own rule. Neither becomes a channel.
                    continue;
                }

                var keys = curve.Keys.OrderBy(key => key.Time).ToArray();

                channels.Add(
                    new() {
                        Target = target.Target,
                        Shape = curve.Shape,
                        WeightTimes = [.. keys.Select(key => key.Time)],

                        // Sampled through the same evaluator the transform components use rather than
                        // copied out of the keys, so that a weight curve's tangents mean what every
                        // other curve's tangents mean. At a key the two agree exactly.
                        Weights = [.. keys.Select(key => Sample(target, AnimationProperty.Weight, key.Time, shape: curve.Shape))]
                    }
                );
            }

            if (weighted > 0 && weighted == target.Curves.Count) {
                continue;
            }

            var position = Times(target, AnimationProperty.PositionX, AnimationProperty.PositionY, AnimationProperty.PositionZ);
            var rotation = Times(target, AnimationProperty.RotationX, AnimationProperty.RotationY, AnimationProperty.RotationZ, AnimationProperty.RotationW);
            var scale = Times(target, AnimationProperty.ScaleX, AnimationProperty.ScaleY, AnimationProperty.ScaleZ);

            channel.PositionTimes = [.. position];
            channel.Positions = [.. position.Select(time => new Vector3(
                Sample(target, AnimationProperty.PositionX, time),
                Sample(target, AnimationProperty.PositionY, time),
                Sample(target, AnimationProperty.PositionZ, time)
            ))];

            channel.RotationTimes = [.. rotation];
            channel.Rotations = [.. rotation.Select(time => Normalised(
                Sample(target, AnimationProperty.RotationX, time),
                Sample(target, AnimationProperty.RotationY, time),
                Sample(target, AnimationProperty.RotationZ, time),
                Sample(target, AnimationProperty.RotationW, time, fallback: 1f)
            ))];

            channel.ScaleTimes = [.. scale];
            channel.Scales = [.. scale.Select(time => new Vector3(
                Sample(target, AnimationProperty.ScaleX, time, fallback: 1f),
                Sample(target, AnimationProperty.ScaleY, time, fallback: 1f),
                Sample(target, AnimationProperty.ScaleZ, time, fallback: 1f)
            ))];

            channels.Add(channel);
        }

        return new() { Name = Name, Duration = Duration, Channels = [.. channels] };
    }

    /// <summary>
    ///     ⚠ Normalised on the way out, because interpolating four components independently does not
    ///     produce a unit quaternion — and a rotation that is not unit is a scale nobody authored.
    /// </summary>
    static Quaternion Normalised(float x, float y, float z, float w) {
        var length = MathF.Sqrt((x * x) + (y * y) + (z * z) + (w * w));

        return length > 1e-6f
            ? new(x / length, y / length, z / length, w / length)
            : Quaternion.Identity;
    }

    /// <summary>Every time any of a group's components has a key, sorted and deduplicated.</summary>
    static List<float> Times(AnimationTargetData target, params ReadOnlySpan<AnimationProperty> properties) {
        SortedSet<float> times = [];

        foreach (var property in properties) {
            if (Find(target, property, shape: string.Empty) is not { } found) {
                continue;
            }

            foreach (var key in found.Keys) {
                times.Add(key.Time);
            }
        }

        return [.. times];
    }

    /// <summary>
    ///     The curve driving one property of one target, identified by the pair that identifies a
    ///     curve.
    /// </summary>
    /// <param name="target">The target to look in.</param>
    /// <param name="property">Which number.</param>
    /// <param name="shape">
    ///     Which blend shape, for <see cref="AnimationProperty.Weight" />; empty for anything else.
    /// </param>
    /// <returns>The curve, or <see langword="null" />.</returns>
    /// <remarks>
    ///     ⚠ <b>The shape is compared for every property and not only for
    ///     <see cref="AnimationProperty.Weight" />.</b> A transform curve's shape is empty, so
    ///     comparing it costs nothing and asking a caller to remember which properties carry one is
    ///     how the two halves drift.
    /// </remarks>
    public static AnimationCurveData? Find(AnimationTargetData target, AnimationProperty property, string shape) =>
        target.Curves.Find(
            curve => curve.Property == property && string.Equals(curve.Shape, shape, StringComparison.Ordinal)
        );

    /// <summary>One component's value at a time, or its fallback when it has no curve.</summary>
    static float Sample(
        AnimationTargetData target,
        AnimationProperty property,
        float time,
        float fallback = 0f,
        string shape = ""
    ) {
        if (Find(target, property, shape) is not { Keys.Count: > 0 } found) {
            return fallback;
        }

        var samples = new CurveSample[found.Keys.Count];

        for (var index = 0; index < samples.Length; index++) {
            samples[index] = found.Keys[index].ToSample();
        }

        // Sorted here rather than assumed, because a hand-edited file is allowed to be out of order
        // and the evaluator's contract is that its caller has already dealt with that.
        Array.Sort(samples, static (left, right) => left.Time.CompareTo(right.Time));

        return CurveEvaluation.Evaluate(samples, time);
    }
}
