using System.Text;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.S3;

namespace GameBackend;

/// <summary>
/// Kinesis consumer: a telemetry event stream (e.g. "game_started",
/// "player_hit", "round_end") produced by game clients via PutRecords to the
/// Kinesis stream. Each invocation batches the raw JSONL lines into one S3
/// object partitioned by day, ready for Athena/QuickSight.
///
/// The event is parsed manually instead of using Amazon.Lambda.KinesisEvents,
/// which drags in AWSSDK.Core 4.x and conflicts with the 3.x service SDKs.
/// </summary>
public sealed class TelemetryConsumer
{
    private static readonly Lazy<AmazonS3Client> S3 = new(() => new AmazonS3Client());

    private readonly string _bucket = Shared.Env("TELEMETRY_BUCKET", "telemetry");

    public async Task<object?> Handle(JsonElement input, ILambdaContext context)
    {
        var lines = new List<string>();

        if (input.TryGetProperty("Records", out var records) && records.ValueKind == JsonValueKind.Array)
        {
            foreach (var record in records.EnumerateArray())
            {
                if (!record.TryGetProperty("kinesis", out var k) ||
                    !k.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(data.GetString()!));
                foreach (var line in decoded.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    lines.Add(line);
            }
        }

        if (lines.Count == 0) return null;

        var now = DateTimeOffset.UtcNow;
        var key = $"telemetry/{now:yyyy}/{now:MM}/{now:dd}/{Guid.NewGuid():N}.jsonl";

        await S3.Value.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            ContentBody = string.Join('\n', lines),
            ContentType = "application/x-ndjson",
        });

        context.Logger.LogInformation("Archived {0} telemetry lines to s3://{1}/{2}", lines.Count, _bucket, key);
        return null;
    }
}