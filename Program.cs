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
        var token = "8926544978:AAHHVBL1VuHlg4KRDC0m0Vqq00B_yeJ_uR0"; // Замените на свой токен
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