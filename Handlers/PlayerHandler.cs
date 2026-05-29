using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Concurrent;

namespace DndBot.Handlers;

public class PlayerHandler : BaseHandler
{
    public PlayerHandler(ITelegramBotClient bot, DatabaseService db, ConcurrentDictionary<long, UserState> userStates)
        : base(bot, db, userStates) { }

    public async Task RequestMasterRole(long userId)
    {
        await Db.UpdateUserRequestedRole(userId, "master");
        await Bot.SendTextMessageAsync(userId, "✅ Заявка на роль мастера отправлена администратору.");
        foreach (var adminId in AdminIds)
            await Bot.SendTextMessageAsync(adminId, $"📢 Пользователь {userId} подал заявку на роль мастера.");
    }

    public new async Task HandleStateInput(Message msg, UserState state)
    {
        var userId = msg.From.Id;
        var text = msg.Text;
        if (state.Action == "group_create")
        {
            if (state.Step == 0)
            {
                state.Data["name"] = text; state.Step = 1;
                var markup = new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🔓 Открытая", "group_private_no") },
                    new[] { InlineKeyboardButton.WithCallbackData("🔒 Приватная", "group_private_yes") }
                });
                await Bot.SendTextMessageAsync(userId, "Выберите тип группы:", replyMarkup: markup);
            }
            else if (state.Step == 2)
            {
                if (!int.TryParse(text, out int max) || max < 2) { await Bot.SendTextMessageAsync(userId, "❌ Введите число ≥2."); return; }
                state.Data["maxMembers"] = max; state.Step = 3;
                var chars = await Db.GetUserCharacters(userId);
                var btns = chars.Select(c => new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"{c.Name} ({c.Class} ур.{c.Level})", $"group_create_char_{c.Id}") }).ToList();
                btns.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Отмена", "groups_menu") });
                await Bot.SendTextMessageAsync(userId, "Выберите персонажа для группы:", replyMarkup: new InlineKeyboardMarkup(btns));
            }
        }
        else
        {
            await base.HandleStateInput(msg, state);
        }
    }
}