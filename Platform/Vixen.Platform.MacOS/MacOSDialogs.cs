// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vixen.Platform.MacOS;

/// <summary>The macOS file pickers: <c>NSOpenPanel</c> and <c>NSSavePanel</c>.</summary>
/// <remarks>
///     <para>
///         <b>The main thread, or nothing.</b> AppKit aborts the process — <c>SIGABRT</c>, with a
///         message about the main thread — when a panel is created anywhere else, and that is not a
///         rule anything can work around. So a call from another thread returns nothing-chosen
///         rather than crashing the application that made it. In practice the restriction costs
///         nothing: <see cref="IPlatform" /> is owned by the thread that created it, and on macOS
///         that thread has to be the main one for the window to exist at all.
///     </para>
///     <para>
///         <b><c>runModal</c>, so the frame loop stops while the panel is open.</b> It is AppKit's
///         own event loop that runs instead of ours, so the application is not hung — its windows
///         redraw, the panel works, the menu bar responds — but nothing of ours advances until the
///         user is finished. The alternative is <c>beginSheetModalForWindow:completionHandler:</c>,
///         which takes an Objective-C block, and constructing a block from managed code means
///         hand-building its layout and its descriptor to an ABI that is not part of any header.
///         That is worth doing when a sheet is worth having; it is not worth doing to open a
///         project.
///     </para>
///     <para>
///         <b><c>setAllowedFileTypes:</c> is deprecated and is used anyway.</b> Its replacement,
///         <c>setAllowedContentTypes:</c>, takes <c>UTType</c> objects, which have to be built from
///         extensions through <c>UTTypeCreateFromExtension</c> — a second framework and a
///         two-step conversion to say what a list of extensions already says. The deprecated call
///         still works, and when it stops working it will stop in a way a test on a Mac notices.
///     </para>
/// </remarks>
/// <param name="fallback">The portable dialogs, which keep the message boxes.</param>
[SupportedOSPlatform("macos")]
public sealed class MacOSDialogs(INativeDialogs fallback) : INativeDialogs {
    /// <summary><c>NSModalResponseOK</c>.</summary>
    const int ResponseOk = 1;

    /// <inheritdoc />
    public ValueTask<string?> OpenFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult(First(Open(options, false, false, cancellationToken)));

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> OpenFilesAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<IReadOnlyList<string>>(Open(options, false, true, cancellationToken));

    /// <inheritdoc />
    public ValueTask<string?> SaveFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult(Save(options, cancellationToken));

    /// <inheritdoc />
    public ValueTask<string?> OpenFolderAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult(First(Open(options, true, false, cancellationToken)));

    /// <summary>Shows the message box the portable implementation shows.</summary>
    /// <remarks>
    ///     SDL's is <c>NSAlert</c> with the button sets already translated, and it is the dialog
    ///     that has to work when the renderer has just failed — which is an argument for the
    ///     implementation that is already there rather than a second one.
    /// </remarks>
    public ValueTask<MessageBoxResult> ShowMessageAsync(
        MessageBoxOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        fallback.ShowMessageAsync(options, owner, cancellationToken);

    /// <summary>Whether a panel can be shown from the calling thread.</summary>
    static bool CanShow => ObjC.IsMainThread;

    static string? First(string[] paths) => paths.Length > 0 ? paths[0] : null;

    static string[] Open(
        FileDialogOptions options,
        bool folders,
        bool multiple,
        CancellationToken cancellationToken
    ) {
        if (cancellationToken.IsCancellationRequested || !CanShow) {
            return [];
        }

        // AppKit needs its application object before a panel can run a modal session, and a head
        // that has already created a window has one. Asking for it is idempotent and is what makes
        // this work in a tool that has not.
        ObjC.Send(ObjC.GetClass("NSApplication"), ObjC.Selector("sharedApplication"));

        var panel = ObjC.Send(ObjC.GetClass("NSOpenPanel"), ObjC.Selector("openPanel"));

        if (panel == 0) {
            return [];
        }

        ObjC.SendSetBool(panel, ObjC.Selector("setCanChooseFiles:"), !folders);
        ObjC.SendSetBool(panel, ObjC.Selector("setCanChooseDirectories:"), folders);
        ObjC.SendSetBool(panel, ObjC.Selector("setAllowsMultipleSelection:"), multiple && !folders);
        ObjC.SendSetBool(panel, ObjC.Selector("setResolvesAliases:"), true);

        Configure(panel, options, folders);

        if (ObjC.Send(panel, ObjC.Selector("runModal")) != ResponseOk) {
            return [];
        }

        var urls = ObjC.Send(panel, ObjC.Selector("URLs"));
        var count = (int)ObjC.Count(urls);
        var paths = new List<string>(count);

        for (var index = 0; index < count; index++) {
            if (PathOf(ObjC.At(urls, index)) is { } path) {
                paths.Add(path);
            }
        }

        return [.. paths];
    }

    static string? Save(FileDialogOptions options, CancellationToken cancellationToken) {
        if (cancellationToken.IsCancellationRequested || !CanShow) {
            return null;
        }

        ObjC.Send(ObjC.GetClass("NSApplication"), ObjC.Selector("sharedApplication"));

        var panel = ObjC.Send(ObjC.GetClass("NSSavePanel"), ObjC.Selector("savePanel"));

        if (panel == 0) {
            return null;
        }

        Configure(panel, options, false);

        if (!string.IsNullOrEmpty(options.SuggestedFileName)) {
            ObjC.Send(
                panel,
                ObjC.Selector("setNameFieldStringValue:"),
                ObjC.String(options.SuggestedFileName)
            );
        }

        return ObjC.Send(panel, ObjC.Selector("runModal")) == ResponseOk
            ? PathOf(ObjC.Send(panel, ObjC.Selector("URL")))
            : null;
    }

    static void Configure(nint panel, in FileDialogOptions options, bool folders) {
        if (!string.IsNullOrEmpty(options.Title)) {
            // The window title, which on a panel is only shown when it is not a sheet. `setMessage:`
            // is the line above the file list and is what a user actually reads, so both are set to
            // the caller's one string.
            ObjC.Send(panel, ObjC.Selector("setTitle:"), ObjC.String(options.Title));
            ObjC.Send(panel, ObjC.Selector("setMessage:"), ObjC.String(options.Title));
        }

        if (!string.IsNullOrEmpty(options.InitialDirectory)) {
            var url = ObjC.Send(
                ObjC.GetClass("NSURL"),
                ObjC.Selector("fileURLWithPath:"),
                ObjC.String(options.InitialDirectory)
            );

            if (url != 0) {
                ObjC.Send(panel, ObjC.Selector("setDirectoryURL:"), url);
            }
        }

        if (folders || options.Filters.Count == 0) {
            return;
        }

        var extensions = new List<string>();

        foreach (var filter in options.Filters) {
            extensions.AddRange(filter.Extensions);
        }

        if (extensions.Count == 0) {
            return;
        }

        // One flat list, because that is the shape AppKit takes. macOS has no filter dropdown of
        // its own — the panel simply disables what does not match — so a caller's grouping into
        // named filters has nowhere to go and its names are not shown to anybody.
        ObjC.Send(panel, ObjC.Selector("setAllowedFileTypes:"), ObjC.StringArray(extensions));
        ObjC.SendSetBool(panel, ObjC.Selector("setAllowsOtherFileTypes:"), false);
    }

    static string? PathOf(nint url) =>
        url == 0 ? null : ObjC.ToString(ObjC.Send(url, ObjC.Selector("path")));
}
