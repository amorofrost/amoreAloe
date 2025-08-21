using amore;
using Azure;
using Azure.Data.Tables;
using System.Collections.Concurrent;

namespace amore;

public interface ILoveRepo
{
    Member? GetByUsername(string usernameNoAtLower);
    IEnumerable<Member> SearchMembers(string query);
    IEnumerable<Member> MembersByBoatOrCaptain(string query);
    IEnumerable<Member> AllMembers();

    // Likes
    Task AddLike(string fromUserName, string toUserName);
    Task RemoveLike(string fromUserName, string toUserName);
    Task<bool> HasLiked(string fromUserName, string toUserName);
    IAsyncEnumerable<string> GetLikesFrom(string fromUserName);
    IAsyncEnumerable<string> GetLikesTo(string toUserName);

    // Roster management
    Task UpsertMembers();

    Task UpdateMember(Member member);

    bool IsAuthorized(string tgUserName);
}

public sealed class AzSaLoveRepo : ILoveRepo
{
    private readonly TableServiceClient _tableServiceClient;
    private readonly TableClient _tableMembers;
    private readonly TableClient _tableLikesFrom;
    private readonly TableClient _tableLikesTo;

    private readonly Dictionary<string, Member> _membersByUsername = new(); // lowercase key, no '@'

    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, byte>> _likes = new();

    public AzSaLoveRepo(string connectionString)
    {
        _tableServiceClient = new TableServiceClient(connectionString);
        _tableMembers = _tableServiceClient.GetTableClient("amore2025members");
        _tableLikesFrom = _tableServiceClient.GetTableClient("amore2025likesfrom");
        _tableLikesTo = _tableServiceClient.GetTableClient("amore2025likesto");
    }

    public async Task AddLike(string fromUserName, string toUserName)
    {
        await _tableLikesFrom.AddEntityAsync(new TableEntity(fromUserName, toUserName));
        await _tableLikesTo.AddEntityAsync(new TableEntity(toUserName, fromUserName));

    }

    public IEnumerable<Member> AllMembers()
    {
        return _membersByUsername.Values.OrderBy(m => m.realName);
    }

    public Member? GetByUsername(string usernameNoAtLower) =>
        _membersByUsername.TryGetValue(usernameNoAtLower, out var m) ? m : null;

    public async IAsyncEnumerable<string> GetLikesFrom(string fromUserName)
    {
        await foreach (var likeFrom in _tableLikesFrom.QueryAsync<TableEntity>(e => e.PartitionKey == fromUserName))
        {
            yield return likeFrom.RowKey;
        }
    }

    public async IAsyncEnumerable<string> GetLikesTo(string toUserName)
    {
        await foreach (var likeTo in _tableLikesTo.QueryAsync<TableEntity>(e => e.PartitionKey == toUserName))
        {
            yield return likeTo.RowKey;
        }
    }

    public async Task<bool> HasLiked(string fromUserName, string toUserName)
    {
        var like = await _tableLikesFrom.GetEntityIfExistsAsync<TableEntity>(fromUserName.ToLowerInvariant(), toUserName.ToLowerInvariant());
        return like.HasValue;
    }

    public bool IsAuthorized(string tgUserName)
    {
        return _membersByUsername.ContainsKey(tgUserName.TrimStart('@').ToLowerInvariant());
    }

    public IEnumerable<Member> MembersByBoatOrCaptain(string query)
    {
        var q = query.Trim().ToLowerInvariant();
        return _membersByUsername.Values
            .Where(m => m.BoatName.ToLowerInvariant().Contains(q)
                     || m.CaptainName.ToLowerInvariant().Contains(q))
            .OrderBy(m => m.BoatName).ThenBy(m => m.realName);
    }

    public async Task RemoveLike(string fromUserName, string toUserName)
    {
        await _tableLikesFrom.DeleteEntityAsync(fromUserName, toUserName);
        await _tableLikesTo.DeleteEntityAsync(toUserName, fromUserName);
    }

    public IEnumerable<Member> SearchMembers(string query)
    {
        var q = query.Trim().TrimStart('@').ToLowerInvariant();
        if (_membersByUsername.ContainsKey(q)) { 
            yield return _membersByUsername[q]; 
            yield break; 
        }

        foreach (var member in _membersByUsername.Values)
        {
            if (member.realName.ToLowerInvariant().Contains(q))
            {
                yield return member;
            }
        }
    }

    public async Task UpdateMember(Member member)
    {
        try
        {
            await _tableMembers.UpdateEntityAsync(member, member.ETag, TableUpdateMode.Replace);
        }
        // catch and retry UpdateConditionNotSatisfied 
        catch (RequestFailedException ex) when (ex.Status == 412)
        {            
            var memberNew = await _tableMembers.GetEntityAsync<Member>(member.PartitionKey, member.RowKey);
            memberNew.Value.realName = member.realName;
            memberNew.Value.ChatId = member.ChatId;
            memberNew.Value.UserId = member.UserId;
            memberNew.Value.photo = member.photo;
            memberNew.Value.photoFileId = member.photoFileId;
            memberNew.Value.instagram = member.instagram;
            memberNew.Value.info = member.info;
            await _tableMembers.UpdateEntityAsync(memberNew.Value, memberNew.Value.ETag, TableUpdateMode.Replace);
        }
    }

    public async Task UpsertMembers()
    {
        await foreach (var item in _tableMembers.QueryAsync<Member>())
        {
            _membersByUsername[item.RowKey.ToLowerInvariant()] = item;
        }
    }
}

public sealed class InMemoryLoveRepo
{
    private readonly ConcurrentDictionary<long, Member> _membersById = new();
    private readonly ConcurrentDictionary<string, Member> _membersByUsername = new(); // lowercase key, no '@'

    // likes[from] = set(to...)
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, byte>> _likes = new();


    public Member? GetByTelegramId(long tgUserId) =>
        _membersById.TryGetValue(tgUserId, out var m) ? m : null;

    public Member? GetByUsername(string usernameNoAtLower) =>
        _membersByUsername.TryGetValue(usernameNoAtLower, out var m) ? m : null;

    public IEnumerable<Member> SearchMembers(string query)
    {
        var q = query.Trim().TrimStart('@').ToLowerInvariant();
        return _membersById.Values
            .Where(m =>
                (!string.IsNullOrWhiteSpace(m.Username) && m.Username.ToLowerInvariant().Contains(q))
                || m.realName.ToLowerInvariant().Contains(q));
    }

    public IEnumerable<Member> MembersByBoatOrCaptain(string query)
    {
        var q = query.Trim().ToLowerInvariant();
        return _membersById.Values
            .Where(m => m.BoatName.ToLowerInvariant().Contains(q)
                     || m.CaptainName.ToLowerInvariant().Contains(q))
            .OrderBy(m => m.BoatName).ThenBy(m => m.realName);
    }

    public IEnumerable<Member> AllMembers() => _membersById.Values.OrderBy(m => m.realName);

    public void AddLike(long fromUserId, long toUserId)
    {
        var set = _likes.GetOrAdd(fromUserId, _ => new ConcurrentDictionary<long, byte>());
        set[toUserId] = 1;
    }

    public void RemoveLike(long fromUserId, long toUserId)
    {
        if (_likes.TryGetValue(fromUserId, out var set))
            set.TryRemove(toUserId, out _);
    }

    public bool HasLiked(long fromUserId, long toUserId) =>
        _likes.TryGetValue(fromUserId, out var set) && set.ContainsKey(toUserId);

    public IEnumerable<long> GetLikesFrom(long fromUserId) =>
        _likes.TryGetValue(fromUserId, out var set) ? set.Keys : Enumerable.Empty<long>();

    public IEnumerable<long> GetLikesTo(long toUserId) =>
        _likes.Where(kv => kv.Value.ContainsKey(toUserId)).Select(kv => kv.Key);

    public bool IsAuthorized(long tgUserId) => _membersById.ContainsKey(tgUserId);
}

public sealed class LikeService
{
    private readonly ILoveRepo _repo;

    public LikeService(ILoveRepo repo) => _repo = repo;

    public bool ToggleLike(string fromUsername, string toUsername, bool like)
    {
        if (fromUsername == toUsername) return false;
        if (like) _repo.AddLike(fromUsername, toUsername);
        else _repo.RemoveLike(fromUsername, toUsername);
        return like;
    }

    public async Task<bool> IsMatch(string a, string b) =>
        await _repo.HasLiked(a, b) && await _repo.HasLiked(b, a);
}