using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopCalendarWidget;

/// <summary>
/// 当日日程面板。收起态是日历下方那条可滚动摘要，展开态是右侧抽屉——同一控件同一份渲染，
/// 只有高度和「✕」按钮不同。面板自己不碰数据，编辑请求以事件抛给主窗口。
/// </summary>
public partial class DayAgendaPanel : UserControl
{
    private bool expanded;

    public DayAgendaPanel() => InitializeComponent();

    /// <summary>展开态出现「✕」，收起态不出现。</summary>
    public void SetExpanded(bool isExpanded)
    {
        expanded = isExpanded;
        CloseButton.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    public event Action<ScheduleItem>? EditRequested;
    public event Action<DateTime>? AddRequested;
    public event Action? CloseRequested;

    private void AddButton_Click(object sender, RoutedEventArgs e) => AddRequested?.Invoke(ShownDate);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AgendaRow row }) EditRequested?.Invoke(row.Item);
    }

    private DateTime ShownDate { get; set; } = DateTime.Today;

    /// <summary>换一天重绑。选中项按 Id 保留，翻月回来时不会丢掉高亮。</summary>
    public void Bind(DateTime date, IReadOnlyList<ScheduleItem> items, Guid? selectedId)
    {
        ShownDate = date.Date;
        DateText.Text = Loc.Fmt(Loc.SchedulePanelHeading, date.ToString(Loc.DateFormatLong, Loc.CurrentCulture));
        CountText.Text = items.Count == 0 ? "" : Loc.Fmt(Loc.EventsDayFormat, items.Count);
        EmptyText.Text = items.Count == 0 ? Loc.ScheduleEmptyText : "";
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ItemList.ItemsSource = new ObservableCollection<AgendaRow>(items.Select(item => new AgendaRow(item, item.Id == selectedId)));
    }

    /// <summary>只改高亮，不重绑整张列表——编辑完只有那一行的选中态变了。</summary>
    public void SetSelected(Guid? id)
    {
        if (ItemList.ItemsSource is not IEnumerable<AgendaRow> rows) return;
        foreach (var row in rows) row.IsSelected = id == row.Item.Id;
    }

    /// <summary>一条日程在面板上的样子。IsSelected 需要通知，Border 的 DataTrigger 才能跟着变。</summary>
    internal sealed class AgendaRow(ScheduleItem item, bool selected) : INotifyPropertyChanged
    {
        private bool isSelected = selected;

        public ScheduleItem Item { get; } = item;

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value) return;
                isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public string Icon => ScheduleKinds.Icon(Item.Kind);

        public string Title => Item.Title;

        public string Time => Agenda.IsAllDay(Item) ? Loc.EventAllDay
            : Agenda.IsTimePending(Item) ? Loc.ScheduleTimePending
            : Agenda.TimeLabel(Item);

        public string Place => string.IsNullOrWhiteSpace(Item.Location) ? "" : Item.Location!;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
