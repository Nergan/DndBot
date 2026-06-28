using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;

namespace DndBot;

class Program
{
    static async Task Main(string[] args)
    {
        var token = Environment.GetEnvironmentVariable("TOKEN") 
            ?? throw new InvalidOperationException("Переменная окружения TOKEN не задана.");
        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(token));
                services.AddSingleton<DatabaseService>();
                services.AddSingleton<BotHandlers>();
                //services.AddHostedService<NotificationService>();
            })
            .Build();

        var botClient = host.Services.GetRequiredService<ITelegramBotClient>();
        var handlers = host.Services.GetRequiredService<BotHandlers>();

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>()
        };

        botClient.StartReceiving(
            handlers.HandleUpdateAsync,
            handlers.HandleErrorAsync,
            receiverOptions
        );

        Console.WriteLine("Бот запущен...");
        await Task.Delay(-1);
    }
}