// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Platform.Linux.Tests;

/// <summary>
///     The command lines the two picker programs are driven with, tested without either of them
///     installed — which is the state of most machines this suite runs on, and the reason the
///     argument building is a pure function in the first place.
/// </summary>
public class DialogArgumentsTests {
    [Fact]
    public void ZenityOpensAFileSelection() {
        var arguments = DialogArguments.For(DialogTool.Zenity, new(), DialogKind.Open, false);

        Assert.Equal(["--file-selection"], arguments);
    }

    [Fact]
    public void ZenityConfirmsAnOverwriteWhenSaving() {
        var arguments = DialogArguments.For(DialogTool.Zenity, new(), DialogKind.Save, false);

        Assert.Contains("--save", arguments);
        Assert.Contains("--confirm-overwrite", arguments);
    }

    /// <summary>
    ///     Both tools separate several paths with something legal in a file name unless told
    ///     otherwise — zenity with <c>|</c>, kdialog with a space — so both are told otherwise, and
    ///     a regression here silently truncates a multiple selection at the first odd name.
    /// </summary>
    [Fact]
    public void AMultipleSelectionIsSeparatedByNewlines() {
        var zenity = DialogArguments.For(DialogTool.Zenity, new(), DialogKind.Open, true);
        var kdialog = DialogArguments.For(DialogTool.KDialog, new(), DialogKind.Open, true);

        Assert.Contains("--multiple", zenity);
        Assert.Contains("--separator=\n", zenity);
        Assert.Contains("--multiple", kdialog);
        Assert.Contains("--separate-output", kdialog);
    }

    [Fact]
    public void OnlyAnOpenDialogEverAsksForSeveral() {
        Assert.DoesNotContain("--multiple", DialogArguments.For(DialogTool.Zenity, new(), DialogKind.Save, true));
        Assert.DoesNotContain("--multiple", DialogArguments.For(DialogTool.Zenity, new(), DialogKind.Folder, true));
        Assert.DoesNotContain("--multiple", DialogArguments.For(DialogTool.KDialog, new(), DialogKind.Folder, true));
    }

    [Fact]
    public void ZenityStatesEachFilterOnceInTheOrderItWasGiven() {
        var options = new FileDialogOptions {
            Filters = [new("Vixen scene", "vxscene"), new("Every file", "*")]
        };

        var arguments = DialogArguments.For(DialogTool.Zenity, options, DialogKind.Open, false);

        Assert.Equal("--file-filter=Vixen scene | *.vxscene", arguments[1]);
        Assert.Equal("--file-filter=Every file | *.*", arguments[2]);
    }

    [Fact]
    public void ZenityJoinsSeveralExtensionsWithSpaces() {
        var options = new FileDialogOptions { Filters = [new("Images", "png", "jpg", "webp")] };
        var arguments = DialogArguments.For(DialogTool.Zenity, options, DialogKind.Open, false);

        Assert.Contains("--file-filter=Images | *.png *.jpg *.webp", arguments);
    }

    /// <summary>kdialog wants the patterns first and the name after, which is zenity's reversed.</summary>
    [Fact]
    public void KDialogPutsThePatternsBeforeTheName() {
        var options = new FileDialogOptions { Filters = [new("Images", "png", "jpg"), new("Text", "txt")] };
        var arguments = DialogArguments.For(DialogTool.KDialog, options, DialogKind.Open, false);

        Assert.Contains("*.png *.jpg|Images\n*.txt|Text", arguments);
    }

    [Fact]
    public void AFolderDialogCarriesNoFilters() {
        var options = new FileDialogOptions { Filters = [new("Images", "png")] };

        Assert.DoesNotContain(
            DialogArguments.For(DialogTool.Zenity, options, DialogKind.Folder, false),
            argument => argument.Contains("file-filter", StringComparison.Ordinal)
        );

        Assert.Equal(
            ["--getexistingdirectory", "."],
            DialogArguments.For(DialogTool.KDialog, options, DialogKind.Folder, false)
        );
    }

    /// <summary>
    ///     The trailing separator is what makes a directory the place to save in rather than the name
    ///     to save as, and it is the detail that is invisible until somebody's project file is
    ///     called <c>Projects</c>.
    /// </summary>
    [Fact]
    public void ADirectoryEndsWithASeparatorAndAFileNameDoesNot() {
        var directory = new FileDialogOptions { InitialDirectory = "/home/jiu/Projects" };
        var named = directory with { SuggestedFileName = "level.vxscene" };

        Assert.Contains("--filename=/home/jiu/Projects/", DialogArguments.For(DialogTool.Zenity, directory, DialogKind.Open, false));
        Assert.Contains("--filename=/home/jiu/Projects/level.vxscene", DialogArguments.For(DialogTool.Zenity, named, DialogKind.Save, false));
    }

    /// <summary>A suggested name is a save dialog's business and nothing else's.</summary>
    [Fact]
    public void ASuggestedNameIsIgnoredByAnOpenDialog() {
        var options = new FileDialogOptions { InitialDirectory = "/tmp", SuggestedFileName = "level.vxscene" };
        var arguments = DialogArguments.For(DialogTool.Zenity, options, DialogKind.Open, false);

        Assert.Contains("--filename=/tmp/", arguments);
    }

    /// <summary>kdialog has no flag for the starting place and always takes one positionally.</summary>
    [Fact]
    public void KDialogAlwaysHasAStartingPath() {
        Assert.Equal(["--getopenfilename", "."], DialogArguments.For(DialogTool.KDialog, new(), DialogKind.Open, false));

        Assert.Equal(
            ["--getsavefilename", "/tmp/"],
            DialogArguments.For(DialogTool.KDialog, new() { InitialDirectory = "/tmp" }, DialogKind.Save, false)
        );
    }

    [Fact]
    public void ATitleIsPassedToBoth() {
        var options = new FileDialogOptions { Title = "Open project" };

        Assert.Contains("--title=Open project", DialogArguments.For(DialogTool.Zenity, options, DialogKind.Open, false));

        var kdialog = DialogArguments.For(DialogTool.KDialog, options, DialogKind.Open, false);
        Assert.Contains("--title", kdialog);
        Assert.Contains("Open project", kdialog);
    }

    [Fact]
    public void OutputIsSplitOnLinesAndNothingElse() {
        Assert.Equal(
            ["/home/jiu/a file.txt", "/home/jiu/b|c.txt"],
            DialogArguments.Parse("/home/jiu/a file.txt\n/home/jiu/b|c.txt\n")
        );

        Assert.Empty(DialogArguments.Parse(string.Empty));
    }
}
