// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Animation.Moves;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     Doc 34's four documents ask the host for the one thing they will not go looking for, and the
///     editor is where the asking is answered.
/// </summary>
/// <remarks>
///     ⚠ <b>Every one of these asserts on the <i>second</i> failure message rather than a success.</b>
///     A document with no hook set says "no project is bound"; a document whose hook is set and could
///     not find the file says which file. That difference is the whole claim being made here — that
///     something wired it — and it can be made without a character to point at, which nothing in this
///     repository has.
/// </remarks>
public class AnimationWiringTests {
    /// <summary>A move set opened in the editor can see the sets it overlays.</summary>
    [Fact]
    public void An_overlay_resolves_the_set_underneath_it() {
        using var session = EditorSession.Start();

        Write(session, "Assets/walks.vxmoveset", "name: walks\nentries:\n  - name: walk\n    clip: Assets/walk.vxanim\n");

        Write(
            session,
            "Assets/injured.vxmoveset",
            "name: injured\nbases:\n  - Assets/walks.vxmoveset\nentries:\n  - name: limp\n    clip: Assets/limp.vxanim\n"
        );

        var document = Assert.IsType<MoveSetDocument>(Open(session, "Assets/injured.vxmoveset"));

        var (address, content) = Assert.Single(document.Underlay());

        Assert.Equal("Assets/walks.vxmoveset", address);
        Assert.Equal("walks", content.Name);

        // And the table shows both rows, which is what the overlay is for.
        Assert.Equal(2, document.Preview().Count);
    }

    /// <summary>A shape set's vocabulary is bound, and a rig it cannot read is named rather than blank.</summary>
    [Fact]
    public void A_shape_set_gets_its_vocabulary_and_is_told_which_rig_it_is_missing() {
        using var session = EditorSession.Start();

        Write(session, "Assets/humanoid.vxshapevocab", "name: humanoid\nshapes:\n  - name: belly\n    meaning: The front of the torso.\n");

        Write(
            session,
            "Assets/hero.vxproxyshapes",
            "name: hero\nrig: Assets/Hero.gltf\nvocabulary: Assets/humanoid.vxshapevocab\n"
        );

        var document = Assert.IsType<ProxyShapeDocument>(Open(session, "Assets/hero.vxproxyshapes"));

        Assert.NotNull(document.Vocabulary);
        Assert.Equal("belly", Assert.Single(document.Vocabulary!.Shapes.ToArray()).Name.ToString());

        // There is no Hero.gltf, so there is no rig — and the audit says so rather than crashing.
        Assert.Null(document.Rig);
        Assert.Contains("No rig is bound", Assert.Single(document.Audit()).Message, StringComparison.Ordinal);
    }

    /// <summary>Editing the rig field is one undo entry, and the host looks again when it changes.</summary>
    /// <remarks>
    ///     ⚠ <b>The field has to be reachable from the panel.</b> A reference that only a text editor
    ///     could write would make "no rig is bound" a sentence with nothing to do about it — which is
    ///     the whole failure this asset was added to fix.
    /// </remarks>
    [Fact]
    public void Renaming_the_rig_is_undoable_and_is_noticed() {
        using var session = EditorSession.Start();

        Write(session, "Assets/hero.vxproxyshapes", "name: hero\n");

        var document = Assert.IsType<ProxyShapeDocument>(Open(session, "Assets/hero.vxproxyshapes"));

        var seen = 0;

        document.Changed += _ => seen++;
        document.SetField("Rig", static content => content.Rig, static (content, text) => content.Rig = text, "Assets/Hero.gltf");

        Assert.Equal("Assets/Hero.gltf", document.Set.Rig);
        Assert.Equal(1, seen);

        document.Stack.Undo();

        Assert.Equal(string.Empty, document.Set.Rig);
        Assert.Equal(2, seen);
    }

    /// <summary>A clip opened in the editor has a scene resolver, which is what Propose needs.</summary>
    [Fact]
    public void A_clip_has_somewhere_to_look_for_the_scene_it_was_marked_up_against() {
        using var session = EditorSession.Start();

        Write(session, "Assets/reach.vxanim", "name: Reach\nduration: 1.0\n");

        var document = Assert.IsType<AnimationClipDocument>(Open(session, "Assets/reach.vxanim"));

        Assert.NotNull(document.Scene);
        Assert.Empty(document.Propose());

        // ⚠ Not "no scene is bound", which is what an unwired document says. This clip names no
        // context, which is a statement about the clip.
        Assert.Contains("names no authoring context", document.ProposalError, StringComparison.Ordinal);
    }

    /// <summary>A harness plan opened in the editor is told which of the files it names went missing.</summary>
    [Fact]
    public void A_harness_plan_says_which_file_it_could_not_find() {
        using var session = EditorSession.Start();

        Write(session, "Assets/reach.vxharness", "name: reach\nclip: Assets/reach.vxanim\nrig: Assets/Hero.gltf\n");

        var document = Assert.IsType<HarnessDocument>(Open(session, "Assets/reach.vxharness"));

        Assert.NotNull(document.Resolve);
        Assert.False(document.TryRun(out var why));

        Assert.DoesNotContain("No project is bound", why, StringComparison.Ordinal);
        Assert.Contains("Assets/Hero.gltf", why, StringComparison.Ordinal);
    }

    // ── The resolver on its own ──────────────────────────────────────────────

    /// <summary>The link from a body to its rig is read off the shape set, and only off the set.</summary>
    [Fact]
    public void The_set_that_names_a_rig_is_the_one_found() {
        using var session = EditorSession.Start();

        Write(session, "Assets/hero.vxproxyshapes", "name: hero\nrig: Assets/Hero.gltf\n");
        Write(session, "Assets/goblin.vxproxyshapes", "name: goblin\nrig: Assets/Goblin.gltf\n");

        session.Project.Assets.Scan();

        var animation = new EditorAnimation(session.Project);

        Assert.Equal("Assets/goblin.vxproxyshapes", animation.ShapesFor("Assets/Goblin.gltf"));
        Assert.Equal(string.Empty, animation.ShapesFor("Assets/Nobody.gltf"));
        Assert.Equal(string.Empty, animation.ShapesFor(string.Empty));
    }

    /// <summary>
    ///     ⚠ <b>The places somebody modelled are the places watched.</b> Nothing else in a project
    ///     says which joints reach for things, and a shape is a named point an author wrote down.
    /// </summary>
    [Fact]
    public void An_effector_is_a_shape_and_its_chain_is_a_limb() {
        var rig = Rig();
        var shapes = ProxyShapeSet.Of(
            "hero",
            null,
            [
                Shape("belly", rig.IndexOf("Spine"), Vector3.Zero),
                Shape("palm_r", rig.IndexOf("Hand_r"), new(0f, -0.02f, 0f)),
                Shape("ground", 0, Vector3.Zero)
            ]
        );

        var effectors = EditorAnimation.Effectors(rig, shapes);

        // The root's shape is not an effector: it carries the body's volume and, once the scene has
        // been folded in, everything the scene put there.
        Assert.Equal(2, effectors.Count);

        var palm = Assert.Single(effectors, effector => effector.Joint == rig.IndexOf("Hand_r"));

        Assert.Equal(-0.02f, palm.Offset.Y, 3);

        // Two joints up from the hand is the root here, because this rig has three joints — which is
        // the fallback working rather than the chain being wrong.
        Assert.Equal(0, palm.Chain);
        Assert.Equal(rig.IndexOf("Spine"), Assert.Single(effectors, effector => effector.Joint == rig.IndexOf("Spine")).Joint);
    }

    /// <summary>Two shapes at one place on one joint are one effector, not two identical passes.</summary>
    [Fact]
    public void Shapes_sharing_a_joint_and_an_offset_are_one_effector() {
        var rig = Rig();
        var hand = rig.IndexOf("Hand_r");

        var shapes = ProxyShapeSet.Of(
            "hero",
            null,
            [Shape("palm_r", hand, Vector3.Zero), Shape("grip_r", hand, Vector3.Zero), Shape("tip_r", hand, new(0.05f, 0f, 0f))]
        );

        Assert.Equal(2, EditorAnimation.Effectors(rig, shapes).Count);
    }

    static ProxyShape Shape(string name, int joint, Vector3 offset) =>
        new() {
            Name = Symbol.Intern(name),
            Kind = ShapeKind.Sphere,
            Joint = joint,
            Offset = new(offset, Quaternion.Identity, Vector3.One),
            Dimensions = ShapeParams.Sphere(0.05f)
        };

    /// <summary>Writes a file under the session's project and returns where it landed.</summary>
    static string Write(EditorSession session, string relative, string content) {
        var absolute = session.Project.Paths.Absolute(relative);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);

        return absolute;
    }

    /// <summary>Opens an asset the way a double-click in the project browser does.</summary>
    static EditorDocument Open(EditorSession session, string relative) {
        session.Project.Assets.Scan();

        Assert.True(session.Project.Assets.TryGetByPath(relative, out var entry));

        session.Editor.OpenAsset(entry.Guid);
        session.Frame();

        Assert.True(session.Project.TryGetDocument(entry.Guid, out var document));

        return document;
    }

    static Skeleton Rig() {
        (string Name, int Parent, Vector3 Offset)[] joints = [
            ("Root", -1, Vector3.Zero),
            ("Spine", 0, new(0f, 0.9f, 0f)),
            ("Hand_r", 1, new(0.5f, 0.3f, 0f))
        ];

        var model = new Matrix4x4[joints.Length];
        var built = new SkeletonJoint[joints.Length];

        for (var index = 0; index < joints.Length; index++) {
            var local = Matrix4x4.FromTranslation(joints[index].Offset);

            model[index] = joints[index].Parent >= 0 ? local * model[joints[index].Parent] : local;

            Matrix4x4.Invert(model[index], out var inverse);

            built[index] = new() { Name = joints[index].Name, Parent = joints[index].Parent, InverseBindPose = inverse };
        }

        return Skeleton.Create(new() { Name = "hero", Joints = built });
    }
}
