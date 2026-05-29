using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Concurrent;
using DndBot.Handlers;

namespace DndBot;

public class BotHandlers
{
    private readonly ITelegramBotClient _bot;
    private readonly DatabaseService _db;
    private readonly ConcurrentDictionary<long, BaseHandler.UserState> _userStates = new();
    private readonly long[] _adminIds = { 1428337624 };

    private static readonly ConcurrentDictionary<long, List<Character>> _charactersStore = new();
    private static readonly ConcurrentDictionary<long, long> _characterIdCounter = new();

    private readonly PlayerHandler _player;
    private readonly MasterHandler _master;
    private readonly AdminHandler _admin;

    public BotHandlers(ITelegramBotClient bot, DatabaseService db)
    {
        _bot = bot;
        _db = db;
        Task.Run(async () => await LoadCharactersFromDb()).GetAwaiter().GetResult();
        _player = new PlayerHandler(bot, db, _userStates);
        _master = new MasterHandler(bot, db, _userStates);
        _admin = new AdminHandler(bot, db, _userStates);
    }

    private async Task LoadCharactersFromDb()
    {
        try
        {
            var allCharacters = await _db.GetAllCharacters();
            foreach (var ch in allCharacters)
            {
                var list = GetOrCreateCharacters(ch.UserId);
                if (!list.Any(c => c.Id == ch.Id))
                    list.Add(ch);
                _characterIdCounter.AddOrUpdate(ch.UserId, ch.Id, (key, old) => Math.Max(old, ch.Id));
            }
            Console.WriteLine($"[INIT] Загружено {allCharacters.Count} персонажей из БД");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[INIT] Ошибка загрузки персонажей: {ex.Message}");
        }
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Update {update.Type}");
        try
        {
            if (update.Message?.From != null)
            {
                var user = await _db.GetUser(update.Message.From.Id);
                if (user?.IsBanned == true) { await _bot.SendTextMessageAsync(update.Message.From.Id, "🚫 Вы в чёрном списке."); return; }
            }
            else if (update.CallbackQuery?.From != null)
            {
                var user = await _db.GetUser(update.CallbackQuery.From.Id);
                if (user?.IsBanned == true) { await _bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id, "Вы в чёрном списке.", true); return; }
            }
            await OnUpdate(update);
        }
        catch (Exception ex) { Console.WriteLine($"Ошибка HandleUpdate: {ex}"); }
    }

    public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Ошибка: {exception.Message}");
        return Task.CompletedTask;
    }

    private async Task OnUpdate(Update update)
    {
        if (update.Message is { } msg) await OnMessage(msg);
        else if (update.CallbackQuery is { } cb) await OnCallback(cb);
    }

    private async Task OnMessage(Message msg)
    {
        var userId = msg.From.Id;
        var text = msg.Text?.Trim();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] OnMessage от {userId}, текст: '{text}'");

        var user = await _db.GetUser(userId) ?? new User { Id = userId, Role = "player" };
        await _db.AddOrUpdateUser(user);
        bool isAdmin = _adminIds.Contains(userId);
        bool canMaster = (user.Role == "master" || isAdmin);

        var chars = GetOrCreateCharacters(userId);
        bool hasChar = chars.Count > 0;

        if (!_userStates.TryGetValue(userId, out var state))
            _userStates[userId] = state = new BaseHandler.UserState();

        // Переключение режимов
        if (text == "👤 Игрок" || text == "👤 [Игрок]")
        {
            await _player.SwitchMode(userId, "player", hasChar, user.Role, user.RequestedRole, isAdmin);
            return;
        }
        if ((text == "🛠 Мастер" || text == "🛠 [Мастер]") && canMaster)
        {
            await _player.SwitchMode(userId, "master", hasChar, user.Role, user.RequestedRole, isAdmin);
            return;
        }
        if ((text == "👑 Админ" || text == "👑 [Админ]") && isAdmin)
        {
            await _player.SwitchMode(userId, "admin", hasChar, user.Role, user.RequestedRole, isAdmin);
            return;
        }

        // Обработка состояний ввода
        if (_userStates.TryGetValue(userId, out var inputState) && inputState.Action != "")
        {
            if (inputState.Action == "char_create")
            {
                await HandleCharCreateInMemory(msg, inputState);
                return;
            }
            else if (inputState.Action == "char_edit")
            {
                await HandleCharEditInMemory(msg, inputState);
                return;
            }
            else if (inputState.Action == "group_create")
            {
                await HandleGroupCreateInMemory(msg, inputState);
                return;
            }
            else if (inputState.Action == "warn_text")
            {
                await _admin.ProcessWarnText(userId, text, (long)inputState.Data["targetId"]);
                return;
            }
            else
            {
                await _player.HandleStateInput(msg, inputState);
                return;
            }
        }

        // Основные команды
        if (text == "/start" || text == "🏠 Главное меню")
        {
            state.CurrentMode = "player";
            await _player.ShowMainMenu(userId, hasChar, user.Role, user.RequestedRole, isAdmin);
        }
        else if (text == "📜 Персонажи")
        {
            await ShowCharactersMenu(userId);
        }
        else if (text == "👥 Группы") await _player.ShowGroupsMenu(userId);
        else if (text == "📅 Запись на игру")
        {
            if (!hasChar) { await _bot.SendTextMessageAsync(userId, "❌ Нет персонажа."); return; }
            await _player.ShowCalendarForBooking(userId);
        }
        else if (text == "🔔 Оповещения")
        {
            if (!hasChar) { await _bot.SendTextMessageAsync(userId, "❌ Нет персонажа."); return; }
            await _player.ShowNotifications(userId);
        }
        else if (text == "📅 Игры на этой неделе")
        {
            if (!canMaster) return;
            var today = DateTime.Today;
            var start = today.AddDays(-(int)today.DayOfWeek + 1);
            await _master.ShowMasterGamesList(userId, start, start.AddDays(6), "📅 Игры на этой неделе");
        }
        else if (text == "📆 Игры на этом месяце")
        {
            if (!canMaster) return;
            var start = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            await _master.ShowMasterGamesList(userId, start, start.AddMonths(1).AddDays(-1), "📆 Игры в этом месяце");
        }
        else if (text == "🗓 Календарь (выбор дня)")
        {
            if (!canMaster) return;
            await _master.ShowMasterCalendar(userId, 0);
        }
        else if (text == "👮‍♂️ Запросы на мастера")
        {
            if (!isAdmin) return;
            await _admin.ShowRequestsPanel(userId);
        }
        else if (text == "⛔ Бан игроков")
        {
            if (!isAdmin) return;
            await _admin.ShowBanPanel(userId);
        }
        else if (text == "📋 Стать мастером" && user.Role != "master" && !isAdmin && user.RequestedRole == null)
            await _player.RequestMasterRole(userId);
        else if (text != null && text.StartsWith("/")) await _bot.SendTextMessageAsync(userId, "Используйте кнопки меню.");
    }

    // In-memory управление персонажами
    private List<Character> GetOrCreateCharacters(long userId)
    {
        return _charactersStore.GetOrAdd(userId, _ => new List<Character>());
    }

    private async Task CreateCharacterInMemory(Character character)
    {
        // Гарантируем, что пользователь существует в БД (исправление FK constraint)
        await _db.AddOrUpdateUser(new User { Id = character.UserId, Role = "player" });

        long newId = await _db.CreateCharacter(character);
        character.Id = newId;
        var list = GetOrCreateCharacters(character.UserId);
        list.Add(character);
        _characterIdCounter.AddOrUpdate(character.UserId, newId, (key, old) => Math.Max(old, newId));
        Console.WriteLine($"[INMEM] Добавлен персонаж '{character.Name}' (ID:{character.Id}), всего у {character.UserId}: {list.Count}");
    }

    private async Task DeleteCharacterInMemory(long userId, long characterId)
    {
        await _db.DeleteCharacter(characterId);
        if (_charactersStore.TryGetValue(userId, out var list))
        {
            list.RemoveAll(c => c.Id == characterId);
            Console.WriteLine($"[INMEM] Удалён персонаж {characterId} у {userId}");
        }
    }

    private async Task HandleCharCreateInMemory(Message msg, BaseHandler.UserState state)
    {
        var userId = msg.From.Id;
        var text = msg.Text;
        if (state.Step == 0) { state.Data["name"] = text; state.Step = 1; await _bot.SendTextMessageAsync(userId, "Введите расу:"); }
        else if (state.Step == 1) { state.Data["race"] = text; state.Step = 2; await _bot.SendTextMessageAsync(userId, "Введите класс:"); }
        else if (state.Step == 2) { state.Data["class"] = text; state.Step = 3; await _bot.SendTextMessageAsync(userId, "Введите уровень:"); }
        else if (state.Step == 3)
        {
            if (!int.TryParse(text, out int lvl)) { await _bot.SendTextMessageAsync(userId, "❌ Введите число."); return; }
            var ch = new Character
            {
                UserId = userId,
                Name = state.Data["name"].ToString()!,
                Race = state.Data["race"].ToString()!,
                Class = state.Data["class"].ToString()!,
                Level = lvl
            };
            await CreateCharacterInMemory(ch);
            var chars = GetOrCreateCharacters(userId);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Персонаж {ch.Name} создан, всего у {userId}: {chars.Count}");
            await _bot.SendTextMessageAsync(userId, $"✅ Персонаж {ch.Name} создан! У вас теперь {chars.Count} персонаж(ей).");
            _userStates.TryRemove(userId, out _);
            await ShowCharactersMenu(userId);
        }
    }

    private async Task HandleCharEditInMemory(Message msg, BaseHandler.UserState state)
    {
        var userId = msg.From.Id;
        var text = msg.Text;
        var charId = (long)state.Data["charId"];
        var ch = GetOrCreateCharacters(userId).FirstOrDefault(c => c.Id == charId);
        if (ch == null) { _userStates.TryRemove(userId, out _); return; }
        if (state.Step == 0)
        {
            state.Data["newName"] = (text == "-") ? ch.Name : text;
            state.Step = 1;
            await _bot.SendTextMessageAsync(userId, "Введите новую расу (или '-'):");
        }
        else if (state.Step == 1)
        {
            state.Data["newRace"] = (text == "-") ? ch.Race : text;
            state.Step = 2;
            await _bot.SendTextMessageAsync(userId, "Введите новый класс (или '-'):");
        }
        else if (state.Step == 2)
        {
            state.Data["newClass"] = (text == "-") ? ch.Class : text;
            state.Step = 3;
            await _bot.SendTextMessageAsync(userId, "Введите новый уровень (число или '-'):");
        }
        else if (state.Step == 3)
        {
            int newLevel = ch.Level;
            if (text != "-" && !int.TryParse(text, out newLevel)) { await _bot.SendTextMessageAsync(userId, "❌ Неверный уровень."); return; }
            ch.Name = state.Data["newName"].ToString()!;
            ch.Race = state.Data["newRace"].ToString()!;
            ch.Class = state.Data["newClass"].ToString()!;
            ch.Level = newLevel;
            await _db.UpdateCharacter(ch);
            await _bot.SendTextMessageAsync(userId, "✅ Персонаж обновлён!");
            _userStates.TryRemove(userId, out _);
            await ShowCharactersMenu(userId);
        }
    }

    private async Task HandleGroupCreateInMemory(Message msg, BaseHandler.UserState state)
    {
        var userId = msg.From.Id;
        var text = msg.Text;
        if (state.Step == 0)
        {
            state.Data["name"] = text; state.Step = 1;
            var markup = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🔓 Открытая", "group_private_no") },
                new[] { InlineKeyboardButton.WithCallbackData("🔒 Приватная", "group_private_yes") }
            });
            await _bot.SendTextMessageAsync(userId, "Выберите тип группы:", replyMarkup: markup);
        }
        else if (state.Step == 2)
        {
            if (!int.TryParse(text, out int max) || max < 2) { await _bot.SendTextMessageAsync(userId, "❌ Введите число ≥2."); return; }
            state.Data["maxMembers"] = max; state.Step = 3;
            var chars = GetOrCreateCharacters(userId);
            if (chars.Count == 0)
            {
                await _bot.SendTextMessageAsync(userId, "❌ Нет персонажей.");
                _userStates.TryRemove(userId, out _);
                return;
            }
            var btns = chars.Select(c => new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"{c.Name} ({c.Class} ур.{c.Level})", $"group_create_char_{c.Id}") }).ToList();
            btns.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Отмена", "groups_menu") });
            await _bot.SendTextMessageAsync(userId, "Выберите персонажа для группы:", replyMarkup: new InlineKeyboardMarkup(btns));
        }
    }

    private async Task ShowCharactersMenu(long userId)
    {
        var characters = GetOrCreateCharacters(userId);
        Console.WriteLine($"[INMEM] Меню персонажей: у {userId} {characters.Count} персонажей");
        if (characters.Count == 0)
        {
            await _bot.SendTextMessageAsync(userId, "У вас пока нет персонажей. Создайте первого!",
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
        await _bot.SendTextMessageAsync(userId, "Ваши персонажи:", replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    private async Task OnCallback(CallbackQuery cb)
    {
        var userId = cb.From.Id;
        var data = cb.Data;
        try { await _bot.AnswerCallbackQueryAsync(cb.Id); } catch { }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Callback: {data}");

        try
        {
            if (data == "main_menu")
            {
                var u = await _db.GetUser(userId);
                var chars = GetOrCreateCharacters(userId);
                await _player.ShowMainMenu(userId, chars.Count > 0, u?.Role ?? "player", u?.RequestedRole, _adminIds.Contains(userId));
                return;
            }
            if (data == "groups_menu") { await _player.ShowGroupsMenu(userId); return; }
            if (data == "master_panel") { await _master.ShowMasterPanel(userId); return; }
            if (data == "admin_panel") { await _admin.ShowAdminPanel(userId); return; }
            if (data == "admin_requests") { await _admin.ShowRequestsPanel(userId); return; }
            if (data == "admin_bans") { await _admin.ShowBanPanel(userId); return; }
            if (data == "booking_back") { await _player.ShowCalendarForBooking(userId); return; }
            if (data == "booking_next") { await _player.NavigateBookingCalendar(userId, 1); return; }
            if (data == "booking_prev") { await _player.NavigateBookingCalendar(userId, 0); return; }
            if (data == "master_next") { await _master.ShowMasterCalendar(userId, 1); return; }
            if (data == "master_prev") { await _master.ShowMasterCalendar(userId, 0); return; }

            if (data.StartsWith("approve_master_")) { await _admin.ApproveMaster(userId, long.Parse(data.Split('_')[2])); await _admin.ShowRequestsPanel(userId); return; }
            if (data.StartsWith("decline_master_")) { await _admin.DeclineMaster(userId, long.Parse(data.Split('_')[2])); await _admin.ShowRequestsPanel(userId); return; }
            if (data.StartsWith("warn_user_")) { await _admin.WarnUser(userId, long.Parse(data.Split('_')[2])); return; }
            if (data.StartsWith("toggle_ban_")) { await _admin.ToggleBanUser(userId, long.Parse(data.Split('_')[2])); return; }

            if (data == "master_week")
            {
                var today = DateTime.Today;
                var start = today.AddDays(-(int)today.DayOfWeek + 1);
                await _master.ShowMasterGamesList(userId, start, start.AddDays(6), "📅 Игры на этой неделе");
                return;
            }
            if (data == "master_month")
            {
                var start = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                await _master.ShowMasterGamesList(userId, start, start.AddMonths(1).AddDays(-1), "📆 Игры в этом месяце");
                return;
            }
            if (data == "master_calendar") { await _master.ShowMasterCalendar(userId, 0); return; }
            if (data.StartsWith("master_day_")) { var d = data.Substring(11); if (DateTime.TryParseExact(d, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var dt)) await _master.ShowMasterDayDetails(userId, dt); return; }
            if (data.StartsWith("master_confirm_")) { await _master.ConfirmGameByMaster(userId, long.Parse(data.Split('_')[2])); await _master.ShowMasterPanel(userId); return; }

            // Персонажи
            if (data == "char_create")
            {
                _userStates[userId] = new BaseHandler.UserState { Action = "char_create", Step = 0, Data = new() };
                await _bot.SendTextMessageAsync(userId, "Введите имя персонажа:");
            }
            else if (data.StartsWith("char_edit_"))
            {
                var charId = long.Parse(data.Split('_')[2]);
                var ch = GetOrCreateCharacters(userId).FirstOrDefault(c => c.Id == charId);
                if (ch == null) return;
                _userStates[userId] = new BaseHandler.UserState { Action = "char_edit", Step = 0, Data = new() { ["charId"] = charId } };
                await _bot.SendTextMessageAsync(userId, "Введите новое имя (или '-' чтобы оставить прежнее):");
            }
            else if (data.StartsWith("char_delete_"))
            {
                var charId = long.Parse(data.Split('_')[2]);
                await DeleteCharacterInMemory(userId, charId);
                await _bot.SendTextMessageAsync(userId, "✅ Персонаж удалён.");
                await ShowCharactersMenu(userId);
            }
            else if (data.StartsWith("char_view_"))
            {
                var charId = long.Parse(data.Split('_')[2]);
                var ch = GetOrCreateCharacters(userId).FirstOrDefault(c => c.Id == charId);
                if (ch != null) await _bot.SendTextMessageAsync(userId, $"🧙 {ch.Name}\nРаса: {ch.Race}\nКласс: {ch.Class}\nУровень: {ch.Level}");
            }
            // Группы
            else if (data == "group_create")
            {
                var chars = GetOrCreateCharacters(userId);
                if (chars.Count == 0)
                {
                    await _bot.SendTextMessageAsync(userId, "❌ Нет персонажей. Сначала создайте персонажа в разделе «Персонажи».");
                    return;
                }
                _userStates[userId] = new BaseHandler.UserState { Action = "group_create", Step = 0, Data = new() };
                await _bot.SendTextMessageAsync(userId, "Введите название группы:");
            }
            else if (data == "group_join_list") await _player.ShowGroupJoinList(userId);
            else if (data.StartsWith("group_view_")) await _player.ShowGroupView(userId, long.Parse(data.Split('_')[2]));
            else if (data.StartsWith("group_join_team_")) await _player.OnGroupJoinRequest(userId, long.Parse(data.Split('_')[3]));
            else if (data.StartsWith("group_join_confirm_"))
            {
                var parts = data.Split('_'); await _db.AddTeamMember(long.Parse(parts[3]), userId, long.Parse(parts[4]));
                await _bot.SendTextMessageAsync(userId, "✅ Вы вступили в группу!"); await _player.ShowGroupView(userId, long.Parse(parts[3]));
            }
            else if (data.StartsWith("group_leave_")) { await _db.RemoveTeamMember(long.Parse(data.Split('_')[2]), userId); await _bot.SendTextMessageAsync(userId, "Вы покинули группу."); await _player.ShowGroupsMenu(userId); }
            else if (data.StartsWith("group_disband_")) { await _db.DeleteTeam(long.Parse(data.Split('_')[2])); await _bot.SendTextMessageAsync(userId, "Группа расформирована."); await _player.ShowGroupsMenu(userId); }
            else if (data.StartsWith("group_change_char_")) { var parts = data.Split('_'); await _db.UpdateTeamMemberCharacter(long.Parse(parts[3]), userId, long.Parse(parts[4])); await _bot.SendTextMessageAsync(userId, "Персонаж обновлён."); await _player.ShowGroupView(userId, long.Parse(parts[3])); }
            else if (data.StartsWith("group_invite_"))
            {
                var teamId = long.Parse(data.Split('_')[2]);
                var avail = await _db.GetUsersWithCharactersExceptTeam(teamId, userId);
                if (!avail.Any()) { await _bot.SendTextMessageAsync(userId, "Нет доступных игроков."); return; }
                var btns = avail.Select(p => new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"{p.user.Username ?? p.user.FirstName} ({p.characters.Count} перс.)", $"invite_select_user_{teamId}_{p.user.Id}") }).ToList();
                btns.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Назад", $"group_view_{teamId}") });
                await _bot.SendTextMessageAsync(userId, "Выберите игрока:", replyMarkup: new InlineKeyboardMarkup(btns));
            }
            else if (data.StartsWith("invite_select_user_"))
            {
                var parts = data.Split('_'); var teamId = long.Parse(parts[3]); var targetId = long.Parse(parts[4]);
                var chars = GetOrCreateCharacters(targetId);
                var btns = chars.Select(ch => new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"{ch.Name} ({ch.Class} ур.{ch.Level})", $"invite_send_{teamId}_{targetId}_{ch.Id}") }).ToList();
                btns.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Назад", $"group_invite_{teamId}") });
                await _bot.SendTextMessageAsync(userId, "Выберите персонажа:", replyMarkup: new InlineKeyboardMarkup(btns));
            }
            else if (data.StartsWith("invite_send_"))
            {
                var parts = data.Split('_'); var teamId = long.Parse(parts[2]); var targetId = long.Parse(parts[3]); var charId = long.Parse(parts[4]);
                await _db.CreateInvitation(teamId, targetId, userId, charId);
                await _bot.SendTextMessageAsync(userId, "✅ Приглашение отправлено.");
                await _db.AddNotification(new Notification { UserId = targetId, Type = "invite", Content = $"Вас пригласили в команду #{teamId}", IsRead = false, CreatedAt = DateTime.UtcNow });
                await _player.ShowGroupView(userId, teamId);
            }
            else if (data == "group_private_yes" || data == "group_private_no")
            {
                if (_userStates.TryGetValue(userId, out var st) && st.Action == "group_create" && st.Step == 1)
                {
                    st.Data["isPrivate"] = data == "group_private_yes"; st.Step = 2;
                    await _bot.SendTextMessageAsync(userId, "Введите макс. участников (мин. 2):");
                }
            }
   else if (data.StartsWith("group_create_char_"))
{
    var charId = long.Parse(data.Split('_')[3]);
    if (_userStates.TryGetValue(userId, out var st) && st.Action == "group_create" && st.Step == 3)
    {
        // Гарантируем, что пользователь существует в БД
        await _db.AddOrUpdateUser(new User { Id = userId, Role = "player" });

        // Проверяем, существует ли персонаж в БД, и если нет — создаём его (на случай рассинхронизации)
        var character = await _db.GetCharacter(charId);
        if (character == null)
        {
            var memChar = GetOrCreateCharacters(userId).FirstOrDefault(c => c.Id == charId);
            if (memChar != null)
            {
                await _db.CreateCharacter(memChar);
            }
            else
            {
                await _bot.SendTextMessageAsync(userId, "❌ Персонаж не найден.");
                return;
            }
        }

        if (await _db.IsCharacterCaptainInAnyTeam(charId))
        {
            await _bot.SendTextMessageAsync(userId, "❌ Этот персонаж уже капитан.");
            return;
        }

        var team = new Team
        {
            Name = st.Data["name"].ToString()!,
            CaptainUserId = userId,
            IsPrivate = (bool)st.Data["isPrivate"],
            MaxMembers = (int)st.Data["maxMembers"],
            CreatedAt = DateTime.UtcNow
        };

        var teamId = await _db.CreateTeam(team);
        await _db.UpdateTeamMemberCharacter(teamId, userId, charId);
        await _bot.SendTextMessageAsync(userId, $"✅ Группа '{team.Name}' создана!");
        _userStates.TryRemove(userId, out _);
        await _player.ShowGroupsMenu(userId);
    }
}
            // Запись на игру
            else if (data.StartsWith("day_"))
            {
                if (DateTime.TryParse(data.Substring(4), out var date))
                {
                    await ShowDayDetails(userId, date);
                }
            }
            else if (data.StartsWith("book_team_"))
            {
                var parts = data.Split('_');
                var teamId = long.Parse(parts[2]);
                var date = DateTime.Parse(parts[3]);
                await _player.ShowTimeSelection(userId, teamId, "team", date);
            }
            else if (data.StartsWith("book_char_"))
            {
                var parts = data.Split('_');
                var charId = long.Parse(parts[2]);
                var date = DateTime.Parse(parts[3]);
                if (GetOrCreateCharacters(userId).Any(c => c.Id == charId))
                    await _player.ShowTimeSelection(userId, charId, "char", date);
                else
                    await _bot.SendTextMessageAsync(userId, "❌ Персонаж не найден.");
            }
            else if (data.StartsWith("book_time_select_"))
            {
                var parts = data.Split('_');
                var entityType = parts[3];
                var entityId = long.Parse(parts[4]);
                var date = DateTime.Parse(parts[5]);
                var hour = int.Parse(parts[6]);
                var timeStr = $"{hour:00}:00:00";

                var existing = await _db.GetGameSessionsForDate(date);
                if (entityType == "team" && existing.Any(s => s.PlayerId == userId && s.Time == timeStr))
                { await _bot.SendTextMessageAsync(userId, "❌ Вы уже записаны на это время как игрок."); return; }
                if (existing.Any(s => (s.PlayerId == userId || s.TeamId == entityId) && s.Time == timeStr))
                { await _bot.SendTextMessageAsync(userId, "❌ Вы/команда уже записаны на это время."); return; }

                var session = new GameSession { Date = date, Time = timeStr, IsConfirmed = false };
                if (entityType == "team") session.TeamId = entityId;
                else { session.PlayerId = userId; session.CharacterId = entityId; }
                await _db.AddGameSession(session);
                await _bot.SendTextMessageAsync(userId, $"✅ Вы записаны на {date:dd.MM.yyyy} в {timeStr[..5]}.");
                await ShowDayDetails(userId, date);
            }
            else if (data.StartsWith("join_session_"))
            {
                var parts = data.Split('_'); var sessionId = long.Parse(parts[2]); var date = DateTime.Parse(parts[3]);
                var original = (await _db.GetGameSessionsForDate(date)).FirstOrDefault(s => s.Id == sessionId);
                if (original == null || original.TeamId.HasValue) { await _bot.SendTextMessageAsync(userId, "❌ Нельзя присоединиться."); return; }
                var userSessions = await _db.GetGameSessionsForDate(date);
                if (userSessions.Any(s => s.PlayerId == userId && s.Time == original.Time)) { await _bot.SendTextMessageAsync(userId, "❌ Вы уже записаны на это время."); return; }
                var chars = GetOrCreateCharacters(userId);
                if (!chars.Any()) { await _bot.SendTextMessageAsync(userId, "❌ Нет персонажей."); return; }
                if (chars.Count == 1)
                {
                    var ns = new GameSession { Date = date, Time = original.Time, PlayerId = userId, CharacterId = chars[0].Id, IsConfirmed = false };
                    await _db.AddGameSession(ns); await _bot.SendTextMessageAsync(userId, $"✅ Присоединились как {chars[0].Name}.");
                    await ShowDayDetails(userId, date);
                }
                else
                {
                    var btns = chars.Select(c => new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"{c.Name}", $"join_confirm_{sessionId}_{c.Id}_{date:yyyy-MM-dd}") }).ToList();
                    btns.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Назад", $"day_{date:yyyy-MM-dd}") });
                    await _bot.SendTextMessageAsync(userId, "Выберите персонажа:", replyMarkup: new InlineKeyboardMarkup(btns));
                }
            }
            else if (data.StartsWith("join_confirm_"))
            {
                var parts = data.Split('_'); var sessionId = long.Parse(parts[2]); var charId = long.Parse(parts[3]); var date = DateTime.Parse(parts[4]);
                var original = (await _db.GetGameSessionsForDate(date)).FirstOrDefault(s => s.Id == sessionId);
                if (original == null) return;
                var ns = new GameSession { Date = date, Time = original.Time, PlayerId = userId, CharacterId = charId, IsConfirmed = false };
                await _db.AddGameSession(ns); await _bot.SendTextMessageAsync(userId, "✅ Присоединились.");
                await ShowDayDetails(userId, date);
            }
            // Оповещения
            else if (data.StartsWith("invite_accept_"))
            {
                var invId = long.Parse(data.Split('_')[2]); var inv = await _db.GetInvitationById(invId);
                if (inv?.Status == "pending")
                {
                    await _db.AddTeamMember(inv.TeamId, userId, inv.InvitedCharacterId);
                    await _db.UpdateInvitationStatus(invId, "accepted");
                    await _bot.SendTextMessageAsync(userId, "✅ Приглашение принято.");
                }
                else await _bot.SendTextMessageAsync(userId, "Приглашение устарело.");
                await _player.ShowNotifications(userId);
            }
            else if (data.StartsWith("invite_decline_"))
            {
                await _db.UpdateInvitationStatus(long.Parse(data.Split('_')[2]), "declined");
                await _bot.SendTextMessageAsync(userId, "❌ Приглашение отклонено.");
                await _player.ShowNotifications(userId);
            }
            else if (data.StartsWith("notif_read_"))
            {
                var notif = await _db.GetNotificationById(long.Parse(data.Split('_')[2]));
                if (notif != null) await _bot.SendTextMessageAsync(userId, $"🔔 {notif.Content}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"Ошибка OnCallback: {ex}"); }
    }

    private async Task ShowDayDetails(long userId, DateTime date)
    {
        var user = await _db.GetUser(userId) ?? new User { Id = userId, Role = "player" };
        var isMaster = user.Role == "master" || _adminIds.Contains(userId);
        var sessions = await _db.GetGameSessionsWithDetails(date);
        var chars = GetOrCreateCharacters(userId);
        var userTeams = await _db.GetUserTeams(userId);
        var hasChar = chars.Count > 0;

        string text = $"📅 {date:dd.MM.yyyy}\n\n";
        if (!sessions.Any()) text += "Нет записей на этот день.\n";
        else
        {
            text += "Существующие записи:\n";
            foreach (var s in sessions)
            {
                if (s.TeamId.HasValue)
                    text += $"👥 Команда {s.TeamName} — {s.Time[..5]} {(s.IsConfirmed ? "✅" : "⏳")}\n";
                else
                    text += $"🧙 {s.PlayerUsername ?? s.PlayerFirstName} ({s.CharacterName}) — {s.Time[..5]} {(s.IsConfirmed ? "✅" : "⏳")}\n";
            }
            text += "\n";
        }

        var buttons = new List<List<InlineKeyboardButton>>();

        foreach (var team in userTeams.Where(t => t.CaptainUserId == userId))
            buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"👥 Записать команду {team.Name}", $"book_team_{team.Id}_{date:yyyy-MM-dd}") });

        foreach (var ch in chars)
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
        await _bot.SendTextMessageAsync(userId, text, replyMarkup: new InlineKeyboardMarkup(buttons));
    }
}