using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Concurrent;

namespace DndBot.Handlers;

public abstract class BaseHandler
{
    protected readonly ITelegramBotClient Bot;
    protected readonly DatabaseService Db;
    protected readonly ConcurrentDictionary<long, UserState> UserStates;
    protected readonly long[] AdminIds = { 1428337624 };

    public class UserState
    {
        public string Action { get; set; } = "";
        public int Step { get; set; } = 0;
        public Dictionary<string, object> Data { get; set; } = new();
        public int MasterCalendarOffset { get; set; } = 0;
        public string CurrentMode { get; set; } = "player";
        public int? MainMenuMessageId { get; set; } = null;
    }

    protected BaseHandler(ITelegramBotClient bot, DatabaseService db, ConcurrentDictionary<long, UserState> userStates)
    {
        Bot = bot;
        Db = db;
        UserStates = userStates;
    }

    // ========== ГЛАВНОЕ МЕНЮ И ПЕРЕКЛЮЧЕНИЕ РЕЖИМОВ ==========
    public virtual async Task ShowMainMenu(long userId, bool hasCharacter, string role, string? requestedRole, bool isAdmin)
    {
        if (!UserStates.TryGetValue(userId, out var state))
            UserStates[userId] = state = new UserState();

        var markup = BuildMainMenuMarkup(state.CurrentMode, role, requestedRole, isAdmin);
        var message = await Bot.SendTextMessageAsync(userId, "🏠 Главное меню", replyMarkup: markup);
        state.MainMenuMessageId = message.MessageId;
    }

    public async Task SwitchMode(long userId, string newMode, bool hasCharacter, string role, string? requestedRole, bool isAdmin)
    {
        if (!UserStates.TryGetValue(userId, out var state))
            UserStates[userId] = state = new UserState();

        state.CurrentMode = newMode;

        if (state.MainMenuMessageId.HasValue)
        {
            try { await Bot.DeleteMessageAsync(userId, state.MainMenuMessageId.Value); } catch { }
        }

        var markup = BuildMainMenuMarkup(newMode, role, requestedRole, isAdmin);
        var message = await Bot.SendTextMessageAsync(userId, "🏠 Главное меню", replyMarkup: markup);
        state.MainMenuMessageId = message.MessageId;
    }

    private ReplyKeyboardMarkup BuildMainMenuMarkup(string mode, string role, string? requestedRole, bool isAdmin)
    {
        bool canMaster = (role == "master" || isAdmin);
        bool canAdmin = isAdmin;

        var buttons = new List<KeyboardButton[]>();

        // Функциональные кнопки в зависимости от режима
        if (mode == "player")
        {
            buttons.Add(new KeyboardButton[] { "📜 Персонажи", "👥 Группы" });
            buttons.Add(new KeyboardButton[] { "📅 Запись на игру", "🔔 Оповещения" });

            // Кнопка "Стать мастером" для игрока, если он ещё не мастер и не подавал заявку
            if (!canMaster && requestedRole == null)
                buttons.Add(new KeyboardButton[] { "📋 Стать мастером" });
            else if (!canMaster && requestedRole == "master")
                buttons.Add(new KeyboardButton[] { "⏳ Заявка на мастера отправлена" });
        }
        else if (mode == "master")
        {
            buttons.Add(new KeyboardButton[] { "📅 Игры на этой неделе" });
            buttons.Add(new KeyboardButton[] { "📆 Игры на этом месяце" });
            buttons.Add(new KeyboardButton[] { "🗓 Календарь (выбор дня)" });
        }
        else if (mode == "admin")
        {
            buttons.Add(new KeyboardButton[] { "👮‍♂️ Запросы на мастера", "⛔ Бан игроков" });
        }

        // Кнопки переключения режимов
        var modeRow = new List<KeyboardButton>();
        modeRow.Add(new KeyboardButton(mode == "player" ? "👤 [Игрок]" : "👤 Игрок"));
        if (canMaster)
            modeRow.Add(new KeyboardButton(mode == "master" ? "🛠 [Мастер]" : "🛠 Мастер"));
        if (canAdmin)
            modeRow.Add(new KeyboardButton(mode == "admin" ? "👑 [Админ]" : "👑 Админ"));
        if (modeRow.Count > 1)
            buttons.Add(modeRow.ToArray());

        // Кнопка сброса
        buttons.Add(new KeyboardButton[] { "🏠 Главное меню" });

        return new ReplyKeyboardMarkup(buttons) { ResizeKeyboard = true };
    }

    // ========== ПЕРСОНАЖИ ==========
    public async Task ShowCharactersMenu(long userId)
    {
        var characters = await Db.GetUserCharacters(userId);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Меню персонажей: у {userId} {characters.Count} персонажей");
        if (!characters.Any())
        {
            await Bot.SendTextMessageAsync(userId, "У вас пока нет персонажей. Создайте первого!",
                replyMarkup: new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("➕ Создать персонажа", "char_create") } }));
            return;
        }
        var buttons = new List<List<InlineKeyboardButton>>();
        foreach (var ch in characters)
        {
            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData($"📌 {ch.Name} ({ch.Class} ур.{ch.Level})", $"char_view_{ch.Id}"),
                InlineKeyboardButton.WithCallbackData("✏️", $"char_edit_{ch.Id}"),
                InlineKeyboardButton.WithCallbackData("🗑️", $"char_delete_{ch.Id}")
            });
        }
        buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("➕ Создать нового", "char_create") });
        buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Назад", "main_menu") });
        await Bot.SendTextMessageAsync(userId, "Ваши персонажи:", replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    public async Task OnCharacterCreateStart(long userId)
    {
        UserStates[userId] = new UserState { Action = "char_create", Step = 0, Data = new() };
        await Bot.SendTextMessageAsync(userId, "Введите имя персонажа:");
    }

    public async Task OnCharacterEditStart(long userId, long charId)
    {
        var ch = await Db.GetCharacter(charId);
        if (ch == null || ch.UserId != userId) return;
        UserStates[userId] = new UserState { Action = "char_edit", Step = 0, Data = new() { ["charId"] = charId } };
        await Bot.SendTextMessageAsync(userId, "Введите новое имя (или '-' чтобы оставить прежнее):");
    }

    public async Task OnCharacterDelete(long userId, long charId)
    {
        await Db.DeleteCharacter(charId);
        await Bot.SendTextMessageAsync(userId, "✅ Персонаж удалён.");
        await ShowCharactersMenu(userId);
    }

    // ========== ГРУППЫ ==========
    public async Task ShowGroupsMenu(long userId)
    {
        var userTeams = await Db.GetUserTeams(userId);
        var inlineKeyboard = new List<List<InlineKeyboardButton>>();
        if (userTeams.Any())
        {
            foreach (var team in userTeams)
                inlineKeyboard.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"📁 {team.Name}", $"group_view_{team.Id}") });
            inlineKeyboard.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("➕ Создать группу", "group_create"), InlineKeyboardButton.WithCallbackData("🔍 Вступить в группу", "group_join_list") });
        }
        else
        {
            inlineKeyboard.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("➕ Создать группу", "group_create"), InlineKeyboardButton.WithCallbackData("🔍 Вступить в группу", "group_join_list") });
        }
        inlineKeyboard.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Назад", "main_menu") });
        await Bot.SendTextMessageAsync(userId, "👥 Управление группами:", replyMarkup: new InlineKeyboardMarkup(inlineKeyboard));
    }

    public async Task ShowGroupView(long userId, long teamId)
    {
        var team = await Db.GetTeam(teamId);
        if (team == null) return;
        var members = await Db.GetTeamMembersWithCharacters(teamId);
        var isCaptain = team.CaptainUserId == userId;
        var isMember = members.Any(m => m.user.Id == userId);
        var captainUser = await Db.GetUser(team.CaptainUserId);
        var captainName = captainUser?.Username ?? captainUser?.FirstName ?? team.CaptainUserId.ToString();
        var text = $"🏷️ {team.Name}\n👑 Капитан: {captainName}\n🔒 {(team.IsPrivate ? "Приватная" : "Открытая")}\n👥 Участники: {members.Count}/{team.MaxMembers}\n\n";
        foreach (var member in members)
        {
            var user = member.user;
            var ch = member.character;
            text += $"• {user.Username ?? user.FirstName} — {(ch != null ? $"{ch.Name} ({ch.Race}, {ch.Class} ур.{ch.Level})" : "❌ без персонажа")}\n";
        }
        var buttons = new List<List<InlineKeyboardButton>>();
        if (isMember && !isCaptain)
        {
            buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🚪 Покинуть группу", $"group_leave_{teamId}") });
            var userChars = await Db.GetUserCharacters(userId);
            if (userChars.Any())
            {
                var currentCharId = members.FirstOrDefault(m => m.user.Id == userId).character?.Id;
                foreach (var ch in userChars)
                {
                    string label = $"🎭 {ch.Name}";
                    if (currentCharId == ch.Id) label += " (текущий)";
                    buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData(label, $"group_change_char_{teamId}_{ch.Id}") });
                }
            }
        }
        if (isCaptain)
        {
            buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🗑️ Расформировать группу", $"group_disband_{teamId}") });
            buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("👥 Пригласить игрока", $"group_invite_{teamId}") });
        }
        buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Назад", "groups_menu") });
        await Bot.SendTextMessageAsync(userId, text, replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    public async Task ShowGroupJoinList(long userId)
    {
        var openTeams = await Db.GetOpenTeams();
        var userTeams = await Db.GetUserTeams(userId);
        var userTeamIds = userTeams.Select(t => t.Id).ToHashSet();
        var available = openTeams.Where(t => !userTeamIds.Contains(t.Id)).ToList();
        if (!available.Any()) { await Bot.SendTextMessageAsync(userId, "Нет доступных открытых групп."); return; }
        var buttons = available.Select(t => new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"{t.Name} ({t.MaxMembers} мест)", $"group_join_team_{t.Id}") }).ToList();
        buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Назад", "groups_menu") });
        await Bot.SendTextMessageAsync(userId, "Выберите группу:", replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    public async Task OnGroupJoinRequest(long userId, long teamId)
    {
        var characters = await Db.GetUserCharacters(userId);
        if (!characters.Any()) { await Bot.SendTextMessageAsync(userId, "❌ Нет персонажей."); return; }
        if (characters.Count == 1)
        {
            await Db.AddTeamMember(teamId, userId, characters[0].Id);
            await Bot.SendTextMessageAsync(userId, $"✅ Вы вступили в группу с персонажем {characters[0].Name}.");
            await ShowGroupView(userId, teamId);
        }
        else
        {
            var btns = characters.Select(ch => new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"{ch.Name} ({ch.Class} ур.{ch.Level})", $"group_join_confirm_{teamId}_{ch.Id}") }).ToList();
            btns.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Отмена", "groups_menu") });
            await Bot.SendTextMessageAsync(userId, "Выберите персонажа:", replyMarkup: new InlineKeyboardMarkup(btns));
        }
    }

    public async Task OnGroupCreateStart(long userId)
    {
        var chars = await Db.GetUserCharacters(userId);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] OnGroupCreateStart: пользователь {userId}, персонажей в базе: {chars.Count}");
        if (chars.Count == 0)
        {
            await Bot.SendTextMessageAsync(userId, "❌ Нет персонажей. Сначала создайте персонажа в разделе «Персонажи».");
            return;
        }
        UserStates[userId] = new UserState { Action = "group_create", Step = 0, Data = new() };
        await Bot.SendTextMessageAsync(userId, "Введите название группы:");
    }

    // ========== ЗАПИСЬ НА ИГРУ ==========
    public async Task ShowCalendarForBooking(long userId)
    {
        UserStates.TryRemove(userId, out _);
        await ShowBookingCalendar(userId, DateTime.Today, 0);
    }

    private async Task ShowBookingCalendar(long userId, DateTime currentMonth, int offset)
    {
        var markup = GetBookingCalendarMarkup(currentMonth, offset);
        var targetMonth = currentMonth.AddMonths(offset);
        await Bot.SendTextMessageAsync(userId, $"📅 Выберите дату для записи:\n{targetMonth:MMMM yyyy}", replyMarkup: markup);
        if (!UserStates.ContainsKey(userId)) UserStates[userId] = new UserState();
        UserStates[userId].MasterCalendarOffset = offset;
    }

    public async Task NavigateBookingCalendar(long userId, int offset)
    {
        await ShowBookingCalendar(userId, DateTime.Today, offset);
    }

    public InlineKeyboardMarkup GetBookingCalendarMarkup(DateTime currentMonth, int offset)
    {
        var targetMonth = currentMonth.AddMonths(offset);
        var firstOfMonth = new DateTime(targetMonth.Year, targetMonth.Month, 1);
        int daysInMonth = DateTime.DaysInMonth(targetMonth.Year, targetMonth.Month);
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
            bool isPast = date.Date < DateTime.Today.Date;
            dayButtons.Add(InlineKeyboardButton.WithCallbackData(isPast ? $"❌{day}" : day.ToString(), isPast ? "ignore" : $"day_{date:yyyy-MM-dd}"));
            if ((startDayOfWeek + day) % 7 == 0 || day == daysInMonth)
            {
                rows.Add(dayButtons);
                dayButtons = new List<InlineKeyboardButton>();
            }
        }
        var navRow = new List<InlineKeyboardButton>();
        if (offset == 0) navRow.Add(InlineKeyboardButton.WithCallbackData("➡️ Следующий месяц", "booking_next"));
        else if (offset == 1) navRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Предыдущий месяц", "booking_prev"));
        rows.Add(navRow);
        return new InlineKeyboardMarkup(rows);
    }

    public async Task ShowDayDetails(long userId, DateTime date)
    {
        var user = await Db.GetUser(userId);
        var isMaster = user?.Role == "master" || AdminIds.Contains(userId);
        var sessions = await Db.GetGameSessionsWithDetails(date);
        var userTeams = await Db.GetUserTeams(userId);
        var userChars = await Db.GetUserCharacters(userId);
        bool hasChar = userChars.Any();
        string text = $"📅 {date:dd.MM.yyyy}\n\n";
        if (!sessions.Any()) text += "Нет записей на этот день.\n";
        else
        {
            text += "Существующие записи:\n";
            foreach (var s in sessions)
            {
                if (s.TeamId.HasValue) text += $"👥 Команда {s.TeamName} — {s.Time[..5]} {(s.IsConfirmed ? "✅" : "⏳")}\n";
                else text += $"🧙 {s.PlayerUsername ?? s.PlayerFirstName} ({s.CharacterName}) — {s.Time[..5]} {(s.IsConfirmed ? "✅" : "⏳")}\n";
            }
            text += "\n";
        }
        var buttons = new List<List<InlineKeyboardButton>>();
        foreach (var team in userTeams.Where(t => t.CaptainUserId == userId))
            buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"👥 Записать команду {team.Name}", $"book_team_{team.Id}_{date:yyyy-MM-dd}") });
        foreach (var ch in userChars)
            buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"🧙 Записаться как {ch.Name}", $"book_char_{ch.Id}_{date:yyyy-MM-dd}") });
        if (hasChar)
        {
            foreach (var s in sessions.Where(s => s.PlayerId.HasValue && s.PlayerId != userId && !s.TeamId.HasValue))
                buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"➕ Присоединиться к {s.PlayerUsername ?? s.PlayerFirstName} ({s.CharacterName}) в {s.Time[..5]}", $"join_session_{s.Id}_{date:yyyy-MM-dd}") });
        }
        if (isMaster)
        {
            foreach (var s in sessions.Where(s => !s.IsConfirmed))
            {
                string desc = s.TeamId.HasValue
                    ? $"✅ Подтвердить игру команды {s.TeamName} в {s.Time[..5]}"
                    : $"✅ Подтвердить игру {s.PlayerUsername ?? s.PlayerFirstName} ({s.CharacterName}) в {s.Time[..5]}";
                buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData(desc, $"master_confirm_{s.Id}") });
            }
        }
        if (!buttons.Any()) text += "\nУ вас нет персонажей или прав для записи.";
        buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Назад в календарь", "booking_back") });
        await Bot.SendTextMessageAsync(userId, text, replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    public async Task OnDateSelectedForBooking(long userId, DateTime date)
    {
        if (date.Date < DateTime.Today.Date) { await Bot.SendTextMessageAsync(userId, "❌ Нельзя записаться на прошедшую дату."); return; }
        await ShowDayDetails(userId, date);
    }

    public async Task ShowTimeSelection(long userId, long entityId, string entityType, DateTime date)
    {
        var buttons = new List<List<InlineKeyboardButton>>();
        for (int hour = 10; hour <= 22; hour++)
            buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"{hour:00}:00", $"book_time_select_{entityType}_{entityId}_{date:yyyy-MM-dd}_{hour}") });
        buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Назад", $"day_{date:yyyy-MM-dd}") });
        await Bot.SendTextMessageAsync(userId, "Выберите время начала игры:", replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    // ========== ОПОВЕЩЕНИЯ ==========
    public async Task ShowNotifications(long userId)
    {
        var invites = await Db.GetPendingInvitationsForUser(userId);
        var notifications = await Db.GetAllNotifications(userId);
        if (!invites.Any() && !notifications.Any()) { await Bot.SendTextMessageAsync(userId, "📭 У вас нет оповещений."); return; }
        var buttons = new List<List<InlineKeyboardButton>>();
        foreach (var inv in invites)
        {
            var team = await Db.GetTeam(inv.TeamId);
            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData($"📨 Приглашение в {team?.Name ?? "группу"}", $"invite_accept_{inv.Id}"),
                InlineKeyboardButton.WithCallbackData("✅ Принять", $"invite_accept_{inv.Id}"),
                InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"invite_decline_{inv.Id}")
            });
        }
        foreach (var notif in notifications)
            buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"🔔 {notif.Content}", $"notif_read_{notif.Id}") });
        buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Назад", "main_menu") });
        await Bot.SendTextMessageAsync(userId, "Ваши оповещения:", replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    // ========== ОБРАБОТКА СОСТОЯНИЙ ВВОДА ==========
    public virtual async Task HandleStateInput(Message msg, UserState state)
    {
        var userId = msg.From.Id;
        var text = msg.Text;
        switch (state.Action)
        {
            case "char_create":
                await HandleCharCreate(msg, state);
                break;
            case "char_edit":
                await HandleCharEdit(msg, state);
                break;
        }
    }

    private async Task HandleCharCreate(Message msg, UserState state)
    {
        var userId = msg.From.Id;
        var text = msg.Text;
        if (state.Step == 0) { state.Data["name"] = text; state.Step = 1; await Bot.SendTextMessageAsync(userId, "Введите расу:"); }
        else if (state.Step == 1) { state.Data["race"] = text; state.Step = 2; await Bot.SendTextMessageAsync(userId, "Введите класс:"); }
        else if (state.Step == 2) { state.Data["class"] = text; state.Step = 3; await Bot.SendTextMessageAsync(userId, "Введите уровень:"); }
        else if (state.Step == 3)
        {
            if (!int.TryParse(text, out int lvl)) { await Bot.SendTextMessageAsync(userId, "❌ Введите число."); return; }
            var ch = new Character { UserId = userId, Name = state.Data["name"].ToString()!, Race = state.Data["race"].ToString()!, Class = state.Data["class"].ToString()!, Level = lvl };
            try
            {
                await Db.CreateCharacter(ch);
                var chars = await Db.GetUserCharacters(userId);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Персонаж {ch.Name} создан, всего у {userId}: {chars.Count}");
                await Bot.SendTextMessageAsync(userId, $"✅ Персонаж {ch.Name} создан! У вас теперь {chars.Count} персонаж(ей).");
                UserStates.TryRemove(userId, out _);
                await ShowCharactersMenu(userId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при создании персонажа: {ex}");
                await Bot.SendTextMessageAsync(userId, "❌ Ошибка при создании персонажа. Попробуйте позже.");
            }
        }
    }

    private async Task HandleCharEdit(Message msg, UserState state)
    {
        var userId = msg.From.Id;
        var text = msg.Text;
        var charId = (long)state.Data["charId"];
        var ch = await Db.GetCharacter(charId);
        if (ch == null) { UserStates.TryRemove(userId, out _); return; }
        if (state.Step == 0)
        {
            state.Data["newName"] = (text == "-") ? ch.Name : text;
            state.Step = 1;
            await Bot.SendTextMessageAsync(userId, "Введите новую расу (или '-'):");
        }
        else if (state.Step == 1)
        {
            state.Data["newRace"] = (text == "-") ? ch.Race : text;
            state.Step = 2;
            await Bot.SendTextMessageAsync(userId, "Введите новый класс (или '-'):");
        }
        else if (state.Step == 2)
        {
            state.Data["newClass"] = (text == "-") ? ch.Class : text;
            state.Step = 3;
            await Bot.SendTextMessageAsync(userId, "Введите новый уровень (число или '-'):");
        }
        else if (state.Step == 3)
        {
            int newLevel = ch.Level;
            if (text != "-" && !int.TryParse(text, out newLevel)) { await Bot.SendTextMessageAsync(userId, "❌ Неверный уровень."); return; }
            ch.Name = state.Data["newName"].ToString()!; ch.Race = state.Data["newRace"].ToString()!; ch.Class = state.Data["newClass"].ToString()!; ch.Level = newLevel;
            await Db.UpdateCharacter(ch);
            await Bot.SendTextMessageAsync(userId, "✅ Персонаж обновлён!");
            UserStates.TryRemove(userId, out _);
            await ShowCharactersMenu(userId);
        }
    }
}