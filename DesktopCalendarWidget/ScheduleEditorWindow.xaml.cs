using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace DesktopCalendarWidget;

/// <summary>
/// 日程编辑窗口，课程与其它类型共用一套界面：选到「课程」时日期换成星期 + 起止周 + 单双周，
/// 其余类型就是普通的定时/全天事件。构造时拿不到即空表，取消不外泄，保存才写回副本。
/// </summary>
public partial class ScheduleEditorWindow : Window
{
    private readonly DateTime defaultDate;
    private readonly ScheduleItem? original;
    private bool loading = true;
    private bool dirty;

    /// <summary>保存前一直为空；返回的列表与传进来的对象不共享实例。</summary>
    public List<ScheduleItem> Items { get; private set; } = [];

    public ScheduleEditorWindow(IEnumerable<ScheduleItem> items, ScheduleItem? editing, DateTime defaultDate)
    {
        ArgumentNullException.ThrowIfNull(items);
        InitializeComponent();
        this.defaultDate = defaultDate.Date;
        original = editing;

        Heading.Text = editing is null ? Loc.ScheduleNewTitle : Loc.ScheduleEditTitle;
        KindLabel.Content = Loc.ScheduleKindLabel;
        TitleLabel.Content = Loc.ScheduleTitleLabel;
        AllDayCheck.Content = Loc.EventAllDay;
        StartLabelText.Text = Loc.EventStartLabel;
        EndLabelText.Text = Loc.EventEndLabel;
        DayLabel.Content = Loc.ScheduleDayLabel;
        StartWeekLabelText.Text = Loc.ScheduleStartWeekLabel;
        EndWeekLabelText.Text = Loc.ScheduleEndWeekLabel;
        WeekTypeLabel.Content = Loc.ScheduleWeekTypeLabel;
        LocationLabel.Content = Loc.ScheduleLocationLabel;
        NotesLabel.Content = Loc.ScheduleNotesLabel;
        DateHeading.Text = Loc.ScheduleDateHeading;
        SaveButton.Content = Loc.SaveButton;
        CancelButton.Content = Loc.CancelButton;
        DeleteButton.Content = Loc.DeleteButton;

        foreach (var kind in ScheduleKinds.All) KindCombo.Items.Add(kind);
        foreach (var day in Loc.CourseDayNames()) DayCombo.Items.Add(day);
        WeekTypeCombo.Items.Add(ScheduleWeekType.All);
        WeekTypeCombo.Items.Add(ScheduleWeekType.Odd);
        WeekTypeCombo.Items.Add(ScheduleWeekType.Even);

        if (editing is not null) FillForm(editing);
        else FillNew();

        loadedItems = Agenda.Normalize(items.Cast<ScheduleItem?>());
        Loaded += (_, _) => TitleBox.Focus();
    }

    private List<ScheduleItem> loadedItems = [];

    private void FillNew()
    {
        loading = true;
        KindCombo.SelectedItem = ScheduleKind.Meeting;
        TitleBox.Text = "";
        DatePicker.SelectedDate = defaultDate;
        AllDayCheck.IsChecked = false;
        StartBox.Text = "09:00";
        EndBox.Text = "10:00";
        DayCombo.SelectedIndex = Agenda.WeekdayNumber(defaultDate) - 1;
        StartWeekBox.Text = "1";
        EndWeekBox.Text = "0";
        WeekTypeCombo.SelectedIndex = 0;
        LocationBox.Text = "";
        NotesBox.Text = "";
        ErrorText.Text = "";
        ApplyKindVisibility();
        dirty = false;
        loading = false;
    }

    private void FillForm(ScheduleItem item)
    {
        loading = true;
        KindCombo.SelectedItem = Enum.IsDefined(item.Kind) ? item.Kind : ScheduleKind.Other;
        TitleBox.Text = item.Title;
        DatePicker.SelectedDate = item.Date ?? defaultDate;
        AllDayCheck.IsChecked = item.StartTime is null && item.EndTime is null;
        StartBox.Text = item.StartTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "09:00";
        EndBox.Text = item.EndTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "10:00";
        DayCombo.SelectedIndex = (item.Recurrence?.DayOfWeek ?? Agenda.WeekdayNumber(defaultDate)) - 1;
        StartWeekBox.Text = (item.Recurrence?.StartWeek ?? 1).ToString(CultureInfo.InvariantCulture);
        EndWeekBox.Text = (item.Recurrence?.EndWeek ?? 0).ToString(CultureInfo.InvariantCulture);
        WeekTypeCombo.SelectedIndex = WeekTypeCombo.Items.IndexOf(ScheduleWeekType.Normalize(item.Recurrence?.WeekType));
        LocationBox.Text = item.Location ?? "";
        NotesBox.Text = item.Notes ?? "";
        ErrorText.Text = "";
        ApplyKindVisibility();
        dirty = false;
        loading = false;
    }

    /// <summary>类型决定这一条是重复课程还是一次性事件，字段随之整组切换。</summary>
    private void ApplyKindVisibility()
    {
        var recurring = KindCombo.SelectedItem is ScheduleKind kind && ScheduleKinds.SupportsRecurrence(kind);
        OneOffFields.Visibility = recurring ? Visibility.Collapsed : Visibility.Visible;
        RecurringFields.Visibility = recurring ? Visibility.Visible : Visibility.Collapsed;
        if (recurring)
        {
            // 重复课程没有「全天」概念：没有时刻就是时间待定，界面上由 TimeRow 禁用状态表达。
            TimeRow.IsEnabled = true;
            return;
        }
        TimeRow.IsEnabled = AllDayCheck.IsChecked != true;
    }

    private void KindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 构造期 InitializeComponent 之后 KindCombo 还没有内容，选中项为 null 是正常中间态。
        if (KindCombo.SelectedItem is null) return;
        ApplyKindVisibility();
        MarkDirty(this, new RoutedEventArgs());
    }

    private void AllDayCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (TimeRow is null) return;
        TimeRow.IsEnabled = AllDayCheck.IsChecked != true;
        MarkDirty(this, e);
    }

    private void MarkDirty(object sender, RoutedEventArgs e)
    {
        if (!loading) dirty = true;
    }

    /// <summary>
    /// 确认框的出口。默认弹 MessageBox，测试里换成自动回答——模态框在无人值守时会一直等下去。
    /// </summary>
    public static Func<string, string, MessageBoxButton, MessageBoxImage, bool> Confirm =
        (message, title, button, image) => MessageBox.Show(message, title, button, image) == MessageBoxResult.Yes;

    private bool CanDiscard() => !dirty || Confirm(Loc.ScheduleDiscardPrompt, Heading.Text, MessageBoxButton.YesNo, MessageBoxImage.Question);

    private bool TryRead(out ScheduleItem item)
    {
        item = new ScheduleItem();
        var title = TitleBox.Text.Trim();
        if (title.Length == 0) { return Fail(Loc.ScheduleTitleRequired, TitleBox); }
        var kind = KindCombo.SelectedItem as ScheduleKind? ?? ScheduleKind.Other;

        var candidate = new ScheduleItem
        {
            Id = original?.Id ?? Guid.NewGuid(),
            Kind = kind,
            Title = title,
            Location = NullIfEmpty(LocationBox.Text),
            Notes = NullIfEmpty(NotesBox.Text),
            Color = original?.Color
        };

        if (ScheduleKinds.SupportsRecurrence(kind))
        {
            if (DayCombo.SelectedIndex is < 0 or > 6) return Fail(Loc.CourseDayRequired, DayCombo);
            if (!TryNumber(StartWeekBox, 1, int.MaxValue, Loc.ScheduleStartWeekLabel, out var firstWeek) ||
                !TryNumber(EndWeekBox, 0, int.MaxValue, Loc.ScheduleEndWeekLabel, out var lastWeek)) return false;
            if (lastWeek != 0 && lastWeek < firstWeek) return Fail(Loc.CourseWeekRangeInvalid, EndWeekBox);
            candidate.Recurrence = new WeeklyRecurrence
            {
                DayOfWeek = DayCombo.SelectedIndex + 1,
                StartWeek = firstWeek,
                EndWeek = lastWeek,
                WeekType = WeekTypeCombo.SelectedItem as string ?? ScheduleWeekType.All
            };
            // 课程的时刻可以留空：表示「时间待定」，不是全天。
            if (TryTime(StartBox, out var start) && TryTime(EndBox, out var end))
            {
                candidate.StartTime = start;
                candidate.EndTime = end;
            }
        }
        else
        {
            if (DatePicker.SelectedDate is not DateTime date) return Fail(Loc.ScheduleDateRequired, DatePicker);
            candidate.Date = date.Date;
            if (AllDayCheck.IsChecked == true) return Commit(candidate, out item);
            if (!TryTime(StartBox, out var start)) return Fail(Loc.ScheduleTimeFormatHint, StartBox);
            if (!TryTime(EndBox, out var end)) return Fail(Loc.ScheduleTimeFormatHint, EndBox);
            candidate.StartTime = start;
            candidate.EndTime = end;
        }
        return Commit(candidate, out item);
    }

    private bool Commit(ScheduleItem candidate, out ScheduleItem item)
    {
        item = candidate;
        if (Agenda.IsValid(candidate)) return true;
        // 到这里只剩下「结束不晚于开始」这一种可能，日历事件不允许跨零点的时段。
        return Fail(Loc.EventTimeOrderInvalid, EndBox);
    }

    private static bool TryTime(TextBox box, out TimeOnly value) =>
        TimeOnly.TryParseExact(box.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private bool TryNumber(TextBox box, int min, int max, string label, out int value)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
            return true;
        return Fail(Loc.Fmt(Loc.CourseNumberRangeFormat, label, min, max), box);
    }

    private bool Fail(string message, Control focus)
    {
        ErrorText.Text = message;
        focus.Focus();
        return false;
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(out var item)) return;
        var result = new List<ScheduleItem>(loadedItems);
        var index = result.FindIndex(x => x.Id == item.Id);
        if (index >= 0) result[index] = item;
        else result.Add(item);
        Items = Agenda.Normalize(result);
        DialogResult = true;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (original is null || !CanDiscard()) return;
        if (!Confirm(Loc.Fmt(Loc.ScheduleDeleteConfirmFormat, original.Title), Heading.Text,
                MessageBoxButton.YesNo, MessageBoxImage.Warning)) return;
        Items = loadedItems.Where(x => x.Id != original.Id).ToList();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
