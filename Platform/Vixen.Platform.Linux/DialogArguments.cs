// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform.Linux;

/// <summary>Which of the desktop's picker programs is being driven.</summary>
enum DialogTool {
    /// <summary>GNOME's, and the CLI that <c>qarma</c> and <c>matedialog</c> also implement.</summary>
    Zenity,

    /// <summary>KDE's, which has a different CLI for the same four dialogs.</summary>
    KDialog
}

/// <summary>Which dialog is wanted.</summary>
enum DialogKind {
    Open,
    Save,
    Folder
}

/// <summary>Turning <see cref="FileDialogOptions" /> into a helper program's arguments.</summary>
/// <remarks>
///     Pure, and separate from <see cref="LinuxDialogs" /> for the same reason
///     <c>SdlTranslation</c> is separate from the desktop platform: this is the part that can be
///     wrong, and it can be tested exhaustively on a machine with no display server — or no Linux.
///     What is left in the caller is starting a process and reading its output.
/// </remarks>
static class DialogArguments {
    /// <summary>Builds the command line.</summary>
    /// <param name="tool">Which program will be run.</param>
    /// <param name="options">What the caller asked for.</param>
    /// <param name="kind">Which dialog.</param>
    /// <param name="multiple">Whether several files may be chosen. Open dialogs only.</param>
    /// <returns>The arguments, in order, unquoted — they are passed as a list and never through a
    /// shell, which is what keeps a file called <c>; rm -rf ~</c> a file name.</returns>
    public static string[] For(DialogTool tool, in FileDialogOptions options, DialogKind kind, bool multiple) =>
        tool == DialogTool.Zenity ? Zenity(options, kind, multiple) : KDialog(options, kind, multiple);

    /// <summary>Splits a helper's output into paths.</summary>
    /// <remarks>
    ///     One path per line, which both tools are asked for explicitly — zenity's default separator
    ///     is <c>|</c> and kdialog's is a space, and both of those are legal in a file name.
    /// </remarks>
    public static string[] Parse(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static string[] Zenity(in FileDialogOptions options, DialogKind kind, bool multiple) {
        var arguments = new List<string> { "--file-selection" };

        if (!string.IsNullOrEmpty(options.Title)) {
            arguments.Add("--title=" + options.Title);
        }

        switch (kind) {
            case DialogKind.Save:
                arguments.Add("--save");

                // Not zenity's default, and the one thing a save dialog must do.
                arguments.Add("--confirm-overwrite");
                break;

            case DialogKind.Folder:
                arguments.Add("--directory");
                break;

            default:
                if (multiple) {
                    arguments.Add("--multiple");

                    // The default separator is `|`, which is a legal character in a file name and
                    // therefore not a separator.
                    arguments.Add("--separator=\n");
                }

                break;
        }

        if (StartingPath(options, kind) is { } start) {
            arguments.Add("--filename=" + start);
        }

        if (kind != DialogKind.Folder) {
            foreach (var filter in options.Filters) {
                // "Name | *.a *.b". Zenity splits on the first `|`, so a filter named after a
                // keyboard shortcut is the caller's problem and not a parsing ambiguity.
                arguments.Add("--file-filter=" + filter.Name + " | " + Patterns(filter, ' '));
            }
        }

        return [.. arguments];
    }

    static string[] KDialog(in FileDialogOptions options, DialogKind kind, bool multiple) {
        var arguments = new List<string>();

        // kdialog takes the starting location as a positional argument and has no flag for it, so
        // there is always one — `.` when the caller did not say, which kdialog reads as the working
        // directory.
        var start = StartingPath(options, kind) ?? ".";

        switch (kind) {
            case DialogKind.Save:
                arguments.Add("--getsavefilename");
                arguments.Add(start);
                break;

            case DialogKind.Folder:
                arguments.Add("--getexistingdirectory");
                arguments.Add(start);
                break;

            default:
                arguments.Add("--getopenfilename");
                arguments.Add(start);
                break;
        }

        if (kind != DialogKind.Folder && options.Filters.Count > 0) {
            var filters = new string[options.Filters.Count];

            for (var index = 0; index < options.Filters.Count; index++) {
                // "*.a *.b|Name", the reverse of zenity's order, separated by newlines.
                filters[index] = Patterns(options.Filters[index], ' ') + "|" + options.Filters[index].Name;
            }

            arguments.Add(string.Join('\n', filters));
        }

        if (kind == DialogKind.Open && multiple) {
            arguments.Add("--multiple");

            // Without it kdialog separates the paths with spaces, which are legal in one.
            arguments.Add("--separate-output");
        }

        if (!string.IsNullOrEmpty(options.Title)) {
            arguments.Add("--title");
            arguments.Add(options.Title);
        }

        return [.. arguments];
    }

    /// <summary>Where the dialog opens, and what it is called if it is a save dialog.</summary>
    static string? StartingPath(in FileDialogOptions options, DialogKind kind) {
        var directory = options.InitialDirectory;
        var name = kind == DialogKind.Save ? options.SuggestedFileName : null;

        if (string.IsNullOrEmpty(directory)) {
            return string.IsNullOrEmpty(name) ? null : name;
        }

        // The trailing separator is load-bearing for both tools: without it a directory is offered
        // as the name of the file to save rather than as the place to save it in.
        //
        // ⚠ Joined with '/' by hand rather than through Path.Combine. Whatever host builds this
        // string, a Linux tool reads it, so the running machine's separator is never the right one —
        // Path.Combine produced a backslash on the Windows test leg and nowhere else, which is why
        // only half of this method ever looked wrong.
        var place = directory.EndsWith('/') ? directory : directory + "/";

        return string.IsNullOrEmpty(name) ? place : place + name;
    }

    static string Patterns(in FileFilter filter, char separator) {
        if (filter.Extensions.Length == 0) {
            return "*";
        }

        var patterns = new string[filter.Extensions.Length];

        for (var index = 0; index < filter.Extensions.Length; index++) {
            patterns[index] = "*." + filter.Extensions[index];
        }

        return string.Join(separator, patterns);
    }
}
