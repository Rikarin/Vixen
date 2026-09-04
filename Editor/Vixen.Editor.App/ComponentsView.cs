// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Runtime.CompilerServices;
using Vixen.Audio.Ecs;
using Vixen.Core;
using Vixen.Core.Reflection;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Cameras;
using Vixen.Engine.Scenes;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Terrain;
using Vixen.Ui;
using Vixen.Ui.Controls;
using PrefabSource = Vixen.Editor.AssetEditors.Prefabs.PrefabSource;

namespace Vixen.Editor.App;

/// <summary>What is on an entity, as a foldout each, with add and remove.</summary>
/// <remarks>
///     <para>
///         <b>The last of doc 20's B1 inspector row, and the reason it was last is that it is not a
///         drawer.</b> A drawer edits a member of a described type; this asks a different question —
///         <i>which</i> types are on this entity — and neither the ECS nor the inspector could answer
///         it. The ECS could not because an archetype knows dense ids handed out in first-touch
///         order, which mean nothing to a person; the inspector could not because no runtime
///         component carries <c>[Inspector]</c>, and none should, since that would be a runtime
///         assembly referencing an editor one.
///     </para>
///     <para>
///         Both halves are now answerable. <see cref="IComponentBridge" /> enumerates and asks; and
///         <see cref="ReflectedDescriptor" /> draws the rows from the <c>[DataContract]</c>
///         description the serializer already generates — so a game's components appear with nothing
///         asked of the game.
///     </para>
///     <para>
///         ⚠ <b>A component is read as a box, edited, and written back whole.</b> The child rows are
///         bound with no document, so they write into the copy and record nothing; this view is what
///         puts one <see cref="SetComponentCommand" /> on the stack. Recording each field instead
///         would put a step on the stack that undoes a change to a copy nobody can see, and the
///         visible change would belong to a different step.
///     </para>
///     <para>
///         <b>The panel is <c>ComponentsView.vxml</c>; this file is the half that is not the
///         panel.</b> <c>Default</c> and <c>Registered</c> answer <i>which components exist</i>,
///         which is a question about the editor's registries rather than about anything on screen —
///         and <c>CategoryOf</c> is the Add Component menu's filing rule, which the menu's own tests
///         call directly. None of the three reads an element.
///     </para>
/// </remarks>
sealed partial class ComponentsView {

    /// <summary>Where a behaviour is filed.</summary>
    /// <remarks>
    ///     Named rather than derived from the type's namespace like a component's, because a
    ///     behaviour's namespace is the game's own and "which of these is a script" is a question
    ///     people genuinely ask — it is the one distinction between the two kinds that survives being
    ///     a category rather than a heading.
    /// </remarks>
    internal const string Scripts = "Scripts";

    /// <summary>Where something with a namespace nobody can make a heading out of is filed.</summary>
    internal const string Other = "Other";

    /// <summary>Which group the picker files a component under.</summary>
    /// <param name="bridge">The component or behaviour.</param>
    /// <returns>The category's name.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>From the namespace, because there is nowhere else it could come from.</b> Nothing
    ///         on a component declares a category — <c>[Component]</c> is a layout and
    ///         <c>[DataContract]</c> is a serialiser's — and inventing an attribute for it would be an
    ///         attribute every game component has to remember, to serve a menu. The namespace is
    ///         already the thing an author grouped their code by, and it is right far more often than
    ///         a list in this file could be.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The last meaningful segment, not the first.</b> <c>Vixen.Engine.Cameras</c> filed
    ///         under "Engine" tells nobody anything — half the engine is under <c>Vixen.Engine</c> —
    ///         whereas "Cameras" is the heading somebody would have written. The plumbing segments go
    ///         first: a namespace ending in <c>Ecs</c> or <c>Components</c> is naming our storage
    ///         rather than their subject, and "Ecs" as a category heading is the filing cabinet
    ///         describing itself.
    ///     </para>
    /// </remarks>
    internal static string CategoryOf(IComponentBridge bridge) {
        ArgumentNullException.ThrowIfNull(bridge);

        if (bridge.Kind == AuthoringKind.Behavior) {
            return Scripts;
        }

        if (bridge.ComponentType.Namespace is not { Length: > 0 } space) {
            return Other;
        }

        var segments = space.Split('.').AsSpan();

        // The vendor prefix is not a heading either, and dropping it is what makes the engine's own
        // components file the same way a game's do.
        if (segments.Length > 1 && string.Equals(segments[0], "Vixen", StringComparison.Ordinal)) {
            segments = segments[1..];
        }

        while (segments.Length > 1 && Plumbing(segments[^1])) {
            segments = segments[..^1];
        }

        return segments.Length == 0 || Plumbing(segments[^1])
            ? Other
            : EditorNames.Humanise(segments[^1]);
    }

    static bool Plumbing(string segment) =>
        segment is "Ecs" or "Components" or "Component" or "Runtime" or "Core";

    /// <summary>The components the editor can show.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The loop and nothing else, which is the whole of what this method has to be.</b> A
    ///         component carrying <c>[Component]</c> and <c>[DataContract]</c> is declared to
    ///         <c>SceneComponentRegistry</c> by the engine's component generator, so it appears here —
    ///         and in the Add Component menu, in the <c>.vxscene</c> and in the compiled scene — with no
    ///         registration call and nothing added to this list. That holds for a game's own components
    ///         and for the engine's alike; <c>Light</c> and <c>PrimitiveShape</c> were hand-written
    ///         entries here until they became <c>Vixen.Rendering</c>'s, and their going is the
    ///         arrangement working rather than a special case being removed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A component is here only once its declaring assembly has been loaded</b>, because a
    ///         module initializer runs on assembly load. The editor references the subsystems it draws
    ///         for, and a project's own assemblies have to be loaded before this is asked — which is
    ///         also what makes a scene naming a component from an unloaded assembly fail at the load
    ///         with a message rather than silently.
    ///     </para>
    /// </remarks>
    /// <param name="behaviors">
    ///     Where the behaviours of whatever document the panel is showing live, asked for on each use
    ///     rather than captured — a store belongs to a document and the panel outlives any one of
    ///     them. A caller with no behaviours to show passes nothing and gets the components.
    /// </param>
    /// <param name="extensions">
    ///     What has been contributed, for the <see cref="AuthoringAssembly" /> declarations. A caller
    ///     with none gets whatever happens to be loaded, which is the pre-D5 behaviour and is what a
    ///     test constructing a bare panel wants.
    /// </param>
    public static IReadOnlyList<IComponentBridge> Default(
        Func<BehaviorStore?>? behaviors = null,
        IEditorRegistry? extensions = null
    ) {
        // ⚠ Every declared assembly, before the first read. A module initializer does not run until
        // something touches the module, and the registries are read during the editor's construction
        // — so without this the Add Component menu offered `Camera` and nothing else, because
        // `Vixen.Engine` was the only subsystem loaded by then and everything drawn in the viewport
        // arrived a second later.
        foreach (var declared in extensions?.All<AuthoringAssembly>() ?? []) {
            declared.Touch();
        }

        return new Registered(behaviors ?? (static () => null));
    }

    /// <summary>Everything the registry holds, re-read rather than remembered.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>Offer</c> says the list is "everything registered minus what is on this
    ///         entity" and that "both halves move" — and until this existed only the second one did.</b>
    ///         The bridges were built once into a <c>List</c> during the editor's construction, so a
    ///         component whose assembly loaded afterwards — a subsystem, a plugin, the project's own
    ///         code — could be in a scene, be drawn, and still not be in the menu.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One bridge per binder, kept.</b> A bridge is the key <c>working</c> and
    ///         <c>drawn</c> hold their boxes under, so handing out a fresh one per call would
    ///         make every foldout's box unreachable the moment the list was read again.
    ///     </para>
    /// </remarks>
    sealed class Registered(Func<BehaviorStore?> behaviors) : IReadOnlyList<IComponentBridge> {
        readonly Dictionary<Type, IComponentBridge> made = [];
        readonly List<IComponentBridge> bridges = [];

        public int Count {
            get {
                Sync();
                return bridges.Count;
            }
        }

        public IComponentBridge this[int index] {
            get {
                Sync();
                return bridges[index];
            }
        }

        public IEnumerator<IComponentBridge> GetEnumerator() {
            Sync();
            return bridges.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Brings the bridges into line with the registries, in both directions.</summary>
        /// <remarks>
        ///     <para>
        ///         ⚠ <b>Things are removed as well as added, which they did not used to be.</b> The
        ///         note here said an assembly that has loaded stays loaded for the life of the
        ///         process — true until the editor grew a collectible context for the project's own
        ///         code. A bridge over an evicted binder is worse than a missing one: it names a type
        ///         in an unloaded context, so it keeps that context alive and the menu offers a
        ///         component nothing can construct.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>Removal is decided by asking the registries, not by being told.</b> Eviction
        ///         happens in <c>ProjectAssemblies.Unload</c>, which knows nothing about panels — so
        ///         this compares rather than subscribing, on the same terms the rest of the editor
        ///         polls its selections.
        ///     </para>
        /// </remarks>
        void Sync() {
            Evict();

            foreach (var binder in SceneComponentRegistry.Binders) {
                if (made.ContainsKey(binder.ComponentType)) {
                    continue;
                }

                var bridge = new SceneComponentBridge(binder);

                made[binder.ComponentType] = bridge;
                bridges.Add(bridge);
            }

            // ⚠ And the behaviours, in the same list. Everything above `IComponentBridge` — the menu,
            // the foldouts, the drawers, the reorder — then works on both with nothing added to any
            // of it, which is the whole return on that interface having existed before there was a
            // second kind of thing to put behind it.
            foreach (var binder in SceneBehaviorRegistry.Binders) {
                if (made.ContainsKey(binder.BehaviorType)) {
                    continue;
                }

                var bridge = new BehaviorBridge(binder, behaviors);

                made[binder.BehaviorType] = bridge;
                bridges.Add(bridge);
            }
        }

        /// <summary>Drops the bridges whose binder is no longer registered.</summary>
        void Evict() {
            for (var index = bridges.Count - 1; index >= 0; index--) {
                var type = bridges[index].ComponentType;

                if (SceneComponentRegistry.TryGet(type, out _) || SceneBehaviorRegistry.TryGet(type, out _)) {
                    continue;
                }

                made.Remove(type);
                bridges.RemoveAt(index);
            }
        }
    }
}

