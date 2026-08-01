// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Editor.Core;
using Vixen.Engine.Behaviors;
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

    static string Generator(string name) =>
        Path.Combine(Repository, "Core", name, "bin", "Debug", "netstandard2.1", name + ".dll");

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
