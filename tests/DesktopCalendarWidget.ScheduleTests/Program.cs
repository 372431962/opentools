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
        Run("non-Chinese UI hides lunar and holiday text without editing stored switches", () =>
        {
            var previous = Loc.CurrentCulture.Name;
            try
            {
                var s = Settings();
                Loc.Apply(Loc.ChineseTag);
                Equal(true, s.ShowLunarEffective, "chinese keeps lunar");
                Equal(true, s.ShowHolidaysEffective, "chinese keeps holiday text");
                s.Language = Loc.EnglishTag;
                Loc.Apply(Loc.EnglishTag);
                Equal(false, s.ShowLunarEffective, "english hides lunar");
                Equal(false, s.ShowHolidaysEffective, "english hides holiday text");
                Equal(true, s.ShowLunar, "stored lunar switch untouched");
                Equal(true, s.ShowHolidays, "stored holiday switch untouched");
                Loc.Apply("pt-BR");
                Equal(true, s.ShowLunarEffective, "unknown tag falls back to chinese");
            }
            finally { Loc.Apply(previous); }
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
            s.ShowHolidays = false; s.Width = 612;
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

        // ---- 日程：定位方式互斥、按天查询、类型与展示顺序 ----
        Run("semester week normalizes to Monday and bounds the range", () =>
        {
            Equal(1, Agenda.WeekIndex(new DateTime(2026, 9, 9), new DateTime(2026, 9, 9), 16), "start date midweek is week one");
            Equal(null, Agenda.WeekIndex(new DateTime(2026, 9, 6), new DateTime(2026, 9, 7), 16), "before semester");
            Equal(null, Agenda.WeekIndex(new DateTime(2026, 12, 28), new DateTime(2026, 9, 7), 16), "after semester");
        });
        Run("a schedule is either dated or recurring, never both", () =>
        {
            Equal(true, Agenda.IsValid(Meeting(1)), "one-off");
            Equal(true, Agenda.IsValid(Course(1)), "recurring");
            // 两套定位都填或都不填都不是合法状态，否则「这天到底算不算有课」无从判断。
            Equal(false, Agenda.IsValid(new ScheduleItem { Kind = ScheduleKind.Course, Title = "X",
                Date = new DateTime(2026, 9, 8), Recurrence = new WeeklyRecurrence { DayOfWeek = 1 } }), "both filled");
            Equal(false, Agenda.IsValid(new ScheduleItem { Kind = ScheduleKind.Course, Title = "X" }), "neither filled");
            Equal(false, Agenda.IsValid(new ScheduleItem { Kind = ScheduleKind.Meeting, Title = "  " }), "blank title");
            Equal(false, Agenda.IsValid(new ScheduleItem { Id = Guid.Empty, Kind = ScheduleKind.Meeting, Title = "X",
                Date = new DateTime(2026, 9, 8) }), "empty id");
            Equal(false, Agenda.IsValid(Course(0)), "weekday out of range");
        });
        Run("all-day and time-pending are different states", () =>
        {
            // 一次性没时刻 = 全天；重复没时刻 = 时间待定，不能混成同一个显示。
            var untimed = new ScheduleItem { Kind = ScheduleKind.Meeting, Title = "Offsite", Date = new DateTime(2026, 9, 8) };
            Equal(true, Agenda.IsAllDay(untimed), "one-off without time is all day");
            Equal(false, Agenda.IsAllDay(Course(1)), "recurring without time is not all day");
            Equal(false, Agenda.IsAllDay(Course(1)), "recurring with time is not all day");
            Equal(true, Agenda.IsTimePending(PendingCourse()), "recurring without time is pending");
            Equal(false, Agenda.IsTimePending(untimed), "one-off without time is not pending");
            Equal("09:00-10:00", Agenda.TimeLabel(Timed(Meeting(1))), "time label");
            Equal("", Agenda.TimeLabel(untimed), "no time label");
        });
        Run("courses filter by odd even weeks and sort by time", () =>
        {
            var mondayWeek1 = new DateTime(2026, 9, 7);
            var index = new AgendaIndex([
                Timed(Course(1, "Even", ScheduleWeekType.Even), 12, 13),
                Timed(Course(1, "Zeta"), 8, 9),
                Timed(Course(1, "Odd", ScheduleWeekType.Odd), 14, 15),
                Timed(Course(1, "Alpha"), 8, 9)]);
            Equal("Alpha,Zeta,Odd", Names(index, mondayWeek1, mondayWeek1, 16), "week one order");
            Equal("Alpha,Zeta,Even", Names(index, mondayWeek1.AddDays(7), mondayWeek1, 16), "week two order");
        });
        Run("one-off and recurring share the same query path", () =>
        {
            var monday = new DateTime(2026, 9, 7);
            var index = new AgendaIndex([
                Course(1, "Class"),
                Timed(Meeting(monday, "Standup")),
                Meeting(monday.AddDays(1), "Offsite")]);
            Equal("Class,Standup", Names(index, monday, monday, 16), "same day merges both kinds");
            Equal("Offsite", Names(index, monday.AddDays(1), monday, 16), "one-off on another day");
            // 学期外重复条目不出现，但一次性事件与学期无关，仍然要显示。
            Equal("", Names(index, monday.AddDays(30), monday, 16), "recurring stops outside the semester");
            Equal("Offsite", Names(index, monday.AddDays(1), monday.AddDays(30), 16), "one-off ignores the semester");
        });
        Run("display order puts all-day first and time-pending last", () =>
        {
            var day = new DateTime(2026, 9, 8);
            var index = new AgendaIndex([
                Timed(Meeting(day, "Evening"), 19, 20),
                PendingCourse(2),
                Meeting(day, "AllDay"),
                Timed(Meeting(day, "Morning"))]);
            Equal("AllDay,Morning,Evening,Pending", Names(index, day, day, 16), "bucket order");
        });
        Run("normalize dedupes by id and trims text", () =>
        {
            var id = Guid.NewGuid();
            var normalized = Agenda.Normalize([null,
                new ScheduleItem { Id = id, Kind = ScheduleKind.Meeting, Title = "  Lab  ",
                    Date = new DateTime(2026, 9, 8, 22, 30, 0), Location = " A1 ", Notes = "  " },
                new ScheduleItem { Id = id, Kind = ScheduleKind.Other, Title = "Dup", Date = new DateTime(2026, 9, 8) },
                new ScheduleItem { Kind = ScheduleKind.Meeting, Title = "", Date = new DateTime(2026, 9, 8) }]);
            Equal(1, normalized.Count, "only the first of a duplicate id survives");
            Equal("Lab", normalized[0].Title, "trimmed title");
            Equal("A1", normalized[0].Location, "trimmed location");
            Equal(null, normalized[0].Notes, "blank notes become null");
            Equal(new DateTime(2026, 9, 8), normalized[0].Date, "date truncated to midnight");
            // 时区无关：否则落盘会带 "+08:00"，手改 JSON 时很迷惑。
            Equal(DateTimeKind.Unspecified, normalized[0].Date!.Value.Kind, "date carries no timezone");
        });
        Run("period times parse and expose both ends", () =>
        {
            var times = Agenda.ParsePeriodTimes("1=08:00-08:45;2=08:55-09:40;bad=x;31=10:00-11:00;3=oops;4=10:00-09:00");
            Equal(new TimeOnly(8, 0), Agenda.PeriodStart(times, 1), "period start");
            Equal(new TimeOnly(8, 45), Agenda.PeriodEnd(times, 1), "period end");
            Equal(new TimeOnly(9, 40), Agenda.PeriodEnd(times, 2), "second period end");
            Equal(null, Agenda.PeriodStart(times, 3), "invalid range ignored");
            Equal(null, Agenda.PeriodStart(times, 31), "period upper bound");
        });
        Run("schedule kinds parse leniently and have distinct colors", () =>
        {
            Equal(ScheduleKind.Course, ScheduleKinds.Parse("course"), "case insensitive");
            Equal(ScheduleKind.Other, ScheduleKinds.Parse("nonsense"), "unknown falls back to Other");
            Equal(ScheduleKind.Other, ScheduleKinds.Parse(null), "null falls back to Other");
            Equal(7, ScheduleKinds.All.Count, "seven kinds");
            Equal(ScheduleKinds.All.Distinct().Count(), ScheduleKinds.All.Select(ScheduleKinds.Color).Distinct().Count(),
                "every kind has its own color");
            Equal(true, ScheduleKinds.SupportsRecurrence(ScheduleKind.Course), "only courses repeat");
            Equal(false, ScheduleKinds.SupportsRecurrence(ScheduleKind.Meeting), "meetings do not repeat");
        });
        Run("course reminders fire once, cross midnight and key on id", () =>
        {
            var monday = new DateTime(2026, 9, 28);
            var first = Timed(Course(1, "Algebra"), 0, 1);
            var index = new AgendaIndex([first]);
            var now = monday.AddDays(-1).AddHours(23).AddMinutes(45);
            var due = ScheduleReminders.Due(now, index, monday, 2, 60, new HashSet<string>());
            Equal(1, due.Count, "previous-day reminder");
            Equal(0, ScheduleReminders.Due(now, index, monday, 2, 60, new HashSet<string> { due[0].Key }).Count, "deduplicated");
            Equal(0, ScheduleReminders.Due(monday.AddMinutes(31), index, monday, 2, 60, new HashSet<string>()).Count, "not after start");
            Equal(1, ScheduleReminders.Due(monday.AddSeconds(30), index, monday, 2, 0, new HashSet<string>()).Count,
                "zero minute lead at start");
            Equal(0, ScheduleReminders.Due(monday.AddMinutes(2), index, monday, 2, 0, new HashSet<string>()).Count,
                "zero minute window closes");
            // 时间待定的课提醒不了：拿不出开始时刻就不该假装有。
            Equal(0, ScheduleReminders.Due(now, new AgendaIndex([PendingCourse(1, "NoTime")]), monday, 2, 60, new HashSet<string>()).Count,
                "time-pending course does not remind");
            Equal(0, ScheduleReminders.Due(now, new AgendaIndex([Timed(Meeting(monday, "Sync"))]), monday, 2, 60, new HashSet<string>()).Count,
                "non-course does not remind");
            // 改名不该让去重失效，所以键里带的是 Id 而不是标题：同一个条目改了名，键不变。
            var before = due[0].Key;
            first.Title = "Algebra II";
            Equal(before, ScheduleReminders.ReminderKey(first, monday), "rename does not change the key");
        });
        Run("legacy courses and events migrate into one file", () => WithScratchFolder("legacy-migration", folder =>
        {
            File.WriteAllText(Path.Combine(folder, "courses.json"),
                "[{\"Name\":\"  高等数学  \",\"DayOfWeek\":1,\"StartPeriod\":1,\"EndPeriod\":2,\"Location\":\"A101\"," +
                "\"Teacher\":\"王老师\",\"WeekType\":\"ODD\",\"StartWeek\":3,\"EndWeek\":12}," +
                "{\"Name\":\"无时刻课\",\"DayOfWeek\":3,\"StartPeriod\":9,\"EndPeriod\":10}," +
                "{\"Name\":\"\",\"DayOfWeek\":1}]");
            var eventId = Guid.NewGuid();
            File.WriteAllText(Path.Combine(folder, "events.json"),
                "[{\"Id\":\"" + eventId + "\",\"Title\":\"Exam\",\"Date\":\"2026-09-28T00:00:00\"," +
                "\"AllDay\":false,\"StartTime\":\"13:00:00\",\"EndTime\":\"14:00:00\",\"Location\":\"B2\"}," +
                "{\"Id\":\"" + Guid.NewGuid() + "\",\"Title\":\"\",\"Date\":\"2026-09-28T00:00:00\"}]");
            var result = LegacyMigration.Run(folder, Agenda.ParsePeriodTimes("1=08:00-08:45;2=08:55-09:40"));
            Equal(true, result.Migrated, "legacy files detected");
            Equal(2, result.DroppedCourses, "one blank course and one blank event dropped");
            Equal(1, result.TimePendingCourses, "course without period times is pending");
            Equal(3, result.Items.Count, "two courses and one event survive");
            var math = result.Items.First(x => x.Title == "高等数学");
            Equal(ScheduleKind.Course, math.Kind, "course kind");
            Equal(new TimeOnly(8, 0), math.StartTime, "period 1 became 08:00");
            Equal(new TimeOnly(9, 40), math.EndTime, "period 2 end became 09:40");
            Equal("odd", math.Recurrence!.WeekType, "week type normalized");
            Equal(3, math.Recurrence.StartWeek, "start week kept");
            Equal("王老师", math.Notes, "teacher becomes notes");
            Equal(true, Agenda.IsTimePending(result.Items.First(x => x.Title == "无时刻课")),
                "course without times is pending, not all-day");
            var exam = result.Items.First(x => x.Title == "Exam");
            Equal(eventId, exam.Id, "event keeps its id across migration");
            Equal(ScheduleKind.Other, exam.Kind, "legacy events become Other");
            Equal(new TimeOnly(13, 0), exam.StartTime, "event time kept");
        }));
        Run("schedule storage round trips and leaves no temp file", () => WithScratchFolder("schedule-storage", folder =>
        {
            var store = new SettingsService(folder);
            var course = Timed(Course(1, "  Algebra  "));
            var meeting = Timed(Meeting(new DateTime(2026, 9, 28), "Sync"));
            Equal(true, store.SaveSchedules([course, meeting]), "initial save");
            var restored = store.LoadSchedules(out var dropped, out var pending);
            Equal(2, restored.Count, "round trip count");
            Equal(0, dropped, "nothing dropped");
            Equal(0, pending, "nothing pending");
            Equal("Algebra", restored[0].Title, "trimmed title");
            Equal(ScheduleKind.Course, restored[0].Kind, "kind round trips");
            Equal(true, restored[0].Recurrence is not null, "recurrence round trips");
            Equal(meeting.Id, restored[1].Id, "stable identity");
            Equal(false, store.SaveSchedules([course,
                    new ScheduleItem { Kind = ScheduleKind.Meeting, Title = "", Date = DateTime.Today }]),
                "invalid save refused");
            Equal(2, store.LoadSchedules().Count, "previous file preserved");
            Equal(false, File.Exists(Path.Combine(folder, "schedules.json.tmp")), "temp file cleaned up");
        }));
        Run("legacy files migrate on first load only", () => WithScratchFolder("auto-migrate", folder =>
        {
            File.WriteAllText(Path.Combine(folder, "courses.json"),
                "[{\"Name\":\"高等数学\",\"DayOfWeek\":1,\"StartPeriod\":1,\"EndPeriod\":2}]");
            var store = new SettingsService(folder) { PeriodTimesForMigration = "1=08:00-08:45;2=08:55-09:40" };
            Equal(1, store.LoadSchedules().Count, "migrated on first load");
            Equal(true, File.Exists(Path.Combine(folder, "schedules.json")), "new file written");
            // 第二次起读新文件，旧文件删掉也不影响。
            File.Delete(Path.Combine(folder, "courses.json"));
            Equal(1, new SettingsService(folder).LoadSchedules().Count, "still one schedule after legacy file is gone");
        }));
        Run("loading reports how many records were dropped", () => WithScratchFolder("dropped-count", folder =>
        {
            File.WriteAllText(Path.Combine(folder, "schedules.json"),
                "[{\"Id\":\"" + Guid.NewGuid() + "\",\"Kind\":1,\"Title\":\"Review\",\"Date\":\"2026-09-28T00:00:00\"}," +
                "{\"Id\":\"" + Guid.NewGuid() + "\",\"Kind\":1,\"Title\":\"\",\"Date\":\"2026-09-28T00:00:00\"}," +
                "{\"Id\":\"" + Guid.NewGuid() + "\",\"Kind\":1,\"Title\":\"Both\",\"Date\":\"2026-09-28T00:00:00\"," +
                "\"Recurrence\":{\"DayOfWeek\":1}},null]");
            var items = new SettingsService(folder).LoadSchedules(out var dropped, out var pending);
            Equal(1, items.Count, "only the valid record survives");
            Equal(3, dropped, "null, blank title and both-locations records reported");
            Equal(0, pending, "no pending records");
            Equal(ScheduleKind.Meeting, items[0].Kind, "numeric kind still deserializes");
        }));
        Run("drawer grows rightwards when the screen has room", () =>
        {
            // 1920 宽的屏幕，挂件在左边：往右长，位置一点不动。
            var opened = DrawerGeometry.Open(100, 640, 0, 1920, 300);
            Equal(100, opened.Left, "position untouched");
            Equal(940, opened.Width, "window grew by the drawer width");
            Equal(false, opened.OnLeft, "stayed on the right");
            var closed = DrawerGeometry.Close(opened, 640);
            Equal(100, closed.Left, "close keeps the position");
            Equal(640, closed.Width, "close restores the width");
        });
        Run("drawer flips left when the widget hugs the right edge", () =>
        {
            // 挂件贴着 1920 的右缘：右边放不下，往左长，位置左移 300。
            var left = 1920 - 640 - 2;
            var opened = DrawerGeometry.Open(left, 640, 0, 1920, 300);
            Equal(true, opened.OnLeft, "flipped to the left");
            Equal(left - 300, opened.Left, "window moved left by the drawer width");
            Equal(940, opened.Width, "window still grew by the drawer width");
            var closed = DrawerGeometry.Close(opened, 640);
            Equal(left, closed.Left, "close moves the window back");
            Equal(640, closed.Width, "close restores the width");
        });
        Run("drawer refuses to flip when the left has no room either", () =>
        {
            // 两边都挤不下时宁可不翻边，也不要把日历推出屏幕。
            var opened = DrawerGeometry.Open(30, 640, 0, 1920, 300);
            Equal(false, opened.OnLeft, "stays on the right");
            Equal(30, opened.Left, "position untouched");
        });
        Run("secondary monitors with a negative origin are handled", () =>
        {
            // 显示器摆在主屏左边时 VirtualScreenLeft 是负数，右缘计算不能用 Left + width 直接比。
            var opened = DrawerGeometry.Open(-1200, 640, -1280, 1280, 300);
            Equal(false, opened.OnLeft, "plenty of room on the right of a left-hand monitor");
            Equal(-1200, opened.Left, "position untouched");
            var flipped = DrawerGeometry.Open(-650, 640, -1280, 1280, 300);
            Equal(true, flipped.OnLeft, "flips when hugging that monitor's right edge");
        });

        Run("switching days on an open drawer never resizes the window", () =>
        {
            // 回归：连点日期时窗口一格一格变宽。就是因为这里又跑了一次 Open。
            var opened = DrawerGeometry.Open(100, 640, 0, 1920, 300);
            var afterOne = DrawerGeometry.SwitchDay(opened);
            var afterTwo = DrawerGeometry.SwitchDay(afterOne);
            Equal(100, afterTwo.Left, "position unchanged after two day switches");
            Equal(940, afterTwo.Width, "width unchanged after two day switches");
            Equal(false, afterTwo.OnLeft, "side unchanged");
            // 对照：直觉写法（对已加宽的宽度再 Open 一次）正是要避免的行为。
            var wrong = DrawerGeometry.Open(afterOne.Left, afterOne.Width, 0, 1920, 300);
            Equal(1240, wrong.Width, "opening on an already-open window would grow again");
        });
        Run("closing after switching days returns to the original geometry", () =>
        {
            var opened = DrawerGeometry.Open(100, 640, 0, 1920, 300);
            var switched = DrawerGeometry.SwitchDay(DrawerGeometry.SwitchDay(opened));
            var closed = DrawerGeometry.Close(switched, 640);
            Equal(100, closed.Left, "position restored");
            Equal(640, closed.Width, "width restored");
        });

        return Report();
    }

    /// <summary>在临时目录里跑一次存储往返，结束即删；不落在用户数据目录。</summary>
    private static void WithScratchFolder(string label, Action<string> body)
    {
        var folder = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? AppContext.BaseDirectory,
            $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try { body(folder); }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    private static ScheduleItem Meeting(int dayOffset, string title = "Meeting") =>
        Meeting(new DateTime(2026, 9, 7).AddDays(dayOffset - 1), title);

    private static ScheduleItem Meeting(DateTime date, string title = "Meeting") => new()
        {
            Kind = ScheduleKind.Meeting,
            Title = title,
            Date = date.Date,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(10, 0)
        };

        private static ScheduleItem Course(int dayOfWeek, string title = "Class",
            string weekType = ScheduleWeekType.All) => new()
        {
            Kind = ScheduleKind.Course,
            Title = title,
            StartTime = new TimeOnly(8,  0),
            EndTime = new TimeOnly(9, 40),
            Recurrence = new WeeklyRecurrence { DayOfWeek = dayOfWeek, StartWeek = 1, EndWeek = 16, WeekType = weekType }
        };

    /// <summary>一门没配时刻的课：界面上显示「时间待定」，不是全天。</summary>
    private static ScheduleItem PendingCourse(int dayOfWeek = 1, string title = "Pending") => new()
    {
        Kind = ScheduleKind.Course,
        Title = title,
        Recurrence = new WeeklyRecurrence { DayOfWeek = dayOfWeek, StartWeek = 1, EndWeek = 16 }
    };

    private static ScheduleItem Timed(ScheduleItem item, int startHour = 9, int endHour = 10)
        {
            item.StartTime = new TimeOnly(startHour, 0);
            item.EndTime = new TimeOnly(endHour, 0);
            return item;
        }

    private static string Names(AgendaIndex index, DateTime date, DateTime? semesterStart, int weeks) =>
        string.Join(',', index.ItemsForDate(date, semesterStart, weeks).Select(x => x.Title));

    private static int Report()
    {
        Console.WriteLine($"Tests: {tests}, assertions: {assertions}, failures: {failures}");
        return failures == 0 ? 0 : 1;
    }
}
