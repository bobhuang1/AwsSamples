using Xunit;

namespace GameBackend.Tests;

public class ScoringTests
{
    private static readonly string[] Roster = ["alice", "bob"];

    [Fact]
    public void Points_come_from_kills_objectives_win_and_deaths()
    {
        var points = MatchScoring.Compute(new PlayerResult("alice", Kills: 3, Deaths: 2, Objectives: 1, Won: true));
        Assert.Equal(3 * 100 + 1 * 250 + 500 - 2 * 25, points);
    }

    [Fact]
    public void Points_never_go_below_zero()
        => Assert.Equal(0, MatchScoring.Compute(new PlayerResult("alice", 0, 50, 0, false)));

    [Fact]
    public void Points_are_capped_per_match()
    {
        var best = new PlayerResult("alice", MatchScoring.MaxKills, 0, MatchScoring.MaxObjectives, true);
        Assert.Equal(MatchScoring.MaxMatchPoints, MatchScoring.Compute(best));
    }

    [Fact]
    public void ScoreMatch_scores_every_player_of_the_roster_once()
    {
        var scores = MatchScoring.ScoreMatch([new("alice", 1, 0, 0, true), new("bob", 0, 1, 0, false)], Roster);
        Assert.Equal(600, scores["alice"]);
        Assert.Equal(0, scores["bob"]);
    }

    [Fact]
    public void ScoreMatch_rejects_a_second_result_for_the_same_player()
        => Assert.Throws<BadRequestException>(() => MatchScoring.ScoreMatch([new("alice", 1, 0, 0, true), new("alice", 2, 0, 0, true)], ["alice", "bob"]));

    [Fact]
    public void ScoreMatch_rejects_players_who_were_not_in_the_match()
        => Assert.Throws<BadRequestException>(() => MatchScoring.ScoreMatch([new("mallory", 1, 0, 0, true)], Roster));

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(MatchScoring.MaxKills + 1, 0, 0)]
    [InlineData(0, MatchScoring.MaxDeaths + 1, 0)]
    [InlineData(0, 0, MatchScoring.MaxObjectives + 1)]
    public void ScoreMatch_rejects_stats_outside_their_bounds(int kills, int deaths, int objectives)
        => Assert.Throws<BadRequestException>(() => MatchScoring.ScoreMatch([new("alice", kills, deaths, objectives, false)], Roster));

    [Fact]
    public void ScoreMatch_rejects_empty_and_oversized_results()
    {
        Assert.Throws<BadRequestException>(() => MatchScoring.ScoreMatch(null, Roster));
        Assert.Throws<BadRequestException>(() => MatchScoring.ScoreMatch([], Roster));
        Assert.Throws<BadRequestException>(() => MatchScoring.ScoreMatch([new("alice", 0, 0, 0, false), new("bob", 0, 0, 0, false), new("alice", 0, 0, 0, false)], Roster));
    }

    [Fact]
    public void Shard_key_is_stable_and_inside_the_shard_range()
    {
        var all = LeaderboardShards.All(8).ToHashSet();
        foreach (var id in Enumerable.Range(0, 200).Select(i => $"player-{i}"))
        {
            var key = LeaderboardShards.KeyFor(id, 8);
            Assert.Equal(key, LeaderboardShards.KeyFor(id, 8));
            Assert.Contains(key, all);
        }
    }

    [Fact]
    public void Players_spread_over_the_shards()
    {
        var used = Enumerable.Range(0, 400).Select(i => LeaderboardShards.KeyFor($"player-{i}", 8)).Distinct().Count();
        Assert.Equal(8, used);
    }

    [Fact]
    public void One_shard_means_one_key()
        => Assert.Equal(["global#0"], LeaderboardShards.All(1));
}
