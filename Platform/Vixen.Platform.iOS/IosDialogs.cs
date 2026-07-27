// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using UIKit;

namespace Vixen.Platform.Ios;

/// <summary>Alerts, and the four file operations iOS does not have.</summary>
/// <remarks>
///     <para>
///         <b>There is no file system to open a file from.</b> iOS has a document picker, which
///         browses iCloud and other applications' shared containers rather than a disk, returns a
///         security-scoped URL rather than a path, and needs an entitlement. Returning one of those
///         as a <see cref="string"/> path would produce something that looks like a file and cannot
///         be opened, so all four refuse — the same choice the headless platform makes, and for the
///         same reason.
///     </para>
///     <para>
///         <b>The message box is real.</b> <c>UIAlertController</c> is modal, presented from the
///         root view controller, and its result arrives on a callback — which is why the whole
///         interface is asynchronous, and why this is the platform that shows the interface was
///         shaped correctly.
///     </para>
/// </remarks>
/// <param name="platform">The platform, for the window to present from.</param>
internal sealed class IosDialogs(IosPlatform platform) : INativeDialogs {
    /// <inheritdoc />
    public ValueTask<string?> OpenFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> OpenFilesAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public ValueTask<string?> SaveFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public ValueTask<string?> OpenFolderAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public ValueTask<MessageBoxResult> ShowMessageAsync(
        MessageBoxOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) {
        if (platform.RootController is not { } root) {
            return ValueTask.FromResult(MessageBoxResult.None);
        }

        var completion = new TaskCompletionSource<MessageBoxResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var alert = UIAlertController.Create(options.Title, options.Message, UIAlertControllerStyle.Alert);

        foreach (var (label, result, style) in Buttons(options.Buttons)) {
            alert.AddAction(UIAlertAction.Create(label, style, _ => completion.TrySetResult(result)));
        }

        // Registered before presenting: a cancellation that arrives while the alert is going up must
        // still be able to take it down again.
        cancellationToken.Register(() => {
                alert.DismissViewController(animated: false, null);
                completion.TrySetCanceled(cancellationToken);
            }
        );

        root.PresentViewController(alert, animated: true, null);

        return new(completion.Task);
    }

    /// <summary>
    ///     The buttons, in the order iOS wants them.
    /// </summary>
    /// <remarks>
    ///     The cancelling one is marked <see cref="UIAlertActionStyle.Cancel" /> rather than merely
    ///     placed last, because that is what makes the hardware back gesture and VoiceOver treat it
    ///     as the way out.
    /// </remarks>
    static IEnumerable<(string Label, MessageBoxResult Result, UIAlertActionStyle Style)> Buttons(
        MessageBoxButtons buttons
    ) =>
        buttons switch {
            MessageBoxButtons.OkCancel => [
                ("OK", MessageBoxResult.Ok, UIAlertActionStyle.Default),
                ("Cancel", MessageBoxResult.Cancel, UIAlertActionStyle.Cancel)
            ],
            MessageBoxButtons.YesNo => [
                ("Yes", MessageBoxResult.Yes, UIAlertActionStyle.Default),
                ("No", MessageBoxResult.No, UIAlertActionStyle.Cancel)
            ],
            MessageBoxButtons.YesNoCancel => [
                ("Yes", MessageBoxResult.Yes, UIAlertActionStyle.Default),
                ("No", MessageBoxResult.No, UIAlertActionStyle.Default),
                ("Cancel", MessageBoxResult.Cancel, UIAlertActionStyle.Cancel)
            ],
            _ => [("OK", MessageBoxResult.Ok, UIAlertActionStyle.Default)]
        };
}
