// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>Notifications, background work, theming, the string catalog and the user store.</summary>
public class ServiceTests : IDisposable {
    readonly UiDocument document = new(1280f, 800f);

    public ServiceTests() {
        ControlTheme.Install(document);
        EditorTheme.Install(document);
    }

    public void Dispose() {
        Strings.Use(null);

        document.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Notifications ───────────────────────────────────────────────────────

    [Fact]
    public void An_error_does_not_expire_and_an_ordinary_message_does() {
        var toasts = document.Root.Add<ToastHost>();
        var centre = new NotificationCenter(toasts) { Duration = TimeSpan.FromSeconds(2) };

        centre.Show("Saved", NotificationSeverity.Success);
        centre.Error("Shader failed to compile", "Ui/Msdf.rvn(42): expected ';'");

        // ⚠ Not from zero. `ToastHost` uses a zero `Shown` as "has not started yet", so a first
        // tick at exactly zero leaves every toast unstarted and nothing ever expires. A host driven
        // by a stopwatch never sees exactly zero; a test can.
        centre.Tick(TimeSpan.FromSeconds(1));
        centre.Tick(TimeSpan.FromSeconds(30));

        // An error that disappears while somebody is reading the line number is an error they have
        // to reproduce.
        var live = Assert.Single(toasts.Live);
        Assert.Contains("Msdf.rvn", live.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_history_is_newest_first_and_bounded() {
        var centre = new NotificationCenter(document.Root.Add<ToastHost>()) { HistoryLimit = 3 };

        for (var i = 0; i < 10; i++) {
            centre.Show("message " + i);
        }

        Assert.Equal(3, centre.History.Count);
        Assert.Equal("message 9", centre.History[0].Message);
    }

    [Fact]
    public void Clearing_the_history_leaves_what_is_on_screen() {
        var toasts = document.Root.Add<ToastHost>();
        var centre = new NotificationCenter(toasts);

        centre.Show("Saved");
        centre.Clear();

        Assert.Empty(centre.History);
        Assert.Single(toasts.Live);
    }

    // ── Background tasks ────────────────────────────────────────────────────

    [Fact]
    public async Task Work_that_finishes_leaves_the_list_after_a_pump() {
        var tasks = new BackgroundTaskManager();
        var gate = new TaskCompletionSource();

        var task = tasks.Start("Importing", async _ => await gate.Task);
        Assert.True(tasks.IsBusy);

        gate.SetResult();
        await Drain(tasks, task);

        Assert.Equal(BackgroundTaskState.Completed, task.State);
        Assert.Equal(1f, task.Progress);
        Assert.False(tasks.IsBusy);
    }

    [Fact]
    public async Task Work_that_throws_ends_as_failed_rather_than_taking_the_process_down() {
        var tasks = new BackgroundTaskManager();
        var task = tasks.Start("Building", _ => throw new InvalidOperationException("no compiler"));

        await Drain(tasks, task);

        Assert.Equal(BackgroundTaskState.Failed, task.State);
        Assert.Equal("no compiler", task.Failure?.Message);
    }

    [Fact]
    public async Task Cancelling_asks_and_the_task_ends_when_the_work_notices() {
        var tasks = new BackgroundTaskManager();
        var started = new TaskCompletionSource();

        var task = tasks.Start(
            "Baking",
            async running => {
                started.SetResult();

                while (!running.IsCancellationRequested) {
                    await Task.Delay(1, CancellationToken.None);
                }

                running.Cancellation.ThrowIfCancellationRequested();
            }
        );

        await started.Task;
        task.Cancel();

        // Asked, not done: a manager that took the task out of the list here would let the user
        // start the import again over the top of one that had not stopped.
        Assert.True(task.IsCancellationRequested);

        await Drain(tasks, task);
        Assert.Equal(BackgroundTaskState.Cancelled, task.State);
    }

    [Fact]
    public void Progress_reported_from_the_work_lands_on_the_pump_and_not_before() {
        var tasks = new BackgroundTaskManager();
        var task = tasks.Begin("Importing");

        task.Report(0.5f, "textures");

        Assert.True(task.IsIndeterminate);
        Assert.Equal(0f, task.Progress);

        tasks.Pump();

        Assert.False(task.IsIndeterminate);
        Assert.Equal(0.5f, task.Progress);
        Assert.Equal("textures", task.Status);
    }

    [Fact]
    public void Overall_progress_ignores_the_tasks_that_have_not_said() {
        var tasks = new BackgroundTaskManager();

        var known = tasks.Begin("Importing");
        tasks.Begin("Scanning");

        known.Report(0.8f);
        tasks.Pump();

        // Counting an indeterminate task as zero would leave three imports sitting at a third of
        // the way along and never moving.
        Assert.Equal(0.8f, tasks.Progress, 0.001f);
    }

    [Fact]
    public void A_pump_applies_at_most_its_budget() {
        var tasks = new BackgroundTaskManager();
        var task = tasks.Begin("Importing");

        for (var i = 0; i < 10; i++) {
            task.Report("file " + i);
        }

        tasks.Pump(budget: 3);
        Assert.Equal("file 2", task.Status);

        tasks.Pump();
        Assert.Equal("file 9", task.Status);
    }

    // ── Theming ─────────────────────────────────────────────────────────────

    [Fact]
    public void Switching_theme_is_one_class_on_the_root() {
        var theme = new ThemeService(document, ThemeMode.Light);
        Assert.False(document.Root.HasClass(ThemeService.DarkClass));

        theme.Toggle();

        Assert.Equal(ThemeMode.Dark, theme.Mode);
        Assert.True(document.Root.HasClass(ThemeService.DarkClass));
    }

    [Fact]
    public void A_user_theme_file_becomes_an_author_stylesheet() {
        var css = ThemeService.Compile(
            """
            light:
              accent: "#ff0000"
            dark:
              --accent: "#00ff00"
            """
        );

        // The quotes are the YAML's rather than the value's — `#` starts a comment unquoted — so
        // what reaches the stylesheet is the colour.
        Assert.Contains("root {", css, StringComparison.Ordinal);
        Assert.Contains("--accent: #ff0000;", css, StringComparison.Ordinal);

        // Written with or without the leading dashes, because a person editing this file will do
        // both.
        Assert.Contains("root.dark {", css, StringComparison.Ordinal);
        Assert.Contains("--accent: #00ff00;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flat_theme_file_is_read_as_the_light_theme() {
        var css = ThemeService.Compile("accent: blue");

        Assert.Contains("root {", css, StringComparison.Ordinal);
        Assert.DoesNotContain("root.dark", css, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_would_break_the_sheet_is_dropped() {
        var css = ThemeService.Compile(
            """
            light:
              accent: "blue} root { display: none"
              border: green
            """
        );

        // A value with a brace in it silently breaks every rule after it, which is a miserable
        // thing to debug in a file somebody hand-edited.
        Assert.DoesNotContain("display: none", css, StringComparison.Ordinal);
        Assert.Contains("--border: green;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Reloading_a_theme_replaces_the_sheet_rather_than_adding_one() {
        var theme = new ThemeService(document);

        theme.LoadTokens("light:\n  accent: red");
        theme.LoadTokens("light:\n  accent: blue");

        Assert.Equal("light:\n  accent: blue", theme.Tokens);
    }

    // ── Localisation ────────────────────────────────────────────────────────

    [Fact]
    public void A_string_with_no_translation_falls_back_to_the_source_text() {
        Strings.Use(new StringCatalog("cs"));

        Assert.Equal("Save", EditorStrings.CommandSave.Text);

        // The worst case is English rather than `editor.command.file.save`, which is what an editor
        // whose fallback is a file shows anybody with a missing install.
        Assert.Contains(EditorStrings.CommandSave.Id, Strings.Missing);
    }

    [Fact]
    public void A_translated_string_comes_from_the_catalog() {
        Strings.Use(new StringCatalog("cs").Set(EditorStrings.CommandSave.Id, "Uložit"));

        Assert.Equal("Uložit", EditorStrings.CommandSave.Text);
        Assert.DoesNotContain(EditorStrings.CommandSave.Id, Strings.Missing);
    }

    [Fact]
    public void A_catalog_round_trips_and_is_written_in_id_order() {
        var catalog = new StringCatalog("cs")
            .Set("z.last", "poslední")
            .Set("a.first", "první");

        var text = catalog.Save();
        Assert.True(text.IndexOf("a.first", StringComparison.Ordinal) < text.IndexOf("z.last", StringComparison.Ordinal));

        var reloaded = StringCatalog.Load(text);

        Assert.Equal("cs", reloaded.Language);
        Assert.Equal("poslední", reloaded.Find("z.last"));
        Assert.Equal(text, reloaded.Save());
    }

    [Fact]
    public void A_template_holds_every_string_the_editor_declares() {
        var template = Strings.Template("cs");
        Assert.Equal(EditorStrings.All.Count, template.Count);

        Strings.Use(template);

        foreach (var id in EditorStrings.All) {
            Assert.Equal(id.Source, id.Text);
        }

        Assert.Empty(Strings.Missing);
    }

    [Fact]
    public void Declared_ids_are_unique() {
        // Two strings answering to one id is a translation that changes both, and it is the sort of
        // thing a generator would catch and a hand-written table would not.
        var ids = EditorStrings.All.Select(id => id.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // ── The user store ──────────────────────────────────────────────────────

    [Fact]
    public void Layouts_are_listed_by_name_without_the_one_the_editor_writes_itself() {
        var directory = Path.Combine(Path.GetTempPath(), "vixen-editor-" + Guid.NewGuid().ToString("N"));
        var store = new EditorUserStore(directory);

        try {
            Assert.Empty(store.Layouts());
            Assert.Null(store.LoadLayout("Shading"));

            store.SaveLayout("Shading", "root:");
            store.SaveLayout("Animation", "root:");
            store.SaveLayout(EditorUserStore.CurrentLayout, "root:");

            Assert.Equal(["Animation", "Shading"], store.Layouts());
            Assert.Equal("root:", store.LoadLayout("Shading"));
            Assert.True(store.DeleteLayout("Shading"));
            Assert.Equal(["Animation"], store.Layouts());
        } finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_layout_name_that_would_escape_the_directory_is_refused() =>
        // The name comes from a text box, and `../../keybindings.yaml` is a layout that overwrites
        // the keymap.
        Assert.Throws<ArgumentException>(() => EditorUserStore.FileFor("../../keybindings.yaml"));

    static async Task Drain(BackgroundTaskManager tasks, BackgroundTask task) {
        for (var i = 0; i < 500 && task.IsRunning; i++) {
            tasks.Pump();

            if (task.IsRunning) {
                await Task.Delay(2, CancellationToken.None);
            }
        }

        tasks.Pump();
    }
}
