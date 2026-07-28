// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;

namespace Vixen.Platform.Web;

/// <summary>Assemblies the publish step held back, fetched when something needs them.</summary>
/// <remarks>
///     <para>
///         <b>What this is for.</b> <c>docs/plan/10 § Web</c>: "930 KB is the runtime baseline; the
///         engine's own IL adds to it. Lazy assembly loading and splitting so a 2D/UI app does not
///         download the 3D renderer remain worthwhile." An application head names the assemblies it
///         is willing to wait for:
///     </para>
///     <code language="xml">
///     &lt;ItemGroup&gt;
///       &lt;VixenWebLazyAssembly Include="Vixen.Rendering" /&gt;
///     &lt;/ItemGroup&gt;
///     </code>
///     <para>
///         and <c>build/Vixen.Platform.Web.targets</c> takes each of them out of the boot manifest
///         and republishes it under <c>_lazy/</c>. Nothing downloads it until this does.
///     </para>
///     <para>
///         <b>Two consequences of how it is done, both worth knowing before deferring
///         something.</b> A deferred assembly is ordinary IL rather than the WebCIL the rest of the
///         payload uses, because <see cref="AssemblyLoadContext.LoadFromStream(Stream)" /> reads IL
///         and the runtime's own loader is what understands WebCIL. And it runs <em>interpreted</em>
///         even in an ahead-of-time-compiled build, exactly as Blazor's lazy loading does. Defer
///         subsystems that are large and cold; deferring a hot one buys download size and pays for
///         it every frame.
///     </para>
///     <para>
///         <b>A deferred assembly must still be referenced.</b> The trimmer runs before the split
///         and removes what nothing reaches — an assembly reached only through
///         <see cref="Assembly.Load(string)" /> is an assembly the trimmer deletes, and the publish
///         then fails saying so rather than shipping a page that 404s on first use. Reach it through
///         an interface in a referenced contracts assembly, or root it with
///         <c>TrimmerRootAssembly</c>.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     if (settings.EnableThreeD) {
///         await WebLazyAssemblies.LoadAsync("Vixen.Rendering");
///     }
///     </code>
/// </example>
[SupportedOSPlatform("browser")]
public static class WebLazyAssemblies {
    static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.Ordinal);

    /// <summary>Where the deferred assemblies are served from, relative to the page.</summary>
    /// <remarks>
    ///     Matches <c>VixenWebLazyAssemblyPath</c> in the targets. A head that moved them sets this
    ///     before the first <see cref="LoadAsync" />.
    /// </remarks>
    public static string BaseUrl { get; set; } = "_lazy/";

    /// <summary>The assemblies this has loaded, by simple name.</summary>
    public static IReadOnlyCollection<string> LoadedNames => Loaded.Keys;

    /// <summary>Whether an assembly has already been fetched.</summary>
    /// <param name="name">Its simple name, without an extension.</param>
    public static bool IsLoaded(string name) => Loaded.ContainsKey(name);

    /// <summary>Fetches and loads an assembly, or returns the one already loaded.</summary>
    /// <param name="name">Its simple name, without an extension — <c>"Vixen.Rendering"</c>.</param>
    /// <param name="cancellationToken">Abandons the fetch.</param>
    /// <returns>The assembly.</returns>
    /// <exception cref="FileNotFoundException">
    ///     Nothing was served at the expected URL, which means the publish did not defer this
    ///     assembly — check the name against <c>VixenWebLazyAssembly</c>.
    /// </exception>
    /// <remarks>
    ///     Idempotent, and cheap on the second call: a second load of the same bytes would produce a
    ///     second <see cref="Assembly" /> whose types are not the first one's, and code that then
    ///     cast between them would get an <see cref="InvalidCastException" /> naming the same type
    ///     twice — which is among the harder things to read in a stack trace.
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification =
            "LoadFromStream is annotated because a runtime-loaded assembly is normally invisible to "
            + "the trimmer, so its dependencies may have been removed. That is not the case here, "
            + "and the build enforces it: a deferred assembly is one the application references and "
            + "the trimmer kept — the publish step reads it out of the trimmer's own output, and "
            + "errors if the trimmer removed it. So the bytes loaded here were in the trim graph and "
            + "everything they need is in the payload. What the deferred assembly does with "
            + "reflection is its own trim problem, and is analysed where it is written."
    )]
    public static async Task<Assembly> LoadAsync(string name, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (Loaded.TryGetValue(name, out var already)) {
            return already;
        }

        // An assembly that is already in the process — because it was not deferred after all, or
        // because something loaded it first — is that one. Fetching a second copy would be the
        // duplicate-identity problem above, arrived at by a different route.
        var resident = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, name, StringComparison.Ordinal));

        if (resident is not null) {
            Loaded[name] = resident;
            return resident;
        }

        var url = BaseUrl + name + ".dll";
        int buffer;

        try {
            buffer = await WebInterop.FetchAssembly(url).WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            throw new FileNotFoundException(
                $"'{url}' could not be fetched. A deferred assembly is published there by the "
                + "VixenWebLazyAssembly item group; if this build did not list it, nothing put it "
                + "there.",
                url,
                exception
            );
        }

        var bytes = WebBuffer.Take(buffer);

        if (bytes.Length == 0) {
            throw new FileNotFoundException($"'{url}' was served empty.", url);
        }

        using var stream = new MemoryStream(bytes, writable: false);
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);

        Loaded[name] = assembly;
        return assembly;
    }

    /// <summary>Fetches several assemblies at once.</summary>
    /// <param name="names">Their simple names.</param>
    /// <param name="cancellationToken">Abandons the fetches.</param>
    /// <remarks>
    ///     Concurrently, which is the point: three assemblies fetched in sequence cost three round
    ///     trips, and a browser will happily have all three in flight over one connection.
    /// </remarks>
    public static async Task LoadAllAsync(
        IEnumerable<string> names,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(names);
        await Task.WhenAll(names.Select(name => LoadAsync(name, cancellationToken))).ConfigureAwait(false);
    }

    /// <summary>Finds a type in an assembly that has been loaded.</summary>
    /// <param name="assemblyName">The assembly's simple name.</param>
    /// <param name="typeName">The type's namespace-qualified name.</param>
    /// <param name="type">The type.</param>
    /// <returns><see langword="false" /> if the assembly is not loaded, or has no such type.</returns>
    /// <remarks>
    ///     <para>
    ///         The point at which the trimmer stops being able to help, which is why this is the
    ///         only reflection this class offers and why it is annotated as needing unreferenced
    ///         code: a type reached only by name is a type the trimmer has no reason to keep, and it
    ///         will not be there.
    ///     </para>
    ///     <para>
    ///         What to do instead: put an interface in a contracts assembly that <em>is</em>
    ///         referenced, have the deferred assembly implement it, and reach the implementation
    ///         through a factory the contracts assembly also declares. Then the deferred half is
    ///         reachable statically, the trimmer keeps what it should, and nothing here is needed.
    ///     </para>
    /// </remarks>
    [RequiresUnreferencedCode(
        "The type is named as a string, so the trimmer cannot see that it is used and may have "
        + "removed it. Prefer an interface in a referenced contracts assembly."
    )]
    public static bool TryGetType(
        string assemblyName,
        string typeName,
        [NotNullWhen(true)] out Type? type
    ) {
        type = Loaded.TryGetValue(assemblyName, out var assembly) ? assembly.GetType(typeName) : null;
        return type is not null;
    }
}
