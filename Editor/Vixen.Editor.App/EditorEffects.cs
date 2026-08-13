// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Graphics;
using Vixen.Rendering.Materials;
using Vixen.ShaderCompiler;
using Vixen.Shaders;

namespace Vixen.Editor.App;

/// <summary>Where the editor's shader variants come from.</summary>
/// <remarks>
///     <para>
///         <b>The tier <c>EffectSystem</c> was written expecting and nothing built.</b> Its own
///         remarks say a bundle provider "misses on a key the build did not enumerate, and the
///         compiler behind it answers", and <see cref="RavenEffectCompiler" />'s say "the editor
///         stacks a disk cache over one of these and gets compile-once-then-read without either of
///         them knowing about the other". This is that stack, assembled — no new machinery, and the
///         reason the editor could not be driven by a compositor was that nobody had written these
///         twenty lines.
///     </para>
///     <para>
///         ⚠ <b>Compiled rather than baked, which is the editor's answer and not a shipping
///         build's.</b> A shipping build enumerates its permutations and asserts the miss list is
///         empty; an editor cannot, because the next thing somebody does is add a material with a
///         shading model nothing has ever asked for. What that costs is a stall on the frame a new
///         variant is first needed, and what the disk cache buys is that it is the <em>first</em>
///         frame only, once per machine.
///     </para>
///     <para>
///         ⚠ <b>The cache is under the project's <c>Library/</c> and is keyed by a hash of the
///         sources.</b> Editing a shader changes the hash and invalidates exactly its variants —
///         which is <see cref="EffectDiskCache" />'s own contract — so a stale bytecode surviving an
///         edit is a state that cannot be reached by editing.
///     </para>
///     <para>
///         ⚠ <b>The engine's library ships beside the editor, and a project's own shaders are read
///         from <c>Assets/</c>.</b> Both are one compilation, for the reason <c>CheckShaders</c> gives
///         at length: a package's declarations are visible to a sibling file only within one, so a
///         project shader that imports <c>Vixen.Shaders.Core</c> needs the library's files in the same
///         compilation rather than a reference to a built artefact.
///     </para>
/// </remarks>
sealed class EditorEffects : IDisposable {
    /// <summary>Where the engine's shader library sits beside the editor.</summary>
    /// <remarks>
    ///     Copied into the output by the project file. A build that has not — a partial publish, a
    ///     `dotnet run` from a tree somebody pruned — is the case <see cref="Refusal" /> exists for.
    /// </remarks>
    public const string LibraryFolder = "Shaders/Library";

    readonly IGraphicsDevice device;
    readonly EditorProject project;

    /// <summary>The only provider <see cref="System" /> ever holds.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes <see cref="Rebuild" /> reach the things that resolve through this.</b> A
    ///     <see cref="EffectSystem" /> has no way to drop a provider, so a rebuild used to answer by
    ///     replacing the whole system — and every consumer captured the old one in its constructor.
    ///     <c>WorldRenderer</c> hands it to the render host, to both material features and to the
    ///     compositor builder, so a shader edit would recompile into a system nothing was asking, and
    ///     the editor would draw the pre-edit shader for ever with no miss, no refusal and no warning.
    /// </remarks>
    readonly Reloadable provider = new();

    bool disposed;

    /// <summary>Builds the chain over a project.</summary>
    /// <param name="device">The device the effects are created on.</param>
    /// <param name="project">Whose <c>Assets/</c> are searched and whose <c>Library/</c> caches.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public EditorEffects(IGraphicsDevice device, EditorProject project) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(project);

        this.device = device;
        this.project = project;

        System.AddProvider(provider);

        Build();
    }

    /// <summary>The system every material and every pass resolves through.</summary>
    /// <remarks>
    ///     ⚠ <b>One object for the life of the editor, and <see cref="Rebuild" /> does not replace
    ///     it.</b> Everything that resolves through it takes it once — a <c>WorldRenderer</c> passes it
    ///     to its render host, its two material features and its compositor builder in a constructor —
    ///     so a system swapped underneath them is a system nobody asks any more.
    /// </remarks>
    public EffectSystem System { get; } = new();

    /// <summary>Why there are no effects, or null when there are.</summary>
    /// <remarks>
    ///     ⚠ <b>A reason rather than an exception, because a viewport that cannot shade should still
    ///     open.</b> A missing library folder or a shader that does not parse leaves the editor with a
    ///     scene it cannot draw materials for — and an editor that refused to start would be one
    ///     nobody could use to fix the shader.
    /// </remarks>
    public string? Refusal { get; private set; }

    /// <summary>How many source files the compilation is over.</summary>
    public int SourceCount { get; private set; }

    /// <summary>Re-reads the sources, which is what a shader having been edited means.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A cleared <see cref="EffectSystem" /> rather than a new one</b> — see that property —
    ///         and the old effects are deliberately not destroyed here. A resolved <see cref="Effect" />
    ///         owns device objects a frame in flight may still be reading, and the editor has no fence
    ///         to say otherwise; what a rebuild costs is the memory of one generation of variants,
    ///         which is a few megabytes per shader edit and is reclaimed on exit.
    ///     </para>
    ///     <para>
    ///         <see cref="EffectSystem.Invalidate()" /> is what a hot reload is documented to do and
    ///         costs exactly the same memory: it drops the resolved table without touching what was in
    ///         it. What it keeps is the identity every consumer holds.
    ///     </para>
    /// </remarks>
    public void Rebuild() {
        ObjectDisposedException.ThrowIf(disposed, this);

        Build();
    }

    void Build() {
        var sources = Sources().ToList();

        SourceCount = sources.Count;

        // ⚠ Cleared before the compilation rather than after it, and both lines are needed. Dropping
        // the provider is what stops a variant asked for during the rebuild being answered out of the
        // old compiler; invalidating is what stops one that was already resolved being handed back
        // from memory without any provider being asked at all.
        provider.Source = null;
        System.Invalidate();

        if (sources.Count == 0) {
            Refusal = $"No shader sources: '{LibraryFolder}' is not beside the editor and the project has none.";

            return;
        }

        try {
            // ⚠ With the library's own defaults, and without them nothing compiles at all. A
            // compilation binds every tree it was given, so asking for `Tonemap` — which has no
            // slots — still has to satisfy `ForwardPlus.shading` and the eight of
            // `CompositeSurface`, because those files are in the same compilation. A key's own
            // bindings win over these, which is what keeps a material's chosen shading model its
            // own.
            var compiler = new RavenEffectCompiler(sources) { Composition = MaterialCompiler.PassComposition() };

            // ⚠ The cache in front of the compiler, not behind it. `EffectDiskCache` takes the
            // compiler as its own source, so a miss falls through, compiles and is written back —
            // one object rather than a chain a caller has to order correctly.
            var cache = new EffectDiskCache(
                Path.Combine(project.Paths.Library, "Shaders"),
                compiler.Target,
                compiler
            );

            provider.Source = new EffectSourceProvider(cache, new EffectLoader(device));

            Refusal = null;
        } catch (Exception failure) when (failure is ShaderCompilationException or IOException
                                              or UnauthorizedAccessException or ArgumentException) {
            // ⚠ Parsing happens in the constructor, so a project shader with a syntax error is met
            // here rather than on the frame that first wanted a variant of something else. That is
            // the right place for it — one message about the file, instead of every material in the
            // scene failing to resolve.
            Refusal = failure is ShaderCompilationException compilation
                ? string.Join(Environment.NewLine, compilation.Diagnostics)
                : failure.Message;
        }
    }

    /// <summary>Every <c>.rvn</c> the editor compiles: the engine's library, then the project's.</summary>
    /// <remarks>
    ///     ⚠ <b>Sorted, and the library first.</b> The order decides nothing about resolution — a
    ///     compilation is a set — but it decides the source hash, and a hash that depends on the file
    ///     system's enumeration order invalidates the whole disk cache on a machine that enumerates
    ///     differently.
    /// </remarks>
    IEnumerable<string> Sources() {
        var library = Path.Combine(AppContext.BaseDirectory, LibraryFolder.Replace('/', Path.DirectorySeparatorChar));

        if (Directory.Exists(library)) {
            // ⚠ The package directories, not the folder itself — which is how `LibraryReflectionTests`
            // enumerates it and for the same reason. `Example1.rvn` sits at the root and imports
            // `Vixen.Core` and `Vixen.BaseShaders`, neither of which is a package in this library:
            // it is a syntax sample rather than a shader, and including it fails every variant in the
            // compilation rather than only itself.
            foreach (var package in Directory.EnumerateDirectories(library).Order(StringComparer.Ordinal)) {
                foreach (var file in Directory.EnumerateFiles(package, "*.rvn", SearchOption.AllDirectories)
                             .Order(StringComparer.Ordinal)) {
                    yield return file;
                }
            }
        }

        if (!Directory.Exists(project.Paths.Assets)) {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(project.Paths.Assets, "*.rvn", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
            yield return file;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        provider.Source = null;
        System.Invalidate();
    }

    /// <summary>A provider whose own source can be replaced, which <see cref="EffectSystem" />'s cannot.</summary>
    /// <remarks>
    ///     ⚠ <b>Here rather than a <c>ClearProviders</c> on the system itself.</b> A shipping build adds
    ///     one provider and never removes it — that a runtime cannot change where effects come from is
    ///     part of what makes "no runtime shader compilation" structural — so the ability to swap one is
    ///     the editor's need and belongs in the editor.
    /// </remarks>
    sealed class Reloadable : IEffectProvider {
        /// <summary>What is asked, or null while there is nothing to ask.</summary>
        public IEffectProvider? Source { get; set; }

        /// <inheritdoc />
        public Effect? TryGet(EffectKey key) => Source?.TryGet(key);
    }
}
