using System.Security.Cryptography;
using System.Text;

namespace GameBackend;

/// <summary>One player's outcome in a finished match, as reported by the trusted game server.</summary>
public sealed record PlayerResult(string PlayerId, int Kills, int Deaths, int Objectives, bool Won);

/// <summary>The body of POST /matches/{matchId}/result.</summary>
public sealed record MatchResultRequest(IReadOnlyList<PlayerResult>? Results);

/// <summary>
/// Turns a match result into leaderboard points. The points are computed here from raw
/// match stats; no caller ever supplies a score, and every stat and the total are bounded.
/// </summary>
public static class MatchScoring
{
    public const int MaxKills = 50;
    public const int MaxDeaths = 50;
    public const int MaxObjectives = 20;

    public const int PointsPerKill = 100;
    public const int PointsPerObjective = 250;
    public const int WinBonus = 500;
    public const int PointsPerDeath = 25;

    /// <summary>Upper bound on the points one player can earn from one match.</summary>
    public const int MaxMatchPoints = 10_000;

    public static int Compute(PlayerResult r)
    {
        var raw = r.Kills * PointsPerKill + r.Objectives * PointsPerObjective + (r.Won ? WinBonus : 0) - r.Deaths * PointsPerDeath;
        return Math.Clamp(raw, 0, MaxMatchPoints);
    }

    /// <summary>
    /// Checks a result against the match roster and returns the points per player. At most
    /// one entry per player, only players of the match, stats inside their bounds.
    /// </summary>
    public static IReadOnlyDictionary<string, int> ScoreMatch(IReadOnlyList<PlayerResult>? results, IReadOnlyCollection<string> roster)
    {
        if (results is null || results.Count == 0)
            throw new BadRequestException("Body must be { results: [ { playerId, kills, deaths, objectives, won } ] }");
        if (results.Count > roster.Count)
            throw new BadRequestException("More results than players in the match");

        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            if (string.IsNullOrEmpty(r.PlayerId) || !roster.Contains(r.PlayerId))
                throw new BadRequestException("Every result must be for a player of this match");
            if (r.Kills is < 0 or > MaxKills || r.Deaths is < 0 or > MaxDeaths || r.Objectives is < 0 or > MaxObjectives)
                throw new BadRequestException($"kills 0-{MaxKills}, deaths 0-{MaxDeaths}, objectives 0-{MaxObjectives}");
            if (!scores.TryAdd(r.PlayerId, Compute(r)))
                throw new BadRequestException("Only one result per player per match");
        }
        return scores;
    }
}

/// <summary>
/// The leaderboard index has one hash key per shard ("global#0" ... "global#N-1") so score
/// writes spread over N index partitions instead of one hot "global" key. A player always
/// maps to the same shard; the top-N is read from every shard and merged.
/// </summary>
public static class LeaderboardShards
{
    public const int DefaultCount = 8;
    public const int MaxCount = 32;

    public static int CountFromEnv()
        => int.TryParse(Environment.GetEnvironmentVariable("LEADERBOARD_SHARDS"), out var n)
            ? Math.Clamp(n, 1, MaxCount)
            : DefaultCount;

    /// <summary>Stable across processes and releases (string.GetHashCode is randomized per process).</summary>
    public static string KeyFor(string playerId, int shardCount)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(playerId));
        var shard = (int)(BitConverter.ToUInt32(hash, 0) % (uint)shardCount);
        return $"global#{shard}";
    }

    public static IEnumerable<string> All(int shardCount) => Enumerable.Range(0, shardCount).Select(i => $"global#{i}");
}
