// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Ui.Composition;
using Vixen.Ui.Desktop;
using Vixen.Ui.HotReload;

namespace Vixen.Ui.Desktop.HotReload;

/// <summary>Turns on every reload channel for every application in this process.</summary>
/// <remarks>
///     <para>
///         <b>Referencing this assembly is the whole of the opt-in.</b> There is no call to make and
///         no options to set: a module initializer fills the two hooks
///         <see cref="UiDevelopment" /> leaves open, and every <c>UiApplication</c> constructed
///         afterwards mounts its content under a reload host and gets a stylesheet watcher over its
///         own source directory.
///     </para>
///     <para>
///         ⚠ <b>An application that wants none of this does not reference it.</b> The reference is
///         conditioned on <c>Debug</c> in the sample and in the <c>vixen-app</c> template, so a
///         Release build does not resolve the assembly, the initializer never runs, and the hooks
///         stay null — which is the ordinary build. There is no runtime flag to get wrong and no
///         <c>#if</c> in anybody's <c>Main</c>.
///     </para>
///     <para>
///         ⚠ <b>An application that wants something else still wins.</b>
///         <c>UiApplicationOptions.Mount</c> is checked before <see cref="UiDevelopment.Mount" />, so
///         an application with its own idea of how to mount keeps it.
///     </para>
/// </remarks>
public static class DesktopHotReload {
    /// <summary>Every host this process made, so that the stylesheet watcher can find its own.</summary>
    /// <remarks>
    ///     ⚠ <b>Keyed by document, because a process may run more than one application</b> — the
    ///     sample does not, the editor's tests do — and the two hooks below are called at different
    ///     times with no argument in common but that. A <c>ConditionalWeakTable</c> rather than a
    ///     dictionary so that a closed application's host is collected with its document: the
    ///     runtime's own handler holds hosts weakly for the same reason, and a strong table here
    ///     would quietly undo it.
    /// </remarks>
    static readonly ConditionalWeakTable<UiDocument, HotReloadHost> Hosts = [];

    /// <summary>Whatever is watching each application's stylesheets, kept alive.</summary>
    /// <remarks>
    ///     ⚠ A <c>FileSystemWatcher</c> that nothing references is a watcher that stops raising
    ///     events at the next collection, which is a hot reload that works for a minute. Keyed the
    ///     same way, and weak the same way, so it goes when the document does.
    /// </remarks>
    static readonly ConditionalWeakTable<UiDocument, HotReloadWatcher> Watchers = [];

    /// <summary>Fills the hooks, once, before any application is constructed.</summary>
    /// <remarks>
    ///     ⚠ <b>A module initializer and not a static constructor.</b> Nothing in an application
    ///     names a type in this assembly — that is the point — so a static constructor would never
    ///     run. A module initializer runs when the assembly is loaded, and the assembly is loaded
    ///     because it is a reference.
    /// </remarks>
    // ⚠ **CA2255 is right about libraries in general and this is the case it excludes.** Its point is
    // that a library which runs code on load has surprised whoever referenced it — and that is
    // precisely the contract here: an application references this assembly *in order to* have
    // something happen, references it under a Debug condition, and writes no code because there is no
    // call to make. The alternative is an `EnableHotReload()` every application has to remember, in
    // an `#if DEBUG` every application has to write, which is the boilerplate this exists to remove.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install() {
        UiDevelopment.Mount = Mount;
        UiDevelopment.Started = Started;
    }

    /// <summary>Mounts an application's content through a reload host that tracks it.</summary>
    /// <remarks>
    ///     ⚠ <b>The factory is handed over rather than called here, and that is what makes a
    ///     re-created component keep its parameters.</b> An application writes
    ///     <c>Content = () =&gt; new Shell { Model = model }</c>; an edit the runtime cannot patch
    ///     makes the host construct a replacement, and this lambda is the only thing that knows how.
    ///     Without it the replacement comes up with every parameter at its default — and the reload
    ///     still reports success, so nothing says the panel is now bound to a model nothing holds.
    /// </remarks>
    static Component Mount(UiDocument document, UiElement root, Func<Component> content) {
        var host = new HotReloadHost(document);

        // ⚠ Held weakly by the runtime's handler, so something else has to hold it strongly or the
        // markup channel stops working at the first collection — with nothing to say so.
        MetadataUpdate.Register(host);
        Hosts.AddOrUpdate(document, host);

        return host.Mount(content, root);
    }

    /// <summary>Watches the application's own source directory for stylesheets.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The source tree, not the output directory.</b> <c>AppContext.BaseDirectory</c> is
    ///         <c>bin/Debug/net10.0</c>, where nobody edits anything — a watcher pointed there is
    ///         wired, correct at every step, and silent for ever. The project directory is found by
    ///         walking up for the <c>.csproj</c>, which is the one file that is certainly there and
    ///         certainly not in <c>bin</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every <c>.vcss</c> under it is loaded, and most of them will overlay rather than
    ///         replace.</b> <c>HotReloadWatcher.Load</c> binds a path to a sheet the document already
    ///         holds when the two texts match; a sheet the build concatenated into a generated one —
    ///         which is what a <c>VixenStyleBase</c> is — matches nothing, so a save layers on top.
    ///         Changing a rule works either way; *deleting* one only works when the path replaced.
    ///         <c>HotReloadWatcher.Replaces</c> is what says which, and the line printed below is
    ///         what puts it in front of somebody.
    ///     </para>
    /// </remarks>
    static void Started(UiApplication application) {
        if (Hosts.TryGetValue(application.Document, out var host) is false || Sources() is not { } directory) {
            return;
        }

        var watcher = new HotReloadWatcher(host, directory);
        Watchers.AddOrUpdate(application.Document, watcher);

        foreach (var sheet in Sheets(directory)) {
            watcher.Load(sheet);

            Console.WriteLine(
                watcher.Replaces(sheet)
                    ? $"vixen: watching {Relative(directory, sheet)} — a save replaces it, so deleting a rule takes effect"
                    : $"vixen: watching {Relative(directory, sheet)} — a save layers over the generated sheet, so a *deleted* rule keeps applying until the next build"
            );
        }

        // ⚠ Polled on the frame loop's own thread rather than acted on in the `FileSystemWatcher`
        // callback. The element tree has no lock and that callback is on a pool thread; `Poll` is
        // also what coalesces the three events one save raises — save-to-temp-then-rename, a
        // truncate followed by a write, a tool that touches the timestamp — into one reload.
        application.Frame += (_, _) => {
            foreach (var report in watcher.Poll()) {
                Console.WriteLine($"vixen: {report}");
            }
        };

        // ⚠ The markup channel reports through the host rather than through the watcher, because
        // nothing polls it: the runtime calls `MetadataUpdate` once `dotnet watch` has patched the
        // assembly. Printing it is the only way to tell a rebuild that reloaded from one that could
        // not — a `Build` that throws leaves the component empty, and an empty panel and an
        // unchanged panel look identical for the second it takes to notice.
        host.Reloaded += report => Console.WriteLine($"vixen: {report}");
    }

    /// <summary>The project's hand-written stylesheets, newest-looking first.</summary>
    /// <remarks>
    ///     ⚠ <b><c>obj</c> and <c>bin</c> are excluded, and leaving them in was not harmless.</b> The
    ///     utility step writes a generated <c>&lt;Project&gt;.g.vcss</c> under <c>obj</c> — one per
    ///     configuration — and that file is the concatenation the document actually holds, so it
    ///     matches by text and <c>Load</c> *binds* to it. Which sounds like an improvement and is a
    ///     trap: it is a build artefact, rewritten on every build, so every rebuild would fire a
    ///     reload of a file nobody edited, and the <c>obj/Release</c> copy would bind a sheet this
    ///     process is not even running.
    /// </remarks>
    static IEnumerable<string> Sheets(string directory) =>
        Directory.EnumerateFiles(directory, "*.vcss", SearchOption.AllDirectories)
            .Where(path => !Segments(directory, path).Any(
                segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            ));

    static string[] Segments(string directory, string path) =>
        Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>The project's own directory, if this is running out of a build of it.</summary>
    static string? Sources() {
        for (var walk = new DirectoryInfo(AppContext.BaseDirectory); walk is not null; walk = walk.Parent) {
            if (walk.EnumerateFiles("*.csproj").Any()) {
                return walk.FullName;
            }
        }

        return null;
    }

    static string Relative(string directory, string file) => Path.GetRelativePath(directory, file);
}
