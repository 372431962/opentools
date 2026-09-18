using DesktopCalendarWidget;
using System.Globalization;
using System.Diagnostics;

internal static class Program
{
    private static int assertions;
    private static int failures;
    private static int tests;
    private static DateTime D(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture);
    private static WidgetSettings Settings(string anchor = "2026-09-14", bool single = true,
        SingleRestDay restDay = SingleRestDay.Sunday, RestPattern pattern = RestPattern.Alternate) => new()
        { AnchorWeekStart = D(anchor), AnchorWeekIsSingleRest = single, SingleRestDay = restDay, RestPattern = pattern };
    // All holiday records here are synthetic, not official holiday arrangements.
    private static HolidayEntry Work(string date) => new() { Date = date, IsWorkday = true, IsHoliday = false, Name = "Synthetic work" };
    private static HolidayEntry Holiday(string date) => new() { Date = date, IsHoliday = true, Name = "Synthetic holiday" };
    private static DaySchedule Day(string date, WidgetSettings settings, params HolidayEntry[] entries) =>
        RestSchedule.GetDaySchedule(D(date), settings, RestSchedule.CreateHolidayMap(entries));
    private static WeekSchedule Week(string date, WidgetSettings settings, params HolidayEntry[] entries) =>
        RestSchedule.GetWeekSchedule(D(date), settings, RestSchedule.CreateHolidayMap(entries));
    private static void Equal<T>(T expected, T actual, string message) where T : notnull
    {
        assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
    }
    private static void Run(string name, Action test)
    {
        tests++;
        try { test(); Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
    }

    private static int Main(string[] args)
    {
        // Reproduce the original red run against the retained legacy production signature.
        if (args.Contains("--legacy-baseline"))
        {
            foreach (var (date, expected) in new[] { ("2026-10-03", true), ("2026-10-10", false) })
                Run($"legacy user regression {date}", () => Equal(expected,
                    RestSchedule.IsRestDay(D(date), RestPattern.Alternate, SingleRestDay.Sunday, D("2026-09-14"), true), "rest"));
            return Report();
        }

        Run("user regression 2026-10-03 rests", () => Equal(true, Day("2026-10-03", Settings(), Work("2026-09-26")).IsRestDay, "rest"));
        Run("user regression 2026-10-10 works", () => Equal(false, Day("2026-10-10", Settings(), Work("2026-09-26")).IsRestDay, "rest"));
        Run("adjusted double week is actually single and shift persists", () =>
        {
            var s = Settings(); var h = Work("2026-09-26");
            var week = Week("2026-09-21", s, h);
            Equal(false, week.PlannedSingleRest, "original plan");
            Equal(true, week.IsSingleRestWeek, "actual type");
            Equal(false, week.SaturdayIsRest, "Saturday work");
            Equal(true, week.SundayIsRest, "Sunday rest");
            Equal(true, Day("2026-10-17", s, h).IsRestDay, "persistent shift week 4");
            Equal(false, Day("2026-10-24", s, h).IsRestDay, "persistent shift week 5");
        });
        Run("work on already working single Saturday does not flip twice", () =>
        {
            var s = Settings(); var h = Work("2026-09-19");
            Equal(true, Day("2026-09-26", s, h).IsRestDay, "next double Saturday");
            Equal(false, Day("2026-10-03", s, h).IsRestDay, "following single Saturday");
        });
        Run("weekday holidays and work adjustments do not shift weeks", () =>
        {
            var s = Settings(); HolidayEntry[] h = [Holiday("2026-09-16"), Work("2026-09-17")];
            Equal(true, Day("2026-09-16", s, h).IsRestDay, "weekday holiday");
            Equal(false, Day("2026-09-17", s, h).IsRestDay, "weekday work");
            Equal(true, Day("2026-09-26", s, h).IsRestDay, "next double");
            Equal(false, Day("2026-10-03", s, h).IsRestDay, "following single");
        });
        Run("Saturday holiday converts single to double and shifts", () =>
        {
            var s = Settings(); var h = Holiday("2026-09-19");
            Equal(2, Week("2026-09-14", s, h).WeekendRestDays, "actual double");
            Equal(false, Day("2026-09-26", s, h).IsRestDay, "next single");
            Equal(true, Day("2026-10-03", s, h).IsRestDay, "following double");
        });
        Run("Sunday makeup with Saturday configured rest", () =>
        {
            var s = Settings(restDay: SingleRestDay.Saturday); var h = Work("2026-09-27");
            Equal(true, Day("2026-09-26", s, h).IsRestDay, "Saturday rests");
            Equal(false, Day("2026-09-27", s, h).IsRestDay, "Sunday works");
            Equal(true, Day("2026-10-04", s, h).IsRestDay, "next double Sunday");
            Equal(false, Day("2026-10-11", s, h).IsRestDay, "next single Sunday");
        });
        Run("actual Saturday-only rest must not be reinterpreted as Sunday rest", () =>
        {
            var s = Settings(); var h = Work("2026-09-27");
            Equal(true, Week("2026-09-21", s, h).IsSingleRestWeek, "actual single");
            Equal(true, Day("2026-09-26", s, h).IsRestDay, "unconfigured Saturday still rests");
            Equal(false, Day("2026-09-27", s, h).IsRestDay, "configured Sunday works");
            Equal(true, Day("2026-10-03", s, h).IsRestDay, "next double");
        });
        Run("multiple adjustments across month and year", () =>
        {
            var s = Settings("2026-12-14");
            HolidayEntry[] h = [Work("2026-12-26"), Holiday("2027-01-09"), Work("2027-01-23")];
            foreach (var (date, rest) in new[] { ("2026-12-19", false), ("2026-12-26", false),
                ("2027-01-02", true), ("2027-01-09", true), ("2027-01-16", false),
                ("2027-01-23", false), ("2027-01-30", true), ("2027-02-06", false) })
                Equal(rest, Day(date, s, h).IsRestDay, date);
        });
        Run("pre-anchor events cannot override manual anchor", () =>
        {
            var s = Settings(); HolidayEntry[] h = [Work("2026-09-12"), Holiday("2026-09-05")];
            Equal(false, Day("2026-09-12", s, h).IsRestDay, "historical same-day override");
            Equal(true, Day("2026-09-05", s, h).IsRestDay, "historical holiday");
            Equal(false, Day("2026-09-19", s, h).IsRestDay, "manual single anchor");
            Equal(true, Day("2026-09-26", s, h).IsRestDay, "future double");
            Equal(false, Week("2026-09-07", s, h).PlannedSingleRest, "historical natural parity");
        });
        Run("zero-rest single week flips original plan", () =>
        {
            var s = Settings(); var h = Work("2026-09-20");
            Equal(0, Week("2026-09-14", s, h).WeekendRestDays, "zero");
            Equal(true, Week("2026-09-14", s, h).IsSingleRestWeek, "checkbox keeps plan");
            Equal(true, Day("2026-09-26", s, h).IsRestDay, "next double");
        });
        Run("zero-rest double week flips original plan", () =>
        {
            var s = Settings(); HolidayEntry[] h = [Work("2026-09-26"), Work("2026-09-27")];
            Equal(0, Week("2026-09-21", s, h).WeekendRestDays, "zero");
            Equal(false, Week("2026-09-21", s, h).IsSingleRestWeek, "checkbox keeps plan");
            Equal(false, Day("2026-10-03", s, h).IsRestDay, "next single");
            Equal(true, Day("2026-10-10", s, h).IsRestDay, "following double");
        });
        Run("Weekly never alternates and respects adjustments", () =>
        {
            var s = Settings(pattern: RestPattern.Weekly);
            HolidayEntry[] h = [Holiday("2026-09-19"), Work("2026-09-20")];
            Equal(true, Day("2026-09-19", s, h).IsRestDay, "holiday Saturday");
            Equal(false, Day("2026-09-20", s, h).IsRestDay, "work Sunday");
            for (var i = 0; i < 10; i++)
            {
                Equal(false, RestSchedule.GetDaySchedule(D("2026-09-26").AddDays(i * 7), s, RestSchedule.CreateHolidayMap(h)).IsRestDay, "weekly Saturday");
                Equal(1, RestSchedule.GetWeekSchedule(D("2026-09-21").AddDays(i * 7), s, RestSchedule.CreateHolidayMap(h)).WeekendRestDays, "weekly tooltip type");
            }
        });
        Run("None keeps ordinary weekends and effective overrides", () =>
        {
            var s = Settings(pattern: RestPattern.None);
            Equal(true, Day("2026-09-19", s).IsRestDay, "ordinary Saturday");
            Equal(true, Day("2026-09-20", s).IsRestDay, "ordinary Sunday");
            Equal(false, Day("2026-09-19", s, Work("2026-09-19")).IsRestDay, "makeup Saturday");
            Equal(true, Day("2026-09-16", s, Holiday("2026-09-16")).IsRestDay, "weekday holiday");
            Equal(false, Day("2026-09-17", s).IsRestDay, "ordinary weekday");
        });
        Run("ShowHolidays false never changes decisions in any mode", () =>
        {
            foreach (var pattern in Enum.GetValues<RestPattern>())
            {
                var s = Settings(pattern: pattern);
                HolidayEntry[] h = [Work("2026-09-26"), Holiday("2026-09-16")];
                foreach (var date in new[] { "2026-09-16", "2026-09-26", "2026-10-03", "2026-10-10" })
                {
                    s.ShowHolidays = true; var visible = Day(date, s, h);
                    s.ShowHolidays = false; Equal(visible, Day(date, s, h), $"{pattern} {date}");
                }
            }
        });
        Run("both flags true always means work", () =>
        {
            var entry = Holiday("2026-09-26"); entry.IsWorkday = true;
            foreach (var pattern in Enum.GetValues<RestPattern>())
            {
                var day = Day("2026-09-26", Settings(pattern: pattern), entry);
                Equal(false, day.IsRestDay, "not rest");
                Equal(true, day.IsWorkdayAdjustment, "work marker");
                Equal(false, day.IsHoliday, "no holiday marker");
            }
            Equal(true, Day("2026-10-03", Settings(), entry).IsRestDay, "shift still applies");
        });
        Run("duplicate dates prefer work in either order for UI and schedule", () =>
        {
            foreach (var h in new[] { new[] { Holiday("2026-09-26"), Work("2026-09-26") }, new[] { Work("2026-09-26"), Holiday("2026-09-26") } })
            {
                var map = RestSchedule.CreateHolidayMap(h);
                Equal(true, map[D("2026-09-26")].IsWorkday, "shared UI map");
                Equal("Synthetic work", Day("2026-09-26", Settings(), h).Holiday!.Name!, "UI name");
                Equal(false, Day("2026-09-26", Settings(), h).IsRestDay, "work date");
                Equal(true, Day("2026-10-03", Settings(), h).IsRestDay, "shift");
            }
        });
        Run("neither flag is not an override; invalid dates ignored", () =>
        {
            HolidayEntry[] h = [new() { Date = "2026-09-19", IsHoliday = false },
                new() { Date = "2026-09-20", IsHoliday = false }, new() { Date = "bad", IsWorkday = true }];
            Equal(false, Day("2026-09-19", Settings(), h).IsRestDay, "working Saturday");
            Equal(true, Day("2026-09-20", Settings(), h).IsRestDay, "resting Sunday");
            Equal(true, Day("2026-09-26", Settings(), h).IsRestDay, "no shift");
            Equal(2, RestSchedule.CreateHolidayMap(h).Count, "invalid ignored");
            Equal(true, Day("2026-09-19", Settings(), h[0], Holiday("2026-09-19")).IsRestDay, "holiday outranks inert record");
        });
        Run("ten holiday-free weeks match original alternation for both rest days", () =>
        {
            foreach (var restDay in new[] { SingleRestDay.Saturday, SingleRestDay.Sunday })
            foreach (var single in new[] { true, false })
            {
                var s = Settings(single: single, restDay: restDay);
                for (var i = 0; i < 70; i++)
                {
                    var date = s.AnchorWeekStart!.Value.AddDays(i);
                    Equal(RestSchedule.IsRestDay(date, s.RestPattern, restDay, s.AnchorWeekStart, single),
                        RestSchedule.GetDaySchedule(date, s, RestSchedule.CreateHolidayMap([])).IsRestDay, date.ToString("yyyy-MM-dd"));
                }
            }
        });
        Run("Monday boundary and non-Monday anchor normalization", () =>
        {
            Equal(D("2026-09-14"), RestSchedule.WeekStart(D("2026-09-20")), "Sunday in previous week");
            Equal(D("2026-09-21"), RestSchedule.WeekStart(D("2026-09-21")), "Monday new week");
            Equal(false, Day("2026-09-19", Settings("2026-09-16")).IsRestDay, "anchor normalized");
        });
        Run("unrelated save preserves original anchor even if actual type differs", () =>
        {
            var s = Settings(); var before = s.AnchorWeekStart!.Value;
            var actual = Week("2026-09-21", s, Work("2026-09-26")).IsSingleRestWeek;
            s.ShowHolidays = false; s.Opacity = 0.8;
            RestSchedule.ApplyAnchorSelection(s, RestPattern.Alternate, D("2026-09-21"), actual, false);
            Equal(before, s.AnchorWeekStart!.Value, "anchor preserved");
            Equal(true, s.AnchorWeekIsSingleRest, "anchor type preserved");
        });
        Run("manual selection resets displayed week and overrides remain effective", () =>
        {
            var s = Settings();
            RestSchedule.ApplyAnchorSelection(s, RestPattern.Alternate, D("2026-09-21"), false, true);
            Equal(D("2026-09-21"), s.AnchorWeekStart!.Value, "displayed week");
            Equal(false, s.AnchorWeekIsSingleRest, "selected double");
            Equal(false, Day("2026-09-26", s, Work("2026-09-26")).IsRestDay, "work override");
            Equal(true, Day("2026-10-03", s, Work("2026-09-26")).IsRestDay, "shift after new anchor");
        });
        Run("entering Alternate discards stale anchor for both other modes", () =>
        {
            foreach (var previous in new[] { RestPattern.None, RestPattern.Weekly })
            {
                var s = Settings();
                RestSchedule.ApplyAnchorSelection(s, previous, D("2026-10-05"), false, false);
                Equal(D("2026-10-05"), s.AnchorWeekStart!.Value, "fresh displayed week");
                Equal(false, s.AnchorWeekIsSingleRest, "selection matches stored state");
            }
        });
        Run("missing anchor initialized; non-Alternate save does not reset", () =>
        {
            var s = Settings(); s.AnchorWeekStart = null;
            RestSchedule.ApplyAnchorSelection(s, RestPattern.Alternate, D("2026-10-05"), false, false);
            Equal(D("2026-10-05"), s.AnchorWeekStart!.Value, "initialized");
            Equal(true, s.AnchorWeekIsSingleRest, "missing anchor keeps original plan on unchanged save");
            s.RestPattern = RestPattern.Weekly;
            RestSchedule.ApplyAnchorSelection(s, RestPattern.Alternate, D("2026-10-12"), true, true);
            Equal(D("2026-10-05"), s.AnchorWeekStart!.Value, "weekly keeps anchor");
        });
        Run("missing anchor plus Sunday work preserves Saturday rest on unchanged save", () =>
        {
            var s = Settings(); s.AnchorWeekStart = null; s.AnchorWeekIsSingleRest = false;
            var monday = RestSchedule.WeekStart(DateTime.Today);
            var map = RestSchedule.CreateHolidayMap([new HolidayEntry
            {
                Date = monday.AddDays(6).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                IsWorkday = true, IsHoliday = false
            }]);
            var before = RestSchedule.GetWeekSchedule(monday, s, map);
            Equal(true, before.SaturdayIsRest, "original Saturday rests");
            Equal(false, before.SundayIsRest, "Sunday is adjusted work");
            Equal(true, before.IsSingleRestWeek, "actual week is single");
            RestSchedule.ApplyAnchorSelection(s, RestPattern.Alternate, monday, before.IsSingleRestWeek, false);
            var after = RestSchedule.GetWeekSchedule(monday, s, map);
            Equal(false, s.AnchorWeekIsSingleRest, "original double plan preserved");
            Equal(before, after, "unchanged save preserves actual weekend");
            RestSchedule.ApplyAnchorSelection(s, RestPattern.Alternate, monday, true, true);
            Equal(true, s.AnchorWeekIsSingleRest, "explicit choice still changes plan");
        });
        Run("hundred-year span with shared dictionary", () =>
        {
            var s = Settings("1926-09-13"); var map = RestSchedule.CreateHolidayMap([Work("1926-09-25")]);
            var watch = Stopwatch.StartNew();
            var firstSaturday = D("2026-09-19");
            var first = RestSchedule.GetDaySchedule(firstSaturday, s, map).IsRestDay;
            for (var i = 0; i < 42; i++) RestSchedule.GetDaySchedule(firstSaturday.AddDays(i), s, map);
            Equal(!first, RestSchedule.GetDaySchedule(firstSaturday.AddDays(7), s, map).IsRestDay, "still alternates after 100 years");
            Console.WriteLine($"100-year / 44 day queries: {watch.ElapsedMilliseconds} ms");
        });
        return Report();
    }

    private static int Report()
    {
        Console.WriteLine($"Tests: {tests}, assertions: {assertions}, failures: {failures}");
        return failures == 0 ? 0 : 1;
    }
}
