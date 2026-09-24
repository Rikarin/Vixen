// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Input;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The calendar: six weeks of days, laid out by the culture, walked by the keyboard.</summary>
/// <remarks>
///     ⚠ <b>Every date here is fixed</b> — <c>Today</c> is set, never read off the machine — and
///     every culture is built by hand from the invariant one, on <c>TextFieldTests.Continental</c>'s
///     argument: this repository builds with <c>InvariantGlobalization</c>, so a named culture is not
///     there to look up, and one that was would make the assertion a claim about the machine's ICU.
///     1 September 2026 is a Tuesday.
/// </remarks>
public class CalendarTests {
    static readonly DateOnly Today = new(2026, 9, 24);

    static (ControlFixture Fixture, Calendar Calendar) Built(CultureInfo? culture = null) {
        var fixture = new ControlFixture();
        var calendar = fixture.Add<Calendar>();

        calendar.Today = Today;
        calendar.Culture = culture;
        calendar.Month = Today;
        fixture.Update();

        return (fixture, calendar);
    }

    /// <summary>A week that starts on Monday, and a Czech month, to prove both are read off the culture.</summary>
    static CultureInfo Monday() {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();

        culture.DateTimeFormat.FirstDayOfWeek = DayOfWeek.Monday;
        culture.DateTimeFormat.MonthNames = [
            "leden", "únor", "březen", "duben", "květen", "červen", "červenec", "srpen", "září", "říjen",
            "listopad", "prosinec", ""
        ];

        culture.DateTimeFormat.YearMonthPattern = "MMMM yyyy";

        return culture;
    }

    /// <summary>
    ///     ⚠ <b>Six weeks from the week's first day, whatever the month</b> — so the grid is the same
    ///     size in every month, and the days either side are drawn and marked.
    /// </summary>
    [Fact]
    public void A_month_is_six_weeks_starting_on_the_culture_s_first_day() {
        var (fixture, calendar) = Built();

        using (fixture) {
            Assert.Equal(42, calendar.Days.Count);
            Assert.Equal(new DateOnly(2026, 8, 30), calendar.Days[0].Date);
            Assert.Equal(new DateOnly(2026, 9, 1), calendar.Days[2].Date);
            Assert.Equal(new DateOnly(2026, 10, 10), calendar.Days[41].Date);
            Assert.Equal("30", calendar.Days[0].Label);

            // Two August days before it and ten October days after it, and nothing else outside.
            var outside = calendar.Days.Where(static day => day.HasClass("outside")).Select(static day => day.Date).ToList();

            Assert.Equal(12, outside.Count);
            Assert.All(outside, static date => Assert.NotEqual(9, date.Month));
            Assert.True(calendar.DayOf(Today)!.HasClass("today"));
            Assert.Equal("2026 September", calendar.Title.Text);

            // A month that fills exactly four weeks still shows six: February 2026 starts on Sunday.
            calendar.Month = new DateOnly(2026, 2, 10);
            fixture.Update();

            Assert.Equal(new DateOnly(2026, 2, 1), calendar.Days[0].Date);
            Assert.Equal(new DateOnly(2026, 3, 14), calendar.Days[41].Date);
            Assert.Equal(14, calendar.Days.Count(static day => day.HasClass("outside")));
        }
    }

    /// <summary>
    ///     ⚠ <b>The week's first day, the weekday headings and the month's name are the culture's</b>,
    ///     and the same six-week rule then starts on a Monday.
    /// </summary>
    [Fact]
    public void The_culture_decides_where_the_week_starts_and_what_the_month_is_called() {
        var (fixture, calendar) = Built(Monday());

        using (fixture) {
            Assert.Equal(new DateOnly(2026, 8, 31), calendar.Days[0].Date);
            Assert.Equal("září 2026", calendar.Title.Text);

            var headings = AccessibilitySnapshot.Render(calendar.Grid)
                .Split('\n')
                .Select(static line => line.Trim())
                .Where(static line => line.StartsWith("columnheader", StringComparison.Ordinal))
                .ToList();

            Assert.Equal(7, headings.Count);
            Assert.Equal("columnheader \"Monday\"", headings[0]);
            Assert.Equal("columnheader \"Sunday\"", headings[6]);
        }
    }

    /// <summary>
    ///     ⚠ <b>A grid of named cells inside a group, both named by the month</b> — and a cell is
    ///     called by its whole date, not by the number on it.
    /// </summary>
    [Fact]
    public void The_tree_is_a_group_holding_a_grid_of_days_called_by_their_dates() {
        var (fixture, calendar) = Built();

        using (fixture) {
            calendar.Value = new DateOnly(2026, 9, 3);
            fixture.Update();

            var lines = AccessibilitySnapshot.Render(calendar).Split('\n');

            Assert.Equal("group \"2026 September\"", lines[0]);
            Assert.Contains("  grid \"2026 September\"", lines);
            Assert.Contains("      gridcell \"Sunday, 30 August 2026\"", lines);
            Assert.Contains(lines, static line => line.StartsWith("      gridcell \"Thursday, 03 September 2026\" [selected", StringComparison.Ordinal));
            Assert.Contains(lines, static line => line.StartsWith("  button \"Previous month\"", StringComparison.Ordinal));
            Assert.Equal(1 + 7 + 42 + 6, lines.Count(static line => line.TrimStart().StartsWith("row", StringComparison.Ordinal) || line.TrimStart().StartsWith("columnheader", StringComparison.Ordinal) || line.TrimStart().StartsWith("gridcell", StringComparison.Ordinal)));
            Assert.Empty(AccessibilitySnapshot.Unnamed(calendar));
        }
    }

    /// <summary>A click chooses a day, and a click on a day of the next month shows that month.</summary>
    [Fact]
    public void A_click_chooses_a_day_and_one_outside_the_month_moves_to_it() {
        var (fixture, calendar) = Built();

        using (fixture) {
            var changes = new List<DateOnly?>();
            calendar.ValueChanged += (_, value) => changes.Add(value);

            fixture.Click(calendar.DayOf(new DateOnly(2026, 9, 15))!);

            Assert.Equal(new DateOnly(2026, 9, 15), calendar.Value);
            Assert.True(calendar.DayOf(new DateOnly(2026, 9, 15))!.IsSelected);
            Assert.Equal([new DateOnly(2026, 9, 15)], changes);

            fixture.Click(calendar.DayOf(new DateOnly(2026, 10, 2))!);
            fixture.Update();

            Assert.Equal(new DateOnly(2026, 10, 2), calendar.Value);
            Assert.Equal(new DateOnly(2026, 10, 1), calendar.Month);
            Assert.False(calendar.DayOf(new DateOnly(2026, 10, 2))!.HasClass("outside"));
            Assert.Same(calendar.DayOf(new DateOnly(2026, 10, 2)), fixture.Document.Focused);
        }
    }

    /// <summary>
    ///     ⚠ <b>One day is the tab stop, and the arrows move it</b> — across a month boundary too, by
    ///     showing the next month under the same focus.
    /// </summary>
    [Fact]
    public void The_arrows_walk_the_days_and_page_through_the_months() {
        var (fixture, calendar) = Built();

        using (fixture) {
            // Today is the stop before anything is chosen, and it is the only one.
            Assert.Equal([Today], calendar.Days.Where(static day => day.TabIndex == 0).Select(static day => day.Date));

            fixture.Document.Focus(calendar.DayOf(Today)!);

            DateOnly Focused() => ((CalendarDay)fixture.Document.Focused!).Date;

            fixture.Type(InputKey.Right);
            Assert.Equal(new DateOnly(2026, 9, 25), Focused());

            fixture.Type(InputKey.Down);
            Assert.Equal(new DateOnly(2026, 10, 2), Focused());
            Assert.Equal(new DateOnly(2026, 10, 1), calendar.Month);

            fixture.Type(InputKey.Up);
            fixture.Type(InputKey.Up);
            Assert.Equal(new DateOnly(2026, 9, 18), Focused());
            Assert.Equal(new DateOnly(2026, 9, 1), calendar.Month);

            fixture.Type(InputKey.PageDown);
            Assert.Equal(new DateOnly(2026, 10, 18), Focused());

            fixture.Type(InputKey.PageUp, ModifierKeys.Shift);
            Assert.Equal(new DateOnly(2025, 10, 18), Focused());
            Assert.Equal(new DateOnly(2025, 10, 1), calendar.Month);

            // 18 October 2025 is a Saturday, so the week (Sunday first) runs from the 12th.
            fixture.Type(InputKey.Home);
            Assert.Equal(new DateOnly(2025, 10, 12), Focused());
            fixture.Type(InputKey.End);
            Assert.Equal(new DateOnly(2025, 10, 18), Focused());

            // Still exactly one stop, and it is where the keyboard is.
            Assert.Equal([new DateOnly(2025, 10, 18)], calendar.Days.Where(static day => day.TabIndex == 0).Select(static day => day.Date));

            // Enter chooses, as a click does.
            fixture.Type(InputKey.Enter);
            Assert.Equal(new DateOnly(2025, 10, 18), calendar.Value);
        }
    }

    /// <summary>A month on from the 31st is the last day of the shorter month, not the first of the one after.</summary>
    [Fact]
    public void Paging_from_the_end_of_a_long_month_lands_on_the_end_of_a_short_one() {
        var (fixture, calendar) = Built();

        using (fixture) {
            calendar.Value = new DateOnly(2026, 1, 31);
            fixture.Update();
            fixture.Document.Focus(calendar.DayOf(new DateOnly(2026, 1, 31))!);

            fixture.Type(InputKey.PageDown);

            Assert.Equal(new DateOnly(2026, 2, 28), ((CalendarDay)fixture.Document.Focused!).Date);
        }
    }

    /// <summary>
    ///     ⚠ <b>Outside the limits a day is drawn and refused</b>: disabled, unclickable, and a wall
    ///     the arrows stop at — and the arrow button to a month wholly outside is disabled.
    /// </summary>
    [Fact]
    public void Days_outside_the_limits_are_disabled_and_the_keyboard_stops_at_them() {
        var (fixture, calendar) = Built();

        using (fixture) {
            calendar.Minimum = new DateOnly(2026, 9, 10);
            calendar.Maximum = new DateOnly(2026, 9, 26);
            fixture.Update();

            Assert.True(calendar.DayOf(new DateOnly(2026, 9, 9))!.Disabled);
            Assert.False(calendar.DayOf(new DateOnly(2026, 9, 10))!.Disabled);
            Assert.True(calendar.Previous.Disabled);
            Assert.True(calendar.Next.Disabled);

            fixture.Click(calendar.DayOf(new DateOnly(2026, 9, 5))!);
            Assert.Null(calendar.Value);

            fixture.Document.Focus(calendar.DayOf(Today)!);
            fixture.Type(InputKey.Down);

            Assert.Equal(new DateOnly(2026, 9, 26), ((CalendarDay)fixture.Document.Focused!).Date);
            Assert.Equal(new DateOnly(2026, 9, 1), calendar.Month);
        }
    }

    /// <summary>The arrow buttons show the month either side, without choosing anything.</summary>
    [Fact]
    public void The_arrow_buttons_page_the_month() {
        var (fixture, calendar) = Built();

        using (fixture) {
            fixture.Click(calendar.Next);
            fixture.Update();

            Assert.Equal(new DateOnly(2026, 10, 1), calendar.Month);
            Assert.Equal("2026 October", calendar.Title.Text);
            Assert.Null(calendar.Value);

            // The stop follows to the same day of the month shown, so Tab lands inside it.
            Assert.Equal([new DateOnly(2026, 10, 24)], calendar.Days.Where(static day => day.TabIndex == 0).Select(static day => day.Date));

            fixture.Click(calendar.Previous);
            fixture.Click(calendar.Previous);
            fixture.Update();

            Assert.Equal(new DateOnly(2026, 8, 1), calendar.Month);
        }
    }
}
