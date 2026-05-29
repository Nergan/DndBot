using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using System.Collections.Concurrent;

namespace DndBot;

public class NotificationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITelegramBotClient _bot;
    private static readonly ConcurrentDictionary<string, DateTime> _lastSent = new();

    public NotificationService(IServiceScopeFactory scopeFactory, ITelegramBotClient bot)
    {
        _scopeFactory = scopeFactory;
        _bot = bot;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DatabaseService>();
                var now = DateTime.Now;
                var today = now.Date;
                var tomorrow = today.AddDays(1);

                var tomorrowGames = await db.GetConfirmedGameSessionsForDate(tomorrow);
                foreach (var game in tomorrowGames)
                {
                    string key = $"{game.Id}|daily";
                    if (!_lastSent.TryGetValue(key, out var last) || (now - last).TotalHours > 20)
                    {
                        await NotifyParticipants(db, game, $"⏰ Напоминание: игра завтра в {game.Time[..5]}");
                        _lastSent[key] = now;
                    }
                }

                var todayGames = await db.GetConfirmedGameSessionsForDate(today);
                var inOneHour = now.AddHours(1);
                foreach (var game in todayGames)
                {
                    if (TimeSpan.TryParse(game.Time, out var gameTime))
                    {
                        if (gameTime <= inOneHour.TimeOfDay && gameTime > now.TimeOfDay)
                        {
                            string key = $"{game.Id}|hourly";
                            if (!_lastSent.TryGetValue(key, out var last) || (now - last).TotalMinutes > 50)
                            {
                                await NotifyParticipants(db, game, $"🔔 Напоминание: игра через час в {game.Time[..5]}");
                                _lastSent[key] = now;
                            }
                        }
                    }
                }
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"NotificationService error: {ex.Message}");
            }
        }
    }

    private async Task NotifyParticipants(DatabaseService db, GameSession game, string message)
    {
        if (game.TeamId.HasValue)
        {
            var members = await db.GetTeamMembersWithCharacters(game.TeamId.Value);
            foreach (var member in members)
            {
                try
                {
                    await _bot.SendTextMessageAsync(member.user.Id, message);
                    await db.AddNotification(new Notification
                    {
                        UserId = member.user.Id,
                        Type = "reminder",
                        Content = message,
                        IsRead = false,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    // Игнорируем ошибки отправки – пользователь мог заблокировать бота
                    Console.WriteLine($"Не удалось отправить уведомление {member.user.Id}: {ex.Message}");
                }
            }
        }
        else if (game.PlayerId.HasValue)
        {
            try
            {
                await _bot.SendTextMessageAsync(game.PlayerId.Value, message);
                await db.AddNotification(new Notification
                {
                    UserId = game.PlayerId.Value,
                    Type = "reminder",
                    Content = message,
                    IsRead = false,
                    CreatedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Не удалось отправить уведомление {game.PlayerId.Value}: {ex.Message}");
            }
        }
    }
}