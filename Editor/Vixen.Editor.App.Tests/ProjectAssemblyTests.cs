// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>A project's own code, compiled and loaded, declaring what it declares.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The seam that makes the other three about a game rather than about the engine.</b>
///         A behaviour was authorable only if it lived in an assembly the editor already referenced;
///         this is what puts a script somebody wrote this morning in the Add Component menu.
///     </para>
///     <para>
///         ⚠ <b>A real compiler over real source, not a stub.</b> Everything interesting here is
///         something a hand-built assembly would step over: whether the SDK is on PATH, whether
///         `--getProperty` says where the output went, whether the project's `Behavior` resolves to
///         the host's type rather than a same-named stranger from its own context, and whether a
///         module initializer ever runs. A fake would prove none of it.
///     </para>
/// </remarks>
public sealed class ProjectAssemblyTests : IDisposable {
    readonly string root = Path.Combine(
        Path.GetTempPath(),
        "vixen-project-assembly-" + Guid.NewGuid().ToString("N")[..8]
    );

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A build server holding a handle on something under here is not this test's business.
        }
    }

    /// <summary>Where the editor's own copies of the engine assemblies are.</summary>
    /// <remarks>
    ///     ⚠ The project is referenced against the *running* engine by path rather than against a
    ///     published `Vixen.Sdk` package, because a test cannot restore a package that is built from
    ///     the tree it is testing. What is being proved is the load, not the packaging.
    /// </remarks>
    static string EngineDirectory => Path.GetDirectoryName(typeof(Behavior).Assembly.Location)!;

    /// <summary>The repository, found from the engine assembly rather than from the test's own path.</summary>
    /// <remarks>
    ///     ⚠ <b>Needed because analyzers do not flow through a <c>&lt;Reference&gt;</c>.</b> A real
    ///     project gets the generators from <c>Vixen.Sdk</c>; a generated one referencing the built
    ///     assemblies by path gets the types and none of the code generation, so its behaviour would
    ///     compile, load, and declare nothing. Naming them here is what makes this test about the
    ///     same arrangement a project actually has.
    /// </remarks>
    static string Repository {
        get {
            var walk = new DirectoryInfo(EngineDirectory);

            while (walk is not null && !File.Exists(Path.Combine(walk.FullName, "Vixen.slnx"))) {
                walk = walk.Parent;
            }

            return walk?.FullName ?? throw new DirectoryNotFoundException("no repository above " + EngineDirectory);
        }
    }

    /// <summary>The configuration this tree was built in.</summary>
    /// <remarks>
    ///     ⚠ A generator is named by path, and the path carries the configuration. CI builds Release
    ///     and only Release, so a hard-coded <c>Debug</c> here names a file that was never produced and
    ///     every test in this class fails with <c>CS0006</c> on all three legs at once.
    /// </remarks>
    static string Configuration =>
        typeof(ProjectAssemblyTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()
            ?.Configuration
        ?? "Debug";

    static string Generator(string name) {
        var output = Path.Combine(Repository, "Core", name, "bin");
        var built = Path.Combine(output, Configuration, "netstandard2.1", name + ".dll");

        if (File.Exists(built) || !Directory.Exists(output)) {
            return built;
        }

        // A generator left behind by a build in the other configuration is the same code, and using
        // it beats failing a load test over which folder it landed in. The path built above is still
        // what gets returned when there is nothing at all, so the error names the expected place.
        foreach (var other in Directory.GetDirectories(output)) {
            var candidate = Path.Combine(other, "netstandard2.1", name + ".dll");

            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        return built;
    }

    string Write(string source) {
        Directory.CreateDirectory(root);

        File.WriteAllText(
            Path.Combine(root, "GameCode.csproj"),
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
                 <PropertyGroup>
                     <TargetFramework>net10.0</TargetFramework>
                     <Nullable>enable</Nullable>
                     <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
                 </PropertyGroup>
                 <ItemGroup>
                     <Analyzer Include="{Generator("Vixen.Core.Serialization.Generator")}" />
                     <Analyzer Include="{Generator("Vixen.Core.Reflection.Generator")}" />
                     <Analyzer Include="{Generator("Vixen.Engine.Generators")}" />
                 </ItemGroup>
                 <ItemGroup>
                     <!--
                         Every Vixen assembly beside the engine, rather than the three this source
                         names: the generated serializer and descriptor reach for Vixen.Core.
                         Serialization and Vixen.Core.Reflection, which the source never mentions and
                         a real project gets from the SDK.
                     -->
                     <Reference Include="$([System.IO.Directory]::GetFiles('{EngineDirectory}', 'Vixen.*.dll'))" />
                 </ItemGroup>
             </Project>
             """
        );

        File.WriteAllText(Path.Combine(root, "GameCode.cs"), source);
        return root;
    }

    [Fact]
    public void A_project_with_no_csproj_is_not_a_failure() {
        Directory.CreateDirectory(root);

        var assemblies = new ProjectAssemblies(new ProjectPaths(root));
        var built = assemblies.Reload();

        Assert.Null(built.Assembly);
        Assert.False(built.Failed);
    }

    /// <summary>
    ///     ⚠ <b>Two <c>.csproj</c> files is nothing rather than a guess.</b> Picking the first would
    ///     make "which of my scripts are loaded" depend on a file name.
    /// </summary>
    [Fact]
    public void Two_projects_in_one_root_is_no_project_at_all() {
        Write("public class Empty;");
        File.WriteAllText(Path.Combine(root, "Second.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        Assert.Null(new ProjectAssemblies(new ProjectPaths(root)).Project);
    }

    [Fact]
    public void A_behaviour_a_project_declares_reaches_the_registry() {
        Write(
            """
            using Vixen.Core;
            using Vixen.Engine.Behaviors;

            namespace GameCode;

            [DataContract("ProjectPatrol")]
            public sealed class ProjectPatrol : Behavior {
                public float Speed { get; set; } = 9f;
            }
            """
        );

        var assemblies = new ProjectAssemblies(new ProjectPaths(root));
        var built = assemblies.Reload();

        Assert.False(built.Failed, built.Output);
        Assert.NotNull(built.Assembly);

        // ⚠ Registered without anything having touched the type, which is the module initializer the
        // load runs by hand — the runtime defers one to first access, and nothing here accesses.
        Assert.True(SceneBehaviorRegistry.TryGet("ProjectPatrol", out var binder));
        Assert.Equal("ProjectPatrol", binder.Name);

        // ⚠ And its base is the *host's* `Behavior`. A context that loaded its own copy of
        // Vixen.Engine would produce a type whose base is a same-named stranger, and every cast in
        // the editor would fail with a message that reads like a lie.
        Assert.True(typeof(Behavior).IsAssignableFrom(binder.BehaviorType));

        // The binder can make one, which is what the Add Component menu does.
        Assert.IsAssignableFrom<Behavior>(binder.Create());
    }

    /// <summary>
    ///     ⚠ <b>A compiler error is reported, not swallowed.</b> A build that "just does not work" is
    ///     the failure mode this whole seam is most likely to have, and the output is what the console
    ///     panel shows.
    /// </summary>
    [Fact]
    public void A_project_that_does_not_compile_says_so_and_says_why() {
        Write("public class Broken { this is not C#; }");

        var built = new ProjectAssemblies(new ProjectPaths(root)).Reload();

        Assert.True(built.Failed);
        Assert.Null(built.Assembly);
        Assert.Contains("error", built.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     ⚠ <b>Unloading forgets, and forgetting is what makes the unload possible at all.</b> A
    ///     binder left in the registry names a type in the context being dropped, so it keeps that
    ///     context alive — the unload never completes, the next build cannot overwrite the file, and
    ///     the Add Component menu offers something nothing can construct.
    /// </summary>
    [Fact]
    public void Unloading_forgets_what_the_assembly_declared() {
        Write(
            """
            using Vixen.Core;
            using Vixen.Engine.Behaviors;

            namespace GameCode;

            [DataContract("EvictedPatrol")]
            public sealed class EvictedPatrol : Behavior { public float Speed { get; set; } = 1f; }
            """
        );

        var assemblies = new ProjectAssemblies(new ProjectPaths(root));

        Assert.False(assemblies.Reload().Failed);
        Assert.True(SceneBehaviorRegistry.TryGet("EvictedPatrol", out _));

        Assert.True(assemblies.Unload());
        Assert.False(SceneBehaviorRegistry.TryGet("EvictedPatrol", out _));

        // Asking twice is not an error — an editor closing a project it never opened code for.
        Assert.False(assemblies.Unload());
    }

    /// <summary>
    ///     ⚠ <b>The values a designer typed survive a rebuild, on an instance of the new type.</b>
    ///     That is the whole difference between a reload somebody can use and one that quietly resets
    ///     the scene — the instance cannot cross, so its state does: bytes out through the old
    ///     binder, bytes in through the new one, joined by an alias that is the same on both sides.
    /// </summary>
    [Fact]
    public void An_authored_value_crosses_a_rebuild() {
        Write(
            """
            using Vixen.Core;
            using Vixen.Engine.Behaviors;

            namespace GameCode;

            [DataContract("CrossingPatrol")]
            public sealed class CrossingPatrol : Behavior { public float Speed { get; set; } = 1f; }
            """
        );

        var assemblies = new ProjectAssemblies(new ProjectPaths(root));

        Assert.False(assemblies.Reload().Failed);
        Assert.True(SceneBehaviorRegistry.TryGet("CrossingPatrol", out var before));

        // What a designer typed into the inspector.
        var authored = before.Create();

        before.BehaviorType.GetProperty("Speed")!.SetValue(authored, 41.5f);

        var state = before.Save(authored);

        // The source changes and the project is rebuilt — a new field, which is the ordinary edit.
        File.WriteAllText(
            Path.Combine(root, "GameCode.cs"),
            """
            using Vixen.Core;
            using Vixen.Engine.Behaviors;

            namespace GameCode;

            [DataContract("CrossingPatrol")]
            public sealed class CrossingPatrol : Behavior {
                public float Speed { get; set; } = 1f;
                public float Distance { get; set; } = 7f;
            }
            """
        );

        Assert.False(assemblies.Reload().Failed);
        Assert.True(SceneBehaviorRegistry.TryGet("CrossingPatrol", out var after));

        // A different type, from a different context — the old one is gone.
        Assert.NotSame(before.BehaviorType, after.BehaviorType);

        var restored = after.Restore(state);

        Assert.Equal(41.5f, after.BehaviorType.GetProperty("Speed")!.GetValue(restored));

        // ⚠ And the field the rebuild added keeps its constructor's value rather than a zero. A
        // reload that refused, or that zeroed everything it did not recognise, would be one nobody
        // could use while actually editing.
        Assert.Equal(7f, after.BehaviorType.GetProperty("Distance")!.GetValue(restored));
    }

    /// <summary>
    ///     ⚠ <b>A rebuild that fails keeps the previous build's types (#1490).</b> The old assembly is
    ///     unloaded before the compiler runs, so a syntax error typed into a script that had been working
    ///     used to take every one of its behaviours out of the registries — and with them, out of the scene.
    /// </summary>
    [Fact]
    public void A_rebuild_that_fails_keeps_the_previous_builds_types() {
        Write(Declaring("KeptPatrol"));

        var assemblies = new ProjectAssemblies(new ProjectPaths(root));
        var first = assemblies.Reload();

        Assert.False(first.Failed, first.Output);
        Assert.True(SceneBehaviorRegistry.TryGet("KeptPatrol", out var before));

        File.WriteAllText(Path.Combine(root, "GameCode.cs"), "public class Broken { this is not C#; }");

        var second = assemblies.Reload();

        // Failed, and said why — and still holding what the last good build declared.
        Assert.True(second.Failed);
        Assert.Contains("error", second.Output, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(second.Assembly);
        Assert.Same(second.Assembly, assemblies.Loaded);
        Assert.True(SceneBehaviorRegistry.TryGet("KeptPatrol", out var after), "the failed rebuild took the last good build's types with it");

        // From a fresh context: the old one was unloaded before the compiler ran, as it has to be.
        Assert.NotSame(before.BehaviorType, after.BehaviorType);
    }

    /// <summary>
    ///     ⚠ <b>An authored project behaviour, and its values, survive a rebuild that fails — and when the
    ///     last good build is gone too, they are held rather than dropped, and the scene refuses to be
    ///     saved without them (#1490).</b>
    /// </summary>
    /// <remarks>
    ///     The last build is the user fixing the syntax error: what was held goes back on, with the value
    ///     a designer typed before any of this happened.
    /// </remarks>
    [Fact]
    public void A_failed_rebuild_never_takes_an_authored_behaviour_out_of_the_scene() {
        Write(Declaring("HeldPatrol"));
        Directory.CreateDirectory(Path.Combine(root, "Assets"));

        var project = new EditorProject(new ProjectPaths(root));
        using var world = new World("Scene");
        var file = Path.Combine(root, "Assets", "Level.vxscene");
        var scene = new SceneDocument(project, world, AssetId.Empty, "Level") { Writer = new SceneFileWriter(file) };
        List<string> said = [];

        using var session = new ProjectSession(
            project,
            (title, _, _) => said.Add(title),
            () => { },
            () => false,
            new ProjectHistory(Path.Combine(root, "Data"))
        );

        Assert.True(session.BuildCode(), string.Join(Environment.NewLine, said));

        var entity = scene.Add("Guard", LocalTransform.Identity);

        Assert.True(SceneBehaviorRegistry.TryGet("HeldPatrol", out var binder));

        var authored = binder.Create();

        binder.BehaviorType.GetProperty("Speed")!.SetValue(authored, 41.5f);
        binder.AttachTo(scene.Behaviors, entity, authored);
        scene.Save();

        var saved = File.ReadAllText(file);

        Assert.Contains("!HeldPatrol", saved, StringComparison.Ordinal);

        // A syntax error: the last good build is still on disk and is what comes back.
        File.WriteAllText(Path.Combine(root, "GameCode.cs"), "public class Broken { this is not C#; }");

        Assert.False(session.BuildCode());
        Assert.Equal(41.5f, Speed(Assert.Single(scene.Behaviors.AllOn(entity).ToArray())));

        // And again with the last good build deleted, so there is nothing at all to put it back on.
        Directory.Delete(Path.Combine(root, "bin"), recursive: true);
        said.Clear();

        Assert.False(session.BuildCode());
        Assert.Empty(scene.Behaviors.AllOn(entity).ToArray());
        Assert.Contains(said, title => title.Contains("held", StringComparison.OrdinalIgnoreCase));

        // ⚠ The save path: a scene written now would be written without it, so it is refused, and the
        // file on disk is still the one that names the behaviour.
        Assert.Throws<IOException>(scene.Save);
        Assert.Equal(saved, File.ReadAllText(file));

        File.WriteAllText(Path.Combine(root, "GameCode.cs"), Declaring("HeldPatrol"));

        Assert.True(session.BuildCode(), string.Join(Environment.NewLine, said));
        Assert.Equal(41.5f, Speed(Assert.Single(scene.Behaviors.AllOn(entity).ToArray())));

        scene.Save();
        Assert.Contains("!HeldPatrol", File.ReadAllText(file), StringComparison.Ordinal);

        static float Speed(Behavior behavior) => (float)behavior.GetType().GetProperty("Speed")!.GetValue(behavior)!;
    }

    /// <summary>
    ///     ⚠ <b>Every open scene keeps its project behaviours across a rebuild, not only the active one
    ///     (#1496).</b> The take-off ran over the active scene alone, so a scene opened beside it kept
    ///     instances of the unloaded types: the next save wrote it without them, because the registry no
    ///     longer knew the old type, and the instances held the old context alive.
    /// </summary>
    /// <remarks>
    ///     Both scenes share one world, as the editor's additively opened scenes do. A rebuild that works
    ///     must put an instance of the <i>rebuilt</i> type on each — the value alone would pass against the
    ///     defect, since the stale instance still has it — and a rebuild with nothing to fall back on
    ///     holds both and refuses to save either.
    ///     ⚠ <b>And the old context is collected afterwards (#1502).</b> It was not, with one scene or
    ///     two: <c>BehaviorStore</c> kept the emptied bucket keyed by the old type, so the take-off this
    ///     test proves was undone by the store, and every rebuild with a project behaviour in a scene
    ///     leaked one assembly. The probe was written with #1496 and dropped because of that defect.
    /// </remarks>
    [Fact]
    public void A_rebuild_keeps_the_project_behaviours_of_every_open_scene() {
        Write(Declaring("TwinPatrol"));
        Directory.CreateDirectory(Path.Combine(root, "Assets"));

        var project = new EditorProject(new ProjectPaths(root));
        using var world = new World("Scenes");
        var files = new[] { Path.Combine(root, "Assets", "Active.vxscene"), Path.Combine(root, "Assets", "Beside.vxscene") };
        var scenes = files.Select(file => new SceneDocument(project, world, AssetId.Empty, Path.GetFileNameWithoutExtension(file)) { Writer = new SceneFileWriter(file) }).ToArray();
        List<string> said = [];

        using var session = new ProjectSession(
            project,
            (title, _, _) => said.Add(title),
            () => { },
            () => false,
            new ProjectHistory(Path.Combine(root, "Data"))
        );

        Assert.True(session.BuildCode(), string.Join(Environment.NewLine, said));

        var entities = scenes.Select((scene, index) => Author(scene, 10f + index)).ToArray();
        var first = ContextOf("TwinPatrol");

        // A rebuild that works: a new field, the ordinary edit.
        var laps = Declaring("TwinPatrol").Replace("Speed { get; set; } = 1f;", "Speed { get; set; } = 1f; public int Laps { get; set; }", StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(root, "GameCode.cs"), laps);

        Assert.True(session.BuildCode(), string.Join(Environment.NewLine, said));
        Assert.True(SceneBehaviorRegistry.TryGet("TwinPatrol", out var rebuilt));

        for (var index = 0; index < scenes.Length; index++) {
            var kept = Assert.Single(scenes[index].Behaviors.AllOn(entities[index]).ToArray());

            Assert.Same(rebuilt.BehaviorType, kept.GetType());
            Assert.Equal(10f + index, Speed(kept));

            scenes[index].Save();
            Assert.Contains("!TwinPatrol", File.ReadAllText(files[index]), StringComparison.Ordinal);
        }

        // Bounded by attempts rather than by a clock, as `ProjectCoroutineUnloadTests` is.
        for (var attempt = 0; attempt < 10 && first.IsAlive; attempt++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(first.IsAlive, "the first build's load context was not collected after the rebuild put its behaviours back");

        // A rebuild with nothing to fall back on: both are held, and neither scene will save.
        File.WriteAllText(Path.Combine(root, "GameCode.cs"), "public class Broken { this is not C#; }");
        Directory.Delete(Path.Combine(root, "bin"), recursive: true);

        Assert.False(session.BuildCode());

        for (var index = 0; index < scenes.Length; index++) {
            var saved = File.ReadAllText(files[index]);

            Assert.Empty(scenes[index].Behaviors.AllOn(entities[index]).ToArray());
            Assert.Throws<IOException>(scenes[index].Save);
            Assert.Equal(saved, File.ReadAllText(files[index]));
        }

        File.WriteAllText(Path.Combine(root, "GameCode.cs"), laps);

        Assert.True(session.BuildCode(), string.Join(Environment.NewLine, said));

        for (var index = 0; index < scenes.Length; index++) {
            Assert.Equal(10f + index, Speed(Assert.Single(scenes[index].Behaviors.AllOn(entities[index]).ToArray())));

            scenes[index].Save();
            Assert.Contains("!TwinPatrol", File.ReadAllText(files[index]), StringComparison.Ordinal);
        }

        static Entity Author(SceneDocument scene, float speed) {
            var entity = scene.Add("Guard", LocalTransform.Identity);

            Assert.True(SceneBehaviorRegistry.TryGet("TwinPatrol", out var binder));

            var authored = binder.Create();

            binder.BehaviorType.GetProperty("Speed")!.SetValue(authored, speed);
            binder.AttachTo(scene.Behaviors, entity, authored);
            scene.Save();

            return entity;
        }

        static float Speed(Behavior behavior) => (float)behavior.GetType().GetProperty("Speed")!.GetValue(behavior)!;
    }

    /// <summary>
    ///     ⚠ <b>A rebuild that removes a member from one behaviour puts every behaviour back (#1503).</b>
    ///     The state crossed the rebuild as the binary serializer's bytes, which are positional and
    ///     refuse data holding more members than the build knows — so removing a property, an
    ///     ordinary edit, threw out of <c>BuildCode</c> partway through the restore, and every
    ///     behaviour after the failing one was neither put back nor held, so the next save wrote the
    ///     scene without them.
    /// </summary>
    /// <remarks>
    ///     Three behaviours, the middle one losing a member from the <i>middle</i> of its list, so a
    ///     read that bound by position would also put <c>Turns</c>' value in the wrong place. All three
    ///     come back on the rebuilt types with their values, the removed member is said by name, and the
    ///     scene saves with all three.
    /// </remarks>
    [Fact]
    public void A_rebuild_that_removes_a_member_puts_every_behaviour_back() {
        const string before = """
            using Vixen.Core;
            using Vixen.Engine.Behaviors;

            namespace GameCode;

            [DataContract("TrioFirst")]
            public sealed class TrioFirst : Behavior { public float Speed { get; set; } = 1f; }

            [DataContract("TrioMiddle")]
            public sealed class TrioMiddle : Behavior {
                public float Speed { get; set; } = 1f;
                public int Laps { get; set; }
                public int Turns { get; set; }
            }

            [DataContract("TrioLast")]
            public sealed class TrioLast : Behavior { public float Speed { get; set; } = 1f; }
            """;

        Write(before);
        Directory.CreateDirectory(Path.Combine(root, "Assets"));

        var project = new EditorProject(new ProjectPaths(root));
        using var world = new World("Scenes");
        var file = Path.Combine(root, "Assets", "Trio.vxscene");
        var scene = new SceneDocument(project, world, AssetId.Empty, "Trio") { Writer = new SceneFileWriter(file) };
        List<string> said = [];

        using var session = new ProjectSession(
            project,
            (title, _, detail) => said.Add(title + ": " + detail),
            () => { },
            () => false,
            new ProjectHistory(Path.Combine(root, "Data"))
        );

        Assert.True(session.BuildCode(), string.Join(Environment.NewLine, said));

        var entity = scene.Add("Guard", LocalTransform.Identity);

        foreach (var (alias, speed) in new[] { ("TrioFirst", 11f), ("TrioMiddle", 12f), ("TrioLast", 13f) }) {
            Assert.True(SceneBehaviorRegistry.TryGet(alias, out var binder));

            var authored = binder.Create();

            binder.BehaviorType.GetProperty("Speed")!.SetValue(authored, speed);

            if (alias == "TrioMiddle") {
                binder.BehaviorType.GetProperty("Laps")!.SetValue(authored, 7);
                binder.BehaviorType.GetProperty("Turns")!.SetValue(authored, 9);
            }

            binder.AttachTo(scene.Behaviors, entity, authored);
        }

        scene.Save();
        said.Clear();

        File.WriteAllText(Path.Combine(root, "GameCode.cs"), before.Replace("public int Laps { get; set; }", string.Empty, StringComparison.Ordinal));

        Assert.True(session.BuildCode(), string.Join(Environment.NewLine, said));

        var back = scene.Behaviors.AllOn(entity).ToArray().ToDictionary(behavior => behavior.GetType().Name);

        Assert.Equal(3, back.Count);
        Assert.Equal(11f, Read<float>(back["TrioFirst"], "Speed"));
        Assert.Equal(12f, Read<float>(back["TrioMiddle"], "Speed"));
        Assert.Equal(9, Read<int>(back["TrioMiddle"], "Turns"));
        Assert.Equal(13f, Read<float>(back["TrioLast"], "Speed"));
        Assert.Null(back["TrioMiddle"].GetType().GetProperty("Laps"));

        foreach (var (name, behavior) in back) {
            Assert.True(SceneBehaviorRegistry.TryGet(name, out var rebuilt));
            Assert.Same(rebuilt.BehaviorType, behavior.GetType());
        }

        // Said, by name: the value is gone from the scene the next save writes.
        Assert.Contains(said, line => line.Contains("TrioMiddle", StringComparison.Ordinal) && line.Contains("laps", StringComparison.OrdinalIgnoreCase));

        scene.Save();

        var saved = File.ReadAllText(file);

        Assert.Contains("!TrioFirst", saved, StringComparison.Ordinal);
        Assert.Contains("!TrioMiddle", saved, StringComparison.Ordinal);
        Assert.Contains("!TrioLast", saved, StringComparison.Ordinal);

        static T Read<T>(Behavior behavior, string member) => (T)behavior.GetType().GetProperty(member)!.GetValue(behavior)!;
    }

    /// <summary>
    ///     ⚠ <b>A script saved on disk rebuilds the project's code through the editor's own frame loop,
    ///     with the scene's behaviours carried — and not while the game is playing (#1508).</b>
    ///     <c>BuildCode</c>'s only production caller was the first frame, so the whole rebuild path —
    ///     #1490, #1496, #1502, #1503 — was reachable from tests and from nothing a user could do.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The real watcher, the real compiler and the real frame loop: nothing here calls
    ///         <c>BuildCode</c> after the first build, which stands in for the first frame's (the harness
    ///         does not greet). What is asserted is a count of builds and the values on the rebuilt type;
    ///         the clocks are hang checks on the watcher's debounce and the compiler, not bounds.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Play holds a rebuild back rather than dropping it.</b> Unloading the assembly under a
    ///         running game would take its behaviours out from under the step, so an edit saved while
    ///         playing waits for the game to stop, and is built then — once.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_script_saved_on_disk_rebuilds_the_projects_code_with_its_behaviours_carried() {
        Write(Declaring("WatchedPatrol"));

        using var editor = Testing.EditorSession.Start(new() { ProjectRoot = root, WatchAssets = true });

        var session = editor.Plugins.Services.Require<IProjectSession>();
        var built = 0;

        session.CodeBuilt += () => built++;

        // The first frame's build, which the harness does not run for itself.
        Assert.True(session.BuildCode());
        Assert.Equal(1, built);

        var scene = editor.Scene;
        var entity = scene.Add("Guard", LocalTransform.Identity);

        Assert.True(SceneBehaviorRegistry.TryGet("WatchedPatrol", out var first));

        var authored = first.Create();

        first.BehaviorType.GetProperty("Speed")!.SetValue(authored, 41.5f);
        first.AttachTo(scene.Behaviors, entity, authored);

        // The build's own writes under `bin/` and `obj/` are drained before anything is counted: an
        // editor that rebuilt on its own output would rebuild for ever.
        Pump(editor, () => false, TimeSpan.FromSeconds(1.5));
        Assert.Equal(1, built);

        // An ordinary edit — a new member — saved by another program.
        File.WriteAllText(
            Path.Combine(root, "GameCode.cs"),
            Declaring("WatchedPatrol").Replace("Speed { get; set; } = 1f;", "Speed { get; set; } = 1f; public int Laps { get; set; } = 3;", StringComparison.Ordinal)
        );

        Assert.True(Pump(editor, () => built == 2, TimeSpan.FromMinutes(2)), "a script saved on disk never rebuilt the project's code");

        Assert.True(SceneBehaviorRegistry.TryGet("WatchedPatrol", out var second));
        Assert.NotSame(first.BehaviorType, second.BehaviorType);

        var kept = Assert.Single(scene.Behaviors.AllOn(entity).ToArray());

        Assert.Same(second.BehaviorType, kept.GetType());
        Assert.Equal(41.5f, (float)kept.GetType().GetProperty("Speed")!.GetValue(kept)!);
        Assert.Equal(3, (int)kept.GetType().GetProperty("Laps")!.GetValue(kept)!);

        // And the build's own output again: still exactly one rebuild for one edit.
        Pump(editor, () => false, TimeSpan.FromSeconds(1.5));
        Assert.Equal(2, built);

        // ⚠ While playing, an edit is held, not built.
        var play = editor.Plugins.Services.Require<IPlaySession>();

        Assert.True(play.Enter());

        File.WriteAllText(
            Path.Combine(root, "GameCode.cs"),
            Declaring("WatchedPatrol").Replace("Speed { get; set; } = 1f;", "Speed { get; set; } = 1f; public int Laps { get; set; } = 4;", StringComparison.Ordinal)
        );

        Pump(editor, () => false, TimeSpan.FromSeconds(2));
        Assert.Equal(2, built);

        play.Leave();

        Assert.True(Pump(editor, () => built == 3, TimeSpan.FromMinutes(2)), "an edit saved during play was never built after it");

        Pump(editor, () => false, TimeSpan.FromSeconds(1.5));
        Assert.Equal(3, built);
    }

    /// <summary>Frames until a condition holds or the budget runs out, sleeping between so the watcher's debounce can pass.</summary>
    static bool Pump(Testing.EditorSession editor, Func<bool> until, TimeSpan budget) {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (clock.Elapsed < budget) {
            editor.Frames(1);

            if (until()) {
                return true;
            }

            // ⚠ Real time, because the thing being waited on is a debounce measured in it.
            Thread.Sleep(25);
        }

        return until();
    }

    /// <summary>A weak handle on the load context the alias's current type was loaded into.</summary>
    /// <remarks>
    ///     ⚠ Not inlined, so neither the binder nor the type is a live local of the caller's frame when
    ///     it collects — a register holding one would report a leak for a fix that works.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference ContextOf(string alias) {
        Assert.True(SceneBehaviorRegistry.TryGet(alias, out var binder));

        var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(binder.BehaviorType.Assembly);

        Assert.NotNull(context);
        Assert.True(context.IsCollectible, "the project's assembly is not in a collectible context");

        return new WeakReference(context);
    }

    /// <summary>A code build redraws the inspector over the rebuilt type and restates the build panel, which follow it themselves.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two readers subscribe to <c>IProjectSession.CodeBuilt</c> themselves (#1293)</b>, where
    ///         the constructor used to forward it to both. A behaviour already on the inspected entity is the
    ///         case that needs the inspector's half: the section's rows were built over the first build's
    ///         binder, so a member the second build added is invisible until something rebuilds them.
    ///     </para>
    ///     <para>
    ///         Counted on the published <c>IPlayerDelivery.Changed</c> straight after each build, before a
    ///         frame runs, so a restate from the content tasks' busy flag cannot stand in for it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_code_build_rebuilds_the_inspector_and_restates_the_build_panel() {
        Write(Declaring("InspectedPatrol"));

        using var editor = Testing.EditorSession.Start(new() { ProjectRoot = root });

        var session = editor.Plugins.Services.Require<IProjectSession>();
        var delivery = editor.Plugins.Services.Require<IPlayerDelivery>();
        var restated = 0;

        delivery.Changed += () => restated++;

        Assert.True(session.BuildCode());
        Assert.Equal(1, restated);

        var scene = editor.Scene;
        var entity = scene.Add("Guard", LocalTransform.Identity);

        Assert.True(SceneBehaviorRegistry.TryGet("InspectedPatrol", out var first));
        first.AttachTo(scene.Behaviors, entity, first.Create());

        editor.ClickRow(editor.Hierarchy, "Guard");
        _ = editor.Inspector;
        editor.Settle();

        Assert.Contains("Speed", Texts(editor.Panel("inspector")));
        Assert.DoesNotContain("Laps", Texts(editor.Panel("inspector")));

        File.WriteAllText(
            Path.Combine(root, "GameCode.cs"),
            Declaring("InspectedPatrol").Replace("Speed { get; set; } = 1f;", "Speed { get; set; } = 1f; public int Laps { get; set; } = 3;", StringComparison.Ordinal)
        );

        Assert.True(session.BuildCode());
        Assert.Equal(2, restated);

        editor.Settle();

        Assert.Contains("Laps", Texts(editor.Panel("inspector")));
    }

    static IEnumerable<string> Texts(Vixen.Ui.UiElement element) {
        if (element.Text is { Length: > 0 } text) {
            yield return text;
        }

        foreach (var child in element.Children) {
            foreach (var found in Texts(child)) {
                yield return found;
            }
        }
    }

    static string Declaring(string alias) =>
        $$"""
          using Vixen.Core;
          using Vixen.Engine.Behaviors;

          namespace GameCode;

          [DataContract("{{alias}}")]
          public sealed class {{alias}} : Behavior { public float Speed { get; set; } = 1f; }
          """;

    /// <summary>The assembly is loaded into a collectible context of its own, not into the host's.</summary>
    [Fact]
    public void The_project_is_loaded_beside_the_editor_rather_than_into_it() {
        Write("public sealed class Ordinary { public int Value { get; set; } }");

        var built = new ProjectAssemblies(new ProjectPaths(root)).Reload();

        Assert.False(built.Failed, built.Output);

        var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(built.Assembly!);

        Assert.NotNull(context);
        Assert.True(context.IsCollectible, "the project's assembly is not in a collectible context");
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, context);
    }
}
