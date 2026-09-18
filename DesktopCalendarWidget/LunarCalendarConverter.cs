using System.Globalization;

namespace DesktopCalendarWidget;

public static class LunarCalendarConverter
{
    private static readonly ChineseLunisolarCalendar Calendar = new();
    private static readonly string[] MonthNames = ["正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "冬月", "腊月"];
    private static readonly string[] DayNames = ["初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十", "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十", "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"];
    private static readonly string[] HeavenlyStems = ["甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸"];
    private static readonly string[] EarthlyBranches = ["子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥"];

    /// <summary>公历日期对应的农历日文本：初一至三十显示日，初一显示月名，闰月加“闰”前缀。</summary>
    public static string Format(DateTime date)
    {
        try
        {
            var lunarYear = Calendar.GetYear(date);
            var rawMonth = Calendar.GetMonth(date);
            var day = Calendar.GetDayOfMonth(date);
            var dayText = DayNames[Math.Clamp(day - 1, 0, DayNames.Length - 1)];
            if (day != 1) return dayText;

            var leapRawMonth = Calendar.GetLeapMonth(lunarYear);
            var displayMonth = rawMonth;
            var isLeap = false;
            if (leapRawMonth > 0)
            {
                // ChineseLunisolarCalendar 在闰月年会给闰月及其之后的月份整体加 1，因此要统一回退一位。
                if (rawMonth == leapRawMonth)
                {
                    isLeap = true;
                    displayMonth = rawMonth - 1;
                }
                else if (rawMonth > leapRawMonth)
                {
                    displayMonth = rawMonth - 1;
                }
            }

            if (displayMonth < 1 || displayMonth > MonthNames.Length) return dayText;
            return (isLeap ? "闰" : "") + MonthNames[displayMonth - 1];
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }
    }

    /// <summary>农历年份的干支文本，例如“农历乙巳年”。超出支持范围时返回空字符串。</summary>
    public static string GetYearLabel(DateTime date)
    {
        try
        {
            var lunarYear = Calendar.GetYear(date);
            var offset = lunarYear - 4;
            if (offset < 0) return "";
            var stem = HeavenlyStems[offset % 10];
            var branch = EarthlyBranches[offset % 12];
            return $"农历{stem}{branch}年";
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }
    }
}
