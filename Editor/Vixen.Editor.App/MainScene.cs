// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Engine.Cameras;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.App;

/// <summary>The project's main scene: where it lives, and the starter scene a project that has none is given.</summary>
/// <remarks>
///     <para>
///         <b>Out of the application's constructor (#1293).</b> Reading the main scene or seeding,
///         saving and indexing a first one was a dozen constructor lines and a private <c>Seed</c>; it is
///         a fact about a project's first run, takes nothing of the composition but the document and the
///         project, and can be asked without a window.
///     </para>
///     <para>
///         ⚠ <b>The subsystems that declare component aliases are registered and touched before
///         <see cref="LoadOrSeed" /> runs, and that stays the application's ordering</b> — a scene file
///         naming a component nothing has loaded does not open at all.
///     </para>
/// </remarks>
static class MainScene {
    /// <summary>Where a project's main scene is, until there is a file dialog to choose another.</summary>
    /// <param name="paths">The project's paths.</param>
    /// <returns><c>Assets/Scenes/Main</c> with the scene extension.</returns>
    /// <remarks>
    ///     Decided here rather than by the document, because where a scene lives is the shell's answer
    ///     and a document only knows how to write itself.
    /// </remarks>
    public static string PathIn(ProjectPaths paths) => Path.Combine(paths.Assets, "Scenes", "Main" + SceneSerializer.Extension);

    /// <summary>Reads the main scene, or seeds, writes and indexes one when the file has nothing in it.</summary>
    /// <param name="scene">The document, whose writer is the file.</param>
    /// <param name="path">The file.</param>
    /// <param name="project">The project, rescanned after a first write.</param>
    /// <returns>Whether it was seeded.</returns>
    public static bool LoadOrSeed(SceneDocument scene, string path, EditorProject project) {
        if (SceneSerializer.Load(scene, path) != 0) {
            return false;
        }

        Seed(scene);

        // ⚠ Written immediately, which is the one time the editor saves without being asked. A
        // new project should contain the scene you are looking at rather than something that
        // exists only until the window closes — and it makes the *second* launch take the load
        // path, which is otherwise reachable only by remembering to press Save first.
        scene.Save();

        // ⚠ And scanned again, because the file was written *after* `Open` indexed the project.
        // Without this a first run shows a project browser with no scene in it — the one file the
        // editor is certain exists, because it just made it.
        project.Assets.Scan();

        return true;
    }

    /// <summary>A scene with something in it, for a project that has none yet.</summary>
    /// <remarks>
    ///     ⚠ <b>Only when there is no file to open.</b> A first run in an empty project opens
    ///     something rather than an empty tree and a viewport with nothing to look at; the moment
    ///     that scene is saved, this never runs again for that project.
    /// </remarks>
    static void Seed(SceneDocument scene) {
        var world = scene.World;
        var root = scene.Add("Scene Root", LocalTransform.Identity);

        // ⚠ Both carry the component their name claims, and until the component panel existed
        // neither did. A first run showing a "Directional Light" that is not a light and a "Main
        // Camera" that is not a camera was invisible while nothing drew what was on an entity; it is
        // the first thing somebody clicking those two rows now sees.
        var sun = scene.Add("Directional Light", LocalTransform.At(new Vector3(0f, 3f, 0f)) with {
            Rotation = SceneEditing.Aimed.Rotation
        }, root);

        Lights.Attach(world, sun, LightKind.Directional);

        var camera = scene.Add("Main Camera", LocalTransform.At(new Vector3(0f, 1.5f, 6f)), root);

        world.Add(camera, new Camera());

        var ground = scene.Add("Ground", LocalTransform.Identity, root);

        // ⚠ Two of these carry a shape, so a first run shows geometry rather than a grid and four
        // crosses — which is also the only exercise the mesh path gets without somebody having used
        // the menu, and so the way a broken one shows up on launch rather than on a click.
        Shape("Crate", PrimitiveKind.Cube, new Vector3(1.5f, 0.5f, 0f), Vector3.One, ground);
        Shape("Barrel", PrimitiveKind.Cylinder, new Vector3(-2f, 0.5f, 1f), new Vector3(0.8f, 1f, 0.8f), ground);

        // ⚠ Ground stays an empty, and the temptation is a scaled Plane. Two things go wrong with
        // one. Its children inherit the scale, so a crate at 1.5 metres under a ground scaled
        // twelvefold is eighteen metres away and twelve times too big; and the plane sits at exactly
        // the height the floor grid does, so it wins the depth test and the grid vanishes underneath
        // it. Both are correct behaviour and neither is what a first run should be a picture of.
        //
        // The stack starts empty: seeding is not an edit somebody made, and an editor that opened
        // with five undo steps already on it is one where Ctrl+Z does something inexplicable.
        scene.Stack.Clear();
        scene.Stack.MarkClean();

        void Shape(string name, PrimitiveKind kind, Vector3 position, Vector3 scale, Entity parent) {
            var entity = scene.Add(
                name,
                new LocalTransform { Position = position, Rotation = Quaternion.Identity, Scale = scale },
                parent
            );

            PrimitiveShapes.Attach(world, entity, kind);
        }
    }
}
