using Telegram.Bot.Types.ReplyMarkups;

namespace DndBot;

public static class Utils
{
    public static InlineKeyboardMarkup GetCalendarMarkup(DateTime currentMonth, int offset, bool forBooking = false)
    {
        var targetMonth = currentMonth.AddMonths(offset);
        var firstOfMonth = new DateTime(targetMonth.Year, targetMonth.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(targetMonth.Year, targetMonth.Month);
        var startDayOfWeek = (int)firstOfMonth.DayOfWeek;
        startDayOfWeek = startDayOfWeek == 0 ? 6 : startDayOfWeek - 1;

        var rows = new List<List<InlineKeyboardButton>>();
        var weekRow = new List<InlineKeyboardButton>();
        string[] weekDays = { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
        foreach (var day in weekDays)
            weekRow.Add(InlineKeyboardButton.WithCallbackData(day, "ignore"));
        rows.Add(weekRow);

        var dayButtons = new List<InlineKeyboardButton>();
        for (int i = 0; i < startDayOfWeek; i++)
            dayButtons.Add(InlineKeyboardButton.WithCallbackData(" ", "ignore"));

        for (int day = 1; day <= daysInMonth; day++)
        {
            var date = new DateTime(targetMonth.Year, targetMonth.Month, day);
            bool isPast = forBooking && date.Date < DateTime.Today.Date;
            var callbackData = isPast ? "ignore" : $"day_{date:yyyy-MM-dd}";
            var text = isPast ? $"❌{day}" : day.ToString();
            dayButtons.Add(InlineKeyboardButton.WithCallbackData(text, callbackData));
            if ((startDayOfWeek + day) % 7 == 0 || day == daysInMonth)
            {
                rows.Add(dayButtons);
                dayButtons = new List<InlineKeyboardButton>();
            }
        }

        var navRow = new List<InlineKeyboardButton>();
        if (offset == 0)
            navRow.Add(InlineKeyboardButton.WithCallbackData("➡️ Следующий месяц", "month_next"));
        else if (offset == 1)
            navRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Предыдущий месяц", "month_prev"));
        rows.Add(navRow);

        return new InlineKeyboardMarkup(rows);
    }
}