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

public static class Texts
{
    public const string Help =
@"Добро пожаловать! ⛵

Начни с команды /boats, чтобы посмотреть список лодок и людей на них.

Команды:
/me – твой профиль
/find <query> – найти по имени, @username или городу
/boat <name> – список участников лодки или по капитану
/boats - список всех лодок
/like <@username> – отправить лайк
/likes – список людей, которым ты поставил лайк
/likers – количество лайков тебе
/matches – взимные лайки (go for it!)
/unlike <@username> – убрать лайк
/bio – обновить информацию о себе
/insta - обновить Instagram профиль
/name - обновить отображаемое имя 
/city - обновить город

Совет: Используй 👍 кнопку лайка на профилях

Отправь фото боту, чтобы обновить фото профиля";
}