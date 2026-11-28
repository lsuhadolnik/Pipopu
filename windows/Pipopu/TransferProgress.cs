using System.Diagnostics;

namespace Pipopu;

internal sealed record TransferProgress(string Id, string Name, string Stage, long? Bytes, long Total,
    double? BytesPerSecond, bool Done = false, bool Failed = false)
{
    internal static string FormatBytes(double bytes) => bytes switch
    {
        >= 1024d * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GiB",
        >= 1024d * 1024 => $"{bytes / (1024d * 1024):0.0} MiB",
        >= 1024 => $"{bytes / 1024:0.0} KiB",
        _ => $"{bytes:0} B"
    };
}

internal sealed class TransferProgressReporter(string id, string name, long total, Action<TransferProgress> report)
{
    private readonly object gate = new();
    private string stage = "";
    private long sampleTime;
    private long sampleBytes;
    private double? rate;
    private bool finished;

    internal void Update(string nextStage, long? bytes = null)
    {
        lock (gate)
        {
            if (finished)
                return;
            long now = Stopwatch.GetTimestamp();
            if (stage != nextStage)
            {
                stage = nextStage;
                sampleTime = now;
                sampleBytes = bytes ?? 0;
                rate = null;
            }
            else if (bytes is long count)
            {
                double seconds = Stopwatch.GetElapsedTime(sampleTime, now).TotalSeconds;
                if (seconds < 0.4 && count != total)
                    return;
                if (seconds >= 0.4 || (count == total && count > sampleBytes && seconds > 0))
                {
                    double measured = Math.Max(0, count - sampleBytes) / seconds;
                    rate = rate is double previous && measured > 0 ? 0.65 * measured + 0.35 * previous : measured;
                }
                sampleTime = now;
                sampleBytes = count;
            }
            else
            {
                return;
            }
            report(new(id, name, stage, bytes, total, rate));
        }
    }

    internal void Finish(string? error = null)
    {
        lock (gate)
        {
            if (finished)
                return;
            finished = true;
            report(new(id, name, error ?? "", null, total, null, Done: true, Failed: error is not null));
        }
    }
}
