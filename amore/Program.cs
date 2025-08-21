using System;
using System.Text;
using amore;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace amore;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(cfg =>
            {
                cfg.AddJsonFile("appsettings.json", optional: true)
                   .AddEnvironmentVariables();
            })
            .ConfigureLogging(l => l.AddConsole())
            .ConfigureServices((ctx, services) =>
            {
                var token = ctx.Configuration["Telegram:BotToken"]
                            ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
                            ?? throw new InvalidOperationException("Bot token missing.");
                services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(token));

                var constr = ctx.Configuration["Azure:StorageConnectionString"]
                            ?? Environment.GetEnvironmentVariable("AZURE_CONNECTION_STRING")
                            ?? throw new InvalidOperationException("Azure connection string missing.");
                services.AddSingleton<ILoveRepo, AzSaLoveRepo>(sp => new AzSaLoveRepo(constr));
                services.AddSingleton<BlobServiceClient>(new BlobServiceClient(constr));
                services.AddSingleton<LikeService>();
                services.AddHostedService<BotHostedService>();
            })
            .Build();

        // Seed your roster here (load from CSV/JSON/DB)
        var repo = host.Services.GetRequiredService<ILoveRepo>();
        await repo.UpsertMembers(); // replace with your own loader

        await host.RunAsync();
    }

    /*private static IEnumerable<Member> DemoRoster()
    {
        // TODO: Replace with your real data loader (CSV/DB). TelegramUserId is required.
        return new[]
        {
            new Member(99108740, "amorofrost", "Andrey M", "Salty Kiss", "Captain Valera", "https://picsum.photos/seed/alice/600/800"),
            new Member(111111111, "alice", "Alice Johnson", "Sea Breeze", "Captain Tom", "https://picsum.photos/seed/alice/600/800"),
            new Member(222222222, "bob", "Bob Miller", "Sea Breeze", "Captain Tom", "https://picsum.photos/seed/bob/600/800"),
            new Member(333333333, "carol", "Carol Lee", "Sun Dancer", "Captain Maya", "https://picsum.photos/seed/carol/600/800"),
        };
    }*/
}

public sealed class BotHostedService : BackgroundService
{
    private const string BotVer = "v0.11";

    private readonly ITelegramBotClient _bot;
    private readonly ILoveRepo _repo;
    private readonly LikeService _likes;
    private readonly ILogger<BotHostedService> _log;

    private readonly BlobServiceClient _blobServiceClient;
    private readonly BlobContainerClient _blobContainerClient;

    public BotHostedService(ITelegramBotClient bot, ILoveRepo repo, LikeService likes, ILogger<BotHostedService> log, BlobServiceClient blobServiceClient)
    {
        _bot = bot; 
        _repo = repo; 
        _likes = likes;
        _log = log; 
        _blobServiceClient = blobServiceClient;
        _blobContainerClient = _blobServiceClient.GetBlobContainerClient("amore2025members");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await _bot.GetMe(stoppingToken);
        _log.LogInformation($"Bot @{me.Username} {BotVer} started");

        _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, new()
        {
            AllowedUpdates = Array.Empty<UpdateType>()
        }, cancellationToken: stoppingToken);
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        var err = ex switch
        {
            ApiRequestException apiEx => $"Telegram API Error:\n[{apiEx.ErrorCode}] {apiEx.Message}",
            _ => ex.ToString()
        };
        _log.LogError(err);
        return Task.CompletedTask;
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            if (update.Type == UpdateType.Message && update.Message!.Type == MessageType.Text)
            {
                await HandleMessage(update.Message!, ct);
            }
            else if (update.Type == UpdateType.Message && update.Message.Type == MessageType.Photo)
            {
                var fileId = update.Message.Photo.LastOrDefault()?.FileId;
                if (fileId is not null)
                {
                    var member = _repo.GetByUsername(update.Message.From!.Username.ToLowerInvariant()) ?? throw new InvalidOperationException("User not found in repo");
                    member.photoFileId = fileId; // Save file_id for future use
                    await _repo.UpdateMember(member);
                    
                    await _bot.SendMessage(update.Message.Chat.Id, "Фото профиля обновлено", cancellationToken: ct);
                }
            }
            else if (update.Type == UpdateType.CallbackQuery)
            {
                await HandleCallback(update.CallbackQuery!, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update handling failed");
        }
    }

    private bool IsAuthorized(User from) =>
        from is not null && _repo.IsAuthorized(from.Username.ToLowerInvariant());

    private async Task HandleMessage(Message msg, CancellationToken ct)
    {
        _log.LogInformation($"Received message from {msg.From?.Username}|{msg.From?.Id}: {msg.Text} (#{msg.Chat.Id})");

        if (msg.From is null) return;

        if (!IsAuthorized(msg.From))
        {
            _log.LogWarning("Unauthorized access attempt by {Username}", msg.From.Username);
            await _bot.SendMessage(msg.Chat.Id, $"Этот бот только для участников АолэЯхтинга 2025. Твой аккаунт ({msg.From.Username}) не в списке, напиши Андрею @amorofrost, если нужно тебя добавить", cancellationToken: ct);
            return;
        }

        var member = await HandleInit(msg, ct);

        var text = msg.Text!.Trim();
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (cmd)
        {
            case "/start":
                await HandleStartCmd(member, msg, ct);
                break;

            case "/help":
                await _bot.SendMessage(msg.Chat.Id, Texts.Help, cancellationToken: ct);
                break;

            case "/me":
                var me = member;
                await SendProfileCard(msg.Chat.Id, me, ct);
                break;

            case "/find":
                await HandleFindCmd(member, msg, arg, ct);
                break;

            case "/boat":
                await HandleBoatCmd(member, msg, arg, ct);
                break;

            case "/boats":
                await HandleBoatsCmd(member, msg, ct);
                break;

            case "/like":
                await HandleLikeCmd(member, msg, arg, ct);
                break;

            case "/likes":
                var likes = _repo.GetLikesFrom(msg.From.Username.ToLowerInvariant()).ToBlockingEnumerable(cancellationToken: ct);
                await SendList(msg.Chat.Id, likes, "Твои лайки:", ct);
                break;

            case "/likers":
                await HandleLikersCmd(member, msg, ct);
                break;

            case "/matches":
                await HandleMatchesCmd(member, msg, ct);
                break;

            case "/unlike":
                await HandleUnlikeCmd(member, msg, arg, ct);
                break;

            case "/bio":
                await HandleBioCmd(member, msg, arg, ct);
                break;

            case "/city":
                await HandleCityCmd(member, msg, arg, ct);
                break;

            case "/insta":
                await HandleInstaCmd(member, msg, arg, ct);
                break;

            case "/name":
                await HandleNameCmd(member, msg, arg, ct);
                break;

            case "/reload":
                await HandleReloadCmd(member, msg, ct);
                break;

            case "/broadcast":
                await HandleBroadcastCmd(member, msg, arg, ct);
                break;

            default:
                await _bot.SendMessage(msg.Chat.Id, "Unknown command. Type /help", cancellationToken: ct);
                break;
        }
    }

    private async Task<Member> HandleInit(Message msg, CancellationToken ct) 
    {
        var user = _repo.GetByUsername(msg.From.Username.ToLowerInvariant());

        if (user == null)
        {
            _log.LogWarning($"User {msg.From.Username} is not registered");
            await _bot.SendMessage(msg.Chat.Id, "Не могу найти тебя в списке пользователей. Пожалуйста, попробуй /start позже (или проверь навтройки приватности telegram username в настройках телеграмма).", cancellationToken: ct);
            return null; ;
        }

        bool needUpdate = false;
        if (user.UserId == null)
        {
            user.UserId = msg.From.Id; // Update UserId if not set
            _log.LogInformation($"Updating UserId for {user.Username} to {user.UserId}");
            needUpdate = true;
        }

        if (user.ChatId == null)
        {
            user.ChatId = msg.Chat.Id; // Update ChatId if not set
            _log.LogInformation($"Updating ChatId for {user.Username} to {user.ChatId}");
            needUpdate = true;
        }

        if (needUpdate)
        {
            try
            {
                await _repo.UpdateMember(user); // Save the updated member
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to update member ChatId");
                await _bot.SendMessage(msg.Chat.Id, "Произошла ошибка при обновлении твоего профиля. Пожалуйста, попробуй /start позже.", cancellationToken: ct);
            }
        }

        return user;
    }

    private async Task HandleStartCmd(Member m, Message msg, CancellationToken ct)
    {
        _log.LogInformation("User {Username} started the bot", msg.From?.Username);

        if (msg.From is null)
        {
            _log.LogWarning("Message from null user, ignoring");
            await _bot.SendMessage(msg.Chat.Id, "Не могу тебя идентифицировать. Пожалуйста, попробуй /start позже (или проверь навтройки приватности telegram username в настройках телеграмма).", cancellationToken: ct);
            return;
        }

        await _bot.SendMessage(msg.Chat.Id, $"Добро пожаловать, {msg.From.FirstName}! Отправь /boats, чтобы посмотреть список лодок или /help, чтобы увидеть список всех команд", cancellationToken: ct);

    }

    private async Task HandleFindCmd(Member m, Message msg, string arg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await _bot.SendMessage(msg.Chat.Id, "Используй /find <имя or @username>, например /find @amorofrost или /find Андрей", cancellationToken: ct);
            return;
        }
        var results = _repo.SearchMembers(arg).ToList();
        if (results.Count == 0)
        {
            await _bot.SendMessage(msg.Chat.Id, "Никого не найдено 🤷", cancellationToken: ct);
        }
        else
        {
            foreach (var mr in results.Take(10))
                await SendProfileCard(msg.Chat.Id, mr, ct);
            if (results.Count > 10)
                await _bot.SendMessage(msg.Chat.Id, $"…и еще {results.Count - 10}.", cancellationToken: ct);
        }
    }

    private async Task HandleBoatCmd(Member m, Message msg, string arg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await _bot.SendMessage(msg.Chat.Id, "Используй: /boat <название команды> или /boat <имя капитана>, например /boat Vagabondo или /boat Валера", cancellationToken: ct);
            return;
        }
        var boatMembers = _repo.MembersByBoatOrCaptain(arg).ToList();
        if (boatMembers.Count == 0)
        {
            await _bot.SendMessage(msg.Chat.Id, "На этой лодке никого нет", cancellationToken: ct);
        }
        else
        {
            var header = $"Команда “{arg}”:";
            await _bot.SendMessage(msg.Chat.Id, header, cancellationToken: ct);
            foreach (var mb in boatMembers)
                await SendProfileCard(msg.Chat.Id, mb, ct);
        }
    }

    private async Task HandleBoatsCmd(Member m, Message msg, CancellationToken ct)
    {
        var allMembersGrouped = _repo.AllMembers().GroupBy(m => m.PartitionKey)
                    .OrderBy(g => g.Key)
                    .Select(g => new { Key = g.Key, Count = g.Count() });


        var kb = new InlineKeyboardMarkup();
        
        int k = 0;
        foreach (var group in allMembersGrouped)
        {
            var boatName = group.Key;
            var btn = InlineKeyboardButton.WithCallbackData($"{group.Key} ({group.Count})", $"boat:{group.Key.Substring(0, group.Key.IndexOf('(') - 1)}");
            kb.AddNewRow(btn);  // 1 boat per row?
        }

        await _bot.SendMessage(msg.Chat.Id, "Список лодок:", replyMarkup: kb, cancellationToken: ct);
    }

    private async Task HandleLikeCmd(Member m, Message msg, string arg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await _bot.SendMessage(msg.Chat.Id, "Используй /like <@username>", cancellationToken: ct);
            return;
        }

        var usernameOrAt = arg;

        var uname = usernameOrAt.Trim().TrimStart('@').ToLowerInvariant();
        var target = _repo.GetByUsername(uname);
        if (target is null)
        {
            await _bot.SendMessage(msg.Chat.Id, "Не могу найти этого участника", cancellationToken: ct);
            return;
        }

        var fromUsername = msg.From.Username?.ToLowerInvariant();

        if (target.Username == fromUsername)
        {
            await _bot.SendMessage(msg.Chat.Id, "Нравиться себе - это здорово 😅", cancellationToken: ct);
            return;
        }

        _likes.ToggleLike(fromUsername.ToLowerInvariant(), target.Username.ToLowerInvariant(), like: true);
        await _bot.SendMessage(msg.Chat.Id, $"Отправлен лайк {DisplayName(target)} 👍", cancellationToken: ct);

        if (await _likes.IsMatch(fromUsername.ToLowerInvariant(), target.Username.ToLowerInvariant()))
        {
            // Notify both parties
            var me = m;
            var msgA = $"🎉 У тебя мэтч с {DisplayName(target)} с лодки {target.BoatName}! Напиши привет!";
            var msgB = $"🎉 У тебя мэтч с {DisplayName(me)} с лодки {me.BoatName}! Напиши привет!";
            await _bot.SendMessage(msg.Chat.Id, msgA, cancellationToken: ct);
            if (target.ChatId != null)
            {
                await _bot.SendMessage(target.ChatId, msgB, cancellationToken: ct);
            }
            else if (target.UserId != null)
            {
                await _bot.SendMessage(target.UserId, msgB, cancellationToken: ct);
            }
        }
        else if (target.UserId != null)
        {
            await _bot.SendMessage(target.UserId, "Кто-то поставил тебе лайк. Кто же это может быть?", cancellationToken: ct);
        }
    }

    private async Task HandleLikersCmd(Member m, Message msg, CancellationToken ct) 
    {
        var likers = _repo.GetLikesTo(msg.From.Username.ToLowerInvariant()).ToBlockingEnumerable(cancellationToken: ct);

        var boats = likers
            .Select(uid => _repo.GetByUsername(uid.ToLowerInvariant()))
            .Where(member => member != null)
            .GroupBy(member => member!.BoatName)
            .OrderBy(g => g.Key);
        var likersCount = likers.Count();
        await _bot.SendMessage(msg.Chat.Id, $"У тебя {likersCount} лайков с лодок {string.Join(", ", boats.Select(g => g.Key))}", cancellationToken: ct);
    }

    private async Task HandleMatchesCmd(Member m, Message msg, CancellationToken ct)
    {
        var myLikes = _repo.GetLikesFrom(msg.From.Username.ToLowerInvariant()).ToBlockingEnumerable(cancellationToken: ct).ToHashSet();
        var mutuals = _repo.GetLikesTo(msg.From.Username.ToLowerInvariant()).ToBlockingEnumerable(cancellationToken: ct).Where(uid => myLikes.Contains(uid)).ToList();
        if (mutuals.Count == 0)
            await _bot.SendMessage(msg.Chat.Id, "Мэтчи еще впереди 💘", cancellationToken: ct);
        else
        {
            await _bot.SendMessage(msg.Chat.Id, $"У тебя {mutuals.Count} мэтчей", cancellationToken: ct);
            foreach (var name in mutuals)
                if (_repo.GetByUsername(name.ToLowerInvariant()) is Member m1)
                    await SendProfileCard(msg.Chat.Id, m1, ct);
        }
    }

    private async Task HandleUnlikeCmd(Member m, Message msg, string arg, CancellationToken ct) 
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await _bot.SendMessage(msg.Chat.Id, "Используй /unlike <@username>", cancellationToken: ct);
            return;
        }
        var unameToUnlike = arg.Trim().TrimStart('@').ToLowerInvariant();
        var targetToUnlike = _repo.GetByUsername(unameToUnlike);
        if (targetToUnlike is null)
        {
            await _bot.SendMessage(msg.Chat.Id, "Не могу найти этого участника", cancellationToken: ct);
            return;
        }
        if (targetToUnlike.Username == msg.From.Username)
        {
            await _bot.SendMessage(msg.Chat.Id, "Нравиться себе - это здорово 😅", cancellationToken: ct);
            return;
        }
        _likes.ToggleLike(msg.From.Username, targetToUnlike.Username, like: false);
        await _bot.SendMessage(msg.Chat.Id, $"Лайк отменен для {DisplayName(targetToUnlike)} 👎", cancellationToken: ct);

    }

    private async Task HandleBioCmd(Member m, Message msg, string arg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await _bot.SendMessage(msg.Chat.Id, "/bio <your text> чтобы обновить описание своего профиля", cancellationToken: ct);
            return;
        }

        var userUpd = m;

        if (arg.Length > 1024)
        {
            await _bot.SendMessage(msg.Chat.Id, "Описание слишком длинное. Максимум 1024 символов.", cancellationToken: ct);
            return;
        }
        userUpd.info = arg;
        try
        {
            await _repo.UpdateMember(userUpd); // Save the updated member
            await _bot.SendMessage(msg.Chat.Id, $"Готово!", cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to update member UserId");
            await _bot.SendMessage(msg.Chat.Id, "Произошла ошибка при обновлении твоего профиля. Пожалуйста, попробуй позже.", cancellationToken: ct);
            return;
        }
    }

    private async Task HandleCityCmd(Member m, Message msg, string arg, CancellationToken ct) 
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await _bot.SendMessage(msg.Chat.Id, "/city <твой город> чтобы обновить свой город", cancellationToken: ct);
            return;
        }

        var userUpd = m;

        if (arg.Length > 128)
        {
            await _bot.SendMessage(msg.Chat.Id, "Название города слишком длинное. Максимум 128 символов.", cancellationToken: ct);
            return;
        }
        userUpd.city = arg;
        try
        {
            await _repo.UpdateMember(userUpd); // Save the updated member
            await _bot.SendMessage(msg.Chat.Id, $"Готово!", cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to update member UserId");
            await _bot.SendMessage(msg.Chat.Id, "Произошла ошибка при обновлении твоего профиля. Пожалуйста, попробуй позже.", cancellationToken: ct);
            return;
        }
    }

    private async Task HandleInstaCmd(Member m, Message msg, string arg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await _bot.SendMessage(msg.Chat.Id, "/insta <instagramName> (без @) чтобы обновить instagram", cancellationToken: ct);
            return;
        }

        var userUpdInst = m;
        if (arg.Length > 64)
        {
            await _bot.SendMessage(msg.Chat.Id, "Cлишком длинное instagram name. Максимум 64 символов.", cancellationToken: ct);
            return;
        }
        userUpdInst.instagram = arg;
        try
        {
            await _repo.UpdateMember(userUpdInst); // Save the updated member
            await _bot.SendMessage(msg.Chat.Id, $"Готово!", cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to update member UserId");
            await _bot.SendMessage(msg.Chat.Id, "Произошла ошибка при обновлении твоего профиля. Пожалуйста, попробуй позже.", cancellationToken: ct);
            return;
        }
    }

    private async Task HandleNameCmd(Member m, Message msg, string arg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await _bot.SendMessage(msg.Chat.Id, "/name <Имя> чтобы обновить отображаемое (реальное) имя", cancellationToken: ct);
            return;
        }

        var userUpdName = m;

        if (arg.Length > 64)
        {
            await _bot.SendMessage(msg.Chat.Id, "Cлишком длинное имя. Максимум 64 символов.", cancellationToken: ct);
            return;
        }
        userUpdName.realName = arg;
        try
        {
            await _repo.UpdateMember(userUpdName); // Save the updated member
            await _bot.SendMessage(msg.Chat.Id, $"Готово!", cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, $"Failed to update member {userUpdName.Username}");
            await _bot.SendMessage(msg.Chat.Id, "Произошла ошибка при обновлении твоего профиля. Пожалуйста, попробуй позже.", cancellationToken: ct);
            return;
        }
    }

    private async Task HandleReloadCmd(Member m, Message msg, CancellationToken ct)
    {
        if (msg.From.Id != 99108740)
        {
            await _bot.SendMessage(msg.Chat.Id, "Unauthorized.", cancellationToken: ct);
            return;
        }

        await _repo.UpsertMembers();
        await _bot.SendMessage(msg.Chat.Id, "Reloaded.", cancellationToken: ct);
    }

    private async Task HandleBoardcastCmd(Member m, Message msg, string arg, CancellationToken ct)
    {
        if (msg.From.Id != 99108740)
        {
            await _bot.SendMessage(msg.Chat.Id, "Unauthorized.", cancellationToken: ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(arg))
        {
            await _bot.SendMessage(msg.Chat.Id, "Используй /broadcast <сообщение> чтобы отправить сообщение всем участникам", cancellationToken: ct);
            return;
        }
        var members = _repo.AllMembers().ToList();
        foreach (var member in members)
        {
            try
            {
                if (member.ChatId != null)
                {
                    await _bot.SendMessage(member.ChatId, arg, cancellationToken: ct);
                }
                else if (member.UserId != null)
                {
                    await _bot.SendMessage(member.UserId, arg, cancellationToken: ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to send broadcast to {Username}", member.Username);
            }
        }
        await _bot.SendMessage(msg.Chat.Id, "Broadcast sent.", cancellationToken: ct);
    }

    private async Task HandleCallback(CallbackQuery cb, CancellationToken ct)
    {
        if (cb.From is null || cb.Message is null || string.IsNullOrWhiteSpace(cb.Data))
            return;

        if (!_repo.IsAuthorized(cb.From.Username.ToLowerInvariant()))
        {
            await _bot.AnswerCallbackQuery(cb.Id, "Not authorized", cancellationToken: ct);
            return;
        }

        if (cb.Data.StartsWith("like:"))
        {
            var targetName = cb.Data.Substring("like:".Length);
            
            if (targetName == cb.From.Username.ToLowerInvariant())
            {
                await _bot.AnswerCallbackQuery(cb.Id, "Нравиться себе - это здорово 😅", cancellationToken: ct);
                return;
            }

            _likes.ToggleLike(cb.From.Username.ToLowerInvariant(), targetName.ToLowerInvariant(), like: true);
            await _bot.AnswerCallbackQuery(cb.Id, "Liked! 👍", showAlert: false, cancellationToken: ct);

            var me = _repo.GetByUsername(cb.From.Username.ToLowerInvariant())!;
            var target = _repo.GetByUsername(targetName.ToLowerInvariant())!;

            if (await _likes.IsMatch(cb.From.Username.ToLowerInvariant(), targetName.ToLowerInvariant()))
            {
                if (me.ChatId != null)
                {
                    await _bot.SendMessage(me.ChatId, $"🎉 Кажется, это взаимно с {DisplayName(target)}!", cancellationToken: ct);
                }
                else if (me.UserId != null)
                {
                    await _bot.SendMessage(me.UserId, $"🎉 Кажется, это взаимно с {DisplayName(target)}!", cancellationToken: ct);
                }

                if (target.ChatId != null)
                {
                    await _bot.SendMessage(target.ChatId, $"🎉 Кажется, это взаимно с {DisplayName(me)}!", cancellationToken: ct);
                }
                else if (target.UserId != null)
                {
                    await _bot.SendMessage(target.UserId, $"🎉 Кажется, это взаимно с {DisplayName(me)}!", cancellationToken: ct);
                }
            } 
            else if (target.UserId != null) 
            {
                await _bot.SendMessage(target.UserId, "Кто-то поставил тебе лайк. Кто же это может быть?", cancellationToken: ct);
            }
        }

        if (cb.Data.StartsWith("boat:"))
        {
            var boatName = cb.Data.Substring("boat:".Length);
            var boatMembers = _repo.MembersByBoatOrCaptain(boatName).ToList();
            if (boatMembers.Count == 0)
            {
                await _bot.AnswerCallbackQuery(cb.Id, "На этой лодке никого нет", cancellationToken: ct);
            }
            else
            {
                var header = $"Команда “{boatName}”:";
                await _bot.AnswerCallbackQuery(cb.Id, header, cancellationToken: ct);
                foreach (var m in boatMembers)
                    await SendProfileCard(cb.Message.Chat.Id, m, ct);
            }
        }
    }

    private async Task SendProfileCard(long chatId, Member m, CancellationToken ct)
    {
        var captionSb = new StringBuilder()
            .AppendLine($"👤 {m.realName} {(string.IsNullOrWhiteSpace(m.Username) ? "" : $"(@{m.Username})")}")
            .AppendLine($"⛵ Boat: {m.BoatName}")
            .AppendLine($"🧭 Captain: {m.CaptainName}");

        if (!string.IsNullOrWhiteSpace(m.city))
        {
            captionSb.AppendLine($"🌍 City: {m.city}");
        }

        if (!string.IsNullOrEmpty(m.instagram))
        {
            captionSb.AppendLine($"📸 Instagram: https://www.instagram.com/{m.instagram}");
        }

        if (!string.IsNullOrEmpty(m.info))
        {
            captionSb.AppendLine($"ℹ️ Bio: {m.info}");
        }

        var caption = captionSb
            .ToString();

        var likeBtn = InlineKeyboardButton.WithCallbackData("👍 Like", $"like:{m.Username}");
        var kb = new InlineKeyboardMarkup(likeBtn);

        if (!string.IsNullOrWhiteSpace(m.photoFileId))
        {
            await _bot.SendPhoto(chatId, m.photoFileId!, caption: caption, replyMarkup: kb, cancellationToken: ct);
        }
        else if (!string.IsNullOrWhiteSpace(m.photo))
        {
            try
            {
                var msg = await _bot.SendPhoto(chatId, m.photo!, caption: caption, replyMarkup: kb, cancellationToken: ct);
                var fileId = msg.Photo.FirstOrDefault()?.FileId;

                if (fileId != null)
                {
                    // Save the file_id for future use, if needed
                    m.photoFileId = fileId; // Update the member's photo with the file_id
                    await _repo.UpdateMember(m);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to send photo for member {Username}", m.Username);
                await _bot.SendMessage(chatId, "(Не удалось загрузить фото)\r\n" + caption, replyMarkup: kb, cancellationToken: ct);
            }
        }
        else
        {
            await _bot.SendMessage(chatId, caption, replyMarkup: kb, cancellationToken: ct);
        }
    }

    private async Task SendList(long chatId, IEnumerable<string> userNames, string title, CancellationToken ct)
    {
        var list = userNames
            .Select(name => _repo.GetByUsername(name.ToLowerInvariant()))
            .Where(m => m is not null)!
            .Select(m => $"• {DisplayName(m!)}")
            .ToList();

        if (list.Count == 0)
            await _bot.SendMessage(chatId, "Пока ничего.", cancellationToken: ct);
        else
            await _bot.SendMessage(chatId, $"{title}\n{string.Join("\n", list)}", cancellationToken: ct);
    }

    private static string DisplayName(Member m) =>
        string.IsNullOrWhiteSpace(m.Username) ? m.realName : $"{m.realName} (@{m.Username})";
}