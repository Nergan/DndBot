using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Concurrent;

namespace DndBot.Handlers;

public class AdminHandler : BaseHandler
{
    public AdminHandler(ITelegramBotClient bot, DatabaseService db, ConcurrentDictionary<long, UserState> userStates)
        : base(bot, db, userStates) { }

    public async Task ShowAdminPanel(long adminId)
    {
        var buttons = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("👮‍♂️ Запросы мастеров", "admin_requests") },
            new[] { InlineKeyboardButton.WithCallbackData("⛔ Управление банами", "admin_bans") },
            new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "main_menu") }
        };
        await Bot.SendTextMessageAsync(adminId, "👑 Админ-панель", replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    public async Task ShowRequestsPanel(long adminId)
    {
        var requests = await Db.GetUsersWithRequestedRole("master");
        if (!requests.Any()) { await Bot.SendTextMessageAsync(adminId, "Нет заявок."); return; }
        var buttons = new List<List<InlineKeyboardButton>>();
        foreach (var req in requests)
        {
            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData($"✅ Одобрить {req.Id}", $"approve_master_{req.Id}"),
                InlineKeyboardButton.WithCallbackData($"❌ Отклонить {req.Id}", $"decline_master_{req.Id}")
            });
        }
        buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Назад", "admin_panel") });
        await Bot.SendTextMessageAsync(adminId, "👮‍♂️ Запросы на роль мастера:", replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    public async Task ShowBanPanel(long adminId)
    {
        var users = await Db.GetAllUsersExceptAdmin(adminId);
        if (!users.Any()) { await Bot.SendTextMessageAsync(adminId, "Нет пользователей."); return; }
        var buttons = new List<List<InlineKeyboardButton>>();
        foreach (var u in users)
        {
            var status = u.IsBanned ? "🔴 Забанен" : (u.Warnings > 0 ? $"⚠️ {u.Warnings}/3" : "🟢 Активен");
            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData($"{u.Username ?? u.FirstName} [{u.Id}] ({status})", $"ban_user_{u.Id}"),
                InlineKeyboardButton.WithCallbackData("⚠️ Предупреждение", $"warn_user_{u.Id}"),
                InlineKeyboardButton.WithCallbackData(u.IsBanned ? "🔓 Разбан" : "🔨 Бан", $"toggle_ban_{u.Id}")
            });
        }
        buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Назад", "admin_panel") });
        await Bot.SendTextMessageAsync(adminId, "⛔ Управление пользователями:", replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    public async Task WarnUser(long adminId, long targetId)
    {
        UserStates[adminId] = new UserState { Action = "warn_text", Step = 0, Data = new() { ["targetId"] = targetId } };
        await Bot.SendTextMessageAsync(adminId, "Введите текст предупреждения:");
    }

    public async Task ProcessWarnText(long adminId, string text, long targetId)
    {
        await Db.AddWarning(targetId, text);
        await Bot.SendTextMessageAsync(adminId, $"✅ Предупреждение выдано пользователю {targetId}.");
        await Bot.SendTextMessageAsync(targetId, $"⚠️ Вы получили предупреждение:\n{text}");
        UserStates.TryRemove(adminId, out _);
        await ShowBanPanel(adminId);
    }

    public async Task ToggleBanUser(long adminId, long targetId)
    {
        var user = await Db.GetUser(targetId);
        if (user == null) return;
        if (user.IsBanned)
        {
            await Db.UnbanUser(targetId);
            await Bot.SendTextMessageAsync(adminId, $"✅ Пользователь {targetId} разбанен.");
            await Bot.SendTextMessageAsync(targetId, "🚫 Ваш бан снят.");
        }
        else
        {
            await Db.BanUser(targetId, $"Забанен администратором {adminId}");
            await Bot.SendTextMessageAsync(adminId, $"🔨 Пользователь {targetId} забанен.");
            await Bot.SendTextMessageAsync(targetId, "🚫 Вы забанены.");
        }
        await ShowBanPanel(adminId);
    }

    public async Task ApproveMaster(long adminId, long targetUserId)
    {
        var user = await Db.GetUser(targetUserId);
        if (user == null) return;
        await Db.UpdateUserRole(targetUserId, "master");
        await Db.UpdateUserRequestedRole(targetUserId, null);
        await Bot.SendTextMessageAsync(adminId, $"✅ Пользователь {targetUserId} теперь мастер.");
        await Bot.SendTextMessageAsync(targetUserId, "🎉 Ваша заявка на мастера одобрена!");
        var hasChar = (await Db.GetUserCharacters(targetUserId)).Any();
        await ShowMainMenu(targetUserId, hasChar, "master", null, AdminIds.Contains(targetUserId));
    }

    public async Task DeclineMaster(long adminId, long targetUserId)
    {
        await Db.UpdateUserRequestedRole(targetUserId, null);
        await Bot.SendTextMessageAsync(adminId, $"❌ Заявка пользователя {targetUserId} отклонена.");
        await Bot.SendTextMessageAsync(targetUserId, "❌ Ваша заявка на мастера отклонена.");
    }
}