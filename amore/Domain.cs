using System;
using Azure;
using Azure.Data.Tables;

namespace amore;

public class Member : ITableEntity
{
    public string PartitionKey { get; set; }
    public string RowKey { get; set; }

    public long? UserId { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }


    public string? realName { get; set; }
    public string? photo { get; set; }
    public string? photoFileId { get; set; }
    public string telegram => RowKey.TrimStart('@');
    public string Username => telegram.ToLowerInvariant();

    public string BoatName => PartitionKey.Substring(0, PartitionKey.IndexOf('(') - 1);

    public string CaptainName => PartitionKey.Substring(PartitionKey.IndexOf('(') + 1, PartitionKey.IndexOf(')') - PartitionKey.IndexOf('(') - 1);
    public string? instagram { get; set; }
    public string? info { get; set; }

    public string? city { get; set; }

    public long? ChatId { get; set; }
}


public sealed record ProfileCard(Member Member);

public sealed record Like(string From, string To);

public sealed record MemberStat(string Member, int LikesSent, int LikesReceived, int Matches);

public static class Texts
{
    public const string Help =
@"Добро пожаловать! ⛵

Начни с команды /boats, чтобы посмотреть список лодок и людей на них.

Команды:
/me – твой профиль
/find запрос – найти по имени, @username или городу. Например, /find @amorofrost, /find Андрей или /find Москва
/boat лодка_или_капитан – список участников лодки или по капитану. Например, /boat хихик или /boat Олег
/boats - список всех лодок
/like @username – отправить лайк, например, /like @amorofrost
/likes – список людей, которым ты поставил лайк
/likers – количество лайков тебе
/matches – взимные лайки (go for it!)
/unlike @username – убрать лайк, например, /unlike @amorofrost
/bio – обновить информацию о себе, например, /bio Люблю путешествовать и котиков
/insta - обновить Instagram профиль, например, /insta amorofrost
/name - обновить отображаемое имя, например, /name Андрей
/city - обновить город, например, /city Москва
/stats - статистика бота

Совет: Используй 👍 кнопку лайка на профилях

Отправь фото боту, чтобы обновить фото профиля";
}