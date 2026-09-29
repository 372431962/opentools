using DesktopCalendarWidget;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class Program
{
    private static int failures;
    private static int tests;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // 测试自建 Application 不会跑 App.xaml，但面板和编辑窗口的 XAML 用 StaticResource
        // 引用了主题里的画刷。Theme.xaml 是纯 ResourceDictionary，合并进来不会触发 App 构造。
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/DesktopCalendarWidget;component/Theme.xaml", UriKind.Absolute)
        });
        // 模态确认框在无人值守时永远等不到回答，测试里一律自动同意。
        ScheduleEditorWindow.Confirm = (_, _, _, _) => true;
        var day = new DateTime(2026, 9, 28);

        Run("editor cancel keeps caller unchanged", () =>
        {
            var original = TimedMeeting(day, "Exam");
            var editor = Hidden(new ScheduleEditorWindow([original], null, day));
            var accepted = Show(editor, () =>
            {
                Box(editor, "TitleBox").Text = "Changed";
                Click(editor, "CancelButton");
            });
            Require(accepted != true && original.Title == "Exam" && editor.Items.Count == 0, "cancel altered schedule");
        });
        Run("editor saves a new timed meeting", () =>
        {
            var editor = Hidden(new ScheduleEditorWindow([], null, day));
            var accepted = Show(editor, () =>
            {
                Box(editor, "TitleBox").Text = "Lab";
                Click(editor, "SaveButton");
            });
            Require(accepted == true && editor.Items.Count == 1 && editor.Items[0].Title == "Lab" &&
                editor.Items[0].Kind == ScheduleKind.Meeting &&
                editor.Items[0].StartTime == new TimeOnly(9, 0) &&
                editor.Items[0].Date == day, "timed meeting not saved");
        });
        Run("editing an existing item keeps its id and replaces it", () =>
        {
            var original = TimedMeeting(day, "Exam");
            var editor = Hidden(new ScheduleEditorWindow([original], original, day));
            var accepted = Show(editor, () =>
            {
                Box(editor, "TitleBox").Text = "Final";
                Click(editor, "SaveButton");
            });
            Require(accepted == true && editor.Items.Count == 1 &&
                editor.Items[0].Id == original.Id && editor.Items[0].Title == "Final", "edit did not replace in place");
        });
        Run("delete removes the item from the result", () =>
        {
            var original = TimedMeeting(day, "Exam");
            var keep = TimedMeeting(day, "Standup");
            var editor = Hidden(new ScheduleEditorWindow([original, keep], original, day));
            var accepted = Show(editor, () => Click(editor, "DeleteButton"));
            Require(accepted == true && editor.Items.Count == 1 && editor.Items[0].Id == keep.Id, "delete left the item behind");
        });
        Run("choosing course switches the form to weekly fields", () =>
        {
            var editor = Hidden(new ScheduleEditorWindow([], null, day));
            var accepted = Show(editor, () =>
            {
                Box(editor, "TitleBox").Text = "Math";
                ((ComboBox)editor.FindName("KindCombo")!).SelectedValue = ScheduleKind.Course;
                Box(editor, "StartWeekBox").Text = "3";
                Box(editor, "EndWeekBox").Text = "12";
                Click(editor, "SaveButton");
            });
            var saved = editor.Items.Single();
            Require(accepted == true, "save rejected");
            Require(saved.Kind == ScheduleKind.Course, $"kind was {saved.Kind}");
            Require(saved.Date is null, $"one-off date leaked in: {saved.Date}");
            Require(saved.Recurrence is not null, "recurrence missing");
            // 用模式匹配而不是先 Require 再解引用：编译器不知道 Require 会抛，拿不到非空推断。
            Require(saved.Recurrence is { DayOfWeek: 1, StartWeek: 3, EndWeek: 12 },
                $"week range was {saved.Recurrence?.DayOfWeek} {saved.Recurrence?.StartWeek}-{saved.Recurrence?.EndWeek}");
            Require(saved.StartTime == new TimeOnly(9, 0), $"start was {saved.StartTime}");
        });
        Run("a course with no time saves as time-pending, not all day", () =>
        {
            var editor = Hidden(new ScheduleEditorWindow([], null, day));
            var accepted = Show(editor, () =>
            {
                Box(editor, "TitleBox").Text = "NoTime";
                ((ComboBox)editor.FindName("KindCombo")!).SelectedValue = ScheduleKind.Course;
                Box(editor, "StartBox").Text = "";
                Box(editor, "EndBox").Text = "";
                Click(editor, "SaveButton");
            });
            var saved = editor.Items.Single();
            Require(accepted == true && Agenda.IsTimePending(saved) && !Agenda.IsAllDay(saved),
                "timeless course should be time-pending");
        });
        Run("an all-day meeting drops its time range", () =>
        {
            var editor = Hidden(new ScheduleEditorWindow([], null, day));
            var accepted = Show(editor, () =>
            {
                Box(editor, "TitleBox").Text = "Offsite";
                ((CheckBox)editor.FindName("AllDayCheck")!).IsChecked = true;
                Click(editor, "SaveButton");
            });
            var saved = editor.Items.Single();
            Require(accepted == true && Agenda.IsAllDay(saved) && saved.StartTime is null,
                "all-day meeting still carries a time");
        });
        PanelSmoke.Run(Run);

        app.Shutdown();
        Console.WriteLine($"Tests: {tests}, failures: {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static ScheduleItem TimedMeeting(DateTime date, string title) => new()
    {
        Kind = ScheduleKind.Meeting,
        Title = title,
        Date = date.Date,
        StartTime = new TimeOnly(13, 0),
        EndTime = new TimeOnly(14, 0)
    };

    private static T Hidden<T>(T window) where T : Window
    {
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Opacity = 0;
        return window;
    }

    private static bool? Show(Window window, Action action)
    {
        Exception? error = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                action();
                // 动作跑完但对话框还开着，说明校验没过、DialogResult 没被设。
                // 这里直接收掉，否则 ShowDialog 会一直等下去，测试表现为超时而不是失败。
                if (window.DialogResult is null) window.DialogResult = false;
            }
            catch (Exception ex) { error = ex; window.DialogResult = false; }
        }));
        var accepted = window.ShowDialog();
        if (error is not null) throw error;
        return accepted;
    }

    private static TextBox Box(Window window, string name) =>
        window.FindName(name) as TextBox ?? throw new InvalidOperationException($"Missing {name}");

    private static void Click(Window window, string name)
    {
        var button = window.FindName(name) as Button ?? throw new InvalidOperationException($"Missing {name}");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Run(string name, Action action)
    {
        tests++;
        try { action(); Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
    }

    internal static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
