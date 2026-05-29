using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Concurrent;

namespace DndBot.Handlers;

public class MasterHandler : BaseHandler
{
    public MasterHandler(ITelegramBotClient bot, DatabaseService db, ConcurrentDictionary<long, UserState> userStates)
        : base(bot, db, userStates) { }

    public async Task ShowMasterPanel(long userId)
    {
        var buttons = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("📅 Игры на этой неделе", "master_week") },
            new[] { InlineKeyboardButton.WithCallbackData("📆 Игры на этом месяце", "master_month") },
            new[] { InlineKeyboardButton.WithCallbackData("🗓 Календарь (выбор дня)", "master_calendar") },
            new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "main_menu") }
        };
        await Bot.SendTextMessageAsync(userId, "🛠 Мастер-панель", replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    public async Task ShowMasterCalendar(long userId, int offset)
    {
        var markup = GetMasterCalendarMarkup(DateTime.Today, offset);
        var targetMonth = DateTime.Today.AddMonths(offset);
        await Bot.SendTextMessageAsync(userId, $"🗓 Выберите день (мастер): {targetMonth:MMMM yyyy}", replyMarkup: markup);
        if (!UserStates.ContainsKey(userId)) UserStates[userId] = new UserState();
        UserStates[userId].MasterCalendarOffset = offset;
    }

    private InlineKeyboardMarkup GetMasterCalendarMarkup(DateTime currentMonth, int offset)
    {
        var targetMonth = currentMonth.AddMonths(offset);
        var firstOfMonth = new DateTime(targetMonth.Year, targetMonth.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(targetMonth.Year, targetMonth.Month);
        int startDayOfWeek = (int)firstOfMonth.DayOfWeek;
        startDayOfWeek = startDayOfWeek == 0 ? 6 : startDayOfWeek - 1;

        var rows = new List<List<InlineKeyboardButton>>();
        var weekRow = new List<InlineKeyboardButton>();
        foreach (var d in new[] { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" })
            weekRow.Add(InlineKeyboardButton.WithCallbackData(d, "ignore"));
        rows.Add(weekRow);

        var dayButtons = new List<InlineKeyboardButton>();
        for (int i = 0; i < startDayOfWeek; i++) dayButtons.Add(InlineKeyboardButton.WithCallbackData(" ", "ignore"));
        for (int day = 1; day <= daysInMonth; day++)
        {
            var date = new DateTime(targetMonth.Year, targetMonth.Month, day);
            dayButtons.Add(InlineKeyboardButton.WithCallbackData(day.ToString(), $"master_day_{date:yyyy-MM-dd}"));
            if ((startDayOfWeek + day) % 7 == 0 || day == daysInMonth)
            {
                rows.Add(dayButtons);
                dayButtons = new List<InlineKeyboardButton>();
            }
        }
        var navRow = new List<InlineKeyboardButton>();
        if (offset == 0) navRow.Add(InlineKeyboardButton.WithCallbackData("➡️ Следующий месяц", "master_next"));
        else navRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Предыдущий месяц", "master_prev"));
        rows.Add(navRow);
        return new InlineKeyboardMarkup(rows);
    }

    public async Task ShowMasterDayDetails(long userId, DateTime date)
    {
        var sessions = await Db.GetGameSessionsWithDetails(date);
        var unconfirmed = sessions.Where(s => !s.IsConfirmed).ToList();
        if (!unconfirmed.Any())
        {
            await Bot.SendTextMessageAsync(userId, $"На {date:dd.MM.yyyy} нет неподтверждённых игр.");
            return;
        }
        var buttons = new List<List<InlineKeyboardButton>>();
        foreach (var s in unconfirmed)
        {
            string desc = s.TeamId.HasValue
                ? $"{s.Date:dd.MM} {s.Time[..5]} 👥 Команда {s.TeamName}"
                : $"{s.Date:dd.MM} {s.Time[..5]} 🧙 {s.PlayerUsername ?? s.PlayerFirstName} ({s.CharacterName})";
            buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"✅ {desc}", $"master_confirm_{s.Id}") });
        }
        buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Назад", "master_panel") });
        await Bot.SendTextMessageAsync(userId, $"📅 Неподтверждённые игры на {date:dd.MM.yyyy}:", replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    public async Task ShowMasterGamesList(long userId, DateTime start, DateTime end, string title)
    {
        var sessions = await Db.GetGameSessionsWithDetailsRange(start, end);
        var unconfirmed = sessions.Where(s => !s.IsConfirmed).ToList();
        if (!unconfirmed.Any()) { await Bot.SendTextMessageAsync(userId, "Нет неподтверждённых игр."); return; }
        var buttons = new List<List<InlineKeyboardButton>>();
        foreach (var s in unconfirmed.OrderBy(s => s.Date).ThenBy(s => s.Time))
        {
            string desc = s.TeamId.HasValue
                ? $"{s.Date:dd.MM} {s.Time[..5]} 👥 Команда {s.TeamName}"
                : $"{s.Date:dd.MM} {s.Time[..5]} 🧙 {s.PlayerUsername ?? s.PlayerFirstName} ({s.CharacterName})";
            buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"✅ {desc}", $"master_confirm_{s.Id}") });
        }
        buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Назад", "master_panel") });
        await Bot.SendTextMessageAsync(userId, title, replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    public async Task ConfirmGameByMaster(long masterId, long sessionId)
    {
        var sessions = await Db.GetGameSessionsForDateRange(DateTime.Today.AddMonths(-1), DateTime.Today.AddMonths(1));
        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null) { await Bot.SendTextMessageAsync(masterId, "Сессия не найдена."); return; }

        await Db.ConfirmGameSession(sessionId, masterId);
        await Bot.SendTextMessageAsync(masterId, $"✅ Игра {session.Date:dd.MM.yyyy} в {session.Time[..5]} подтверждена.");

        if (session.TeamId.HasValue)
        {
            var members = await Db.GetTeamMembersWithCharacters(session.TeamId.Value);
            foreach (var member in members)
            {
                await Bot.SendTextMessageAsync(member.user.Id, $"🎉 Ваша игра ({session.Date:dd.MM.yyyy} {session.Time[..5]}) подтверждена мастером!");
                await Db.AddNotification(new Notification { UserId = member.user.Id, Type = "game_confirmed", Content = $"Игра {session.Date:dd.MM.yyyy} {session.Time[..5]} подтверждена", IsRead = false, CreatedAt = DateTime.UtcNow });
            }
        }
        else if (session.PlayerId.HasValue)
        {
            await Bot.SendTextMessageAsync(session.PlayerId.Value, $"🎉 Ваша игра подтверждена мастером!");
            await Db.AddNotification(new Notification { UserId = session.PlayerId.Value, Type = "game_confirmed", Content = $"Игра {session.Date:dd.MM.yyyy} {session.Time[..5]} подтверждена", IsRead = false, CreatedAt = DateTime.UtcNow });
        }
    }
}