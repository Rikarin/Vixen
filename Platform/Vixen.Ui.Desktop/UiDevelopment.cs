// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;

namespace Vixen.Ui.Desktop;

/// <summary>Where a development-only assembly hooks itself into every application in the process.</summary>
/// <remarks>
///     <para>
///         <b>So that hot reload is a project reference and not a paragraph of bootstrap in every
///         application's <c>Main</c>.</b> The wiring a `.vxml` reload needs is real — a
///         <c>HotReloadHost</c> has to be tracking the component at the moment it is mounted, it has
///         to be registered with the runtime's metadata-update handler, and a stylesheet watcher has
///         to be pointed at the source directory — but none of it varies between applications, and
///         an application that gets one line of it wrong gets a reload that silently does nothing.
///     </para>
///     <para>
///         ⚠ <b>Two static properties rather than a reference, because the direction has to be this
///         way round.</b> <c>Vixen.Ui.HotReload</c> is a development tool: not trimmable, not
///         AOT-compatible, and a shipped application must not link one. So this assembly cannot name
///         it. What it can do is leave a hole, and let an assembly that references *both* fill the
///         hole from a <c>[ModuleInitializer]</c> — which is
///         <c>Platform/Vixen.Ui.Desktop.HotReload</c>, whose entire content is that one method.
///     </para>
///     <para>
///         ⚠ <b>Referencing that assembly is therefore the whole of the opt-in.</b> An application
///         adds it under a <c>Debug</c> condition and writes no code at all; a Release build does not
///         resolve it, nothing runs the initializer, and these two properties stay null. That is the
///         same shape as a browser's developer tools: present in the development build, absent in the
///         shipped one, and never a branch in the application's own source.
///     </para>
///     <para>
///         ⚠ <b>Process-wide, and deliberately not per application.</b> A module initializer runs
///         once and has no application to attach to — the first <c>UiApplication</c> has not been
///         constructed yet. An application that wants different behaviour sets
///         <c>UiApplicationOptions.Mount</c>, which wins.
///     </para>
/// </remarks>
public static class UiDevelopment {
    /// <summary>Mounts an application's content, in place of the ordinary build.</summary>
    /// <remarks>
    ///     <para>
    ///         Given the document, the element to mount into and the application's own
    ///         <c>Content</c> factory, it returns the component it mounted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The factory is passed through rather than called by the host, and that is what
    ///         makes a re-created component keep its parameters.</b> An application writes
    ///         <c>Content = () =&gt; new Shell { Model = model }</c>; a reload that cannot patch a
    ///         component's <c>Build</c> has to construct a new one, and the only thing that knows how
    ///         is that lambda. Handed the instance alone, a reload host falls back to the
    ///         parameterless constructor and the panel comes up bound to a model nothing else holds —
    ///         which nothing reports, because the reload succeeded.
    ///     </para>
    /// </remarks>
    public static Func<UiDocument, UiElement, Func<Component>, Component>? Mount { get; set; }

    /// <summary>Run once against each application, after its interface is built.</summary>
    /// <remarks>
    ///     Where the stylesheet watcher is attached. It is separate from <see cref="Mount" /> because
    ///     it needs the <see cref="UiApplication" />, which does not exist while its own constructor
    ///     is still running.
    /// </remarks>
    public static Action<UiApplication>? Started { get; set; }
}
