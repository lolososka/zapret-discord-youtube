namespace ZapretGui.Core;

public readonly record struct AdapterTrafficSnapshot(
    string Id,
    long Packets,
    long ReceivedBytes,
    long SentBytes);

public readonly record struct TrafficSample(
    long Packets,
    long ReceivedBytes,
    long SentBytes,
    double Seconds)
{
    public double PacketsPerSecond => Seconds > 0 ? Packets / Seconds : 0;
    public double ReceiveBytesPerSecond => Seconds > 0 ? ReceivedBytes / Seconds : 0;
    public double SendBytesPerSecond => Seconds > 0 ? SentBytes / Seconds : 0;
}

/// <summary>
/// Считает разницы отдельно для каждого адаптера. Подключение нового интерфейса,
/// исчезновение старого или сброс его счётчиков не становятся всплеском трафика.
/// </summary>
public sealed class TrafficCounterSampler
{
    private Dictionary<string, AdapterTrafficSnapshot> _previous = new(StringComparer.Ordinal);

    public long TotalReceivedBytes { get; private set; }
    public long TotalSentBytes { get; private set; }

    public TrafficSample Take(IEnumerable<AdapterTrafficSnapshot> snapshots, double seconds)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var current = snapshots
            .Where(snapshot => !string.IsNullOrEmpty(snapshot.Id) &&
                               snapshot.Packets >= 0 && snapshot.ReceivedBytes >= 0 && snapshot.SentBytes >= 0)
            .GroupBy(snapshot => snapshot.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        long packets = 0, received = 0, sent = 0;
        bool validInterval = double.IsFinite(seconds) && seconds > 0;
        if (validInterval)
        {
            foreach (var (id, next) in current)
            {
                if (!_previous.TryGetValue(id, out var before) ||
                    next.Packets < before.Packets || next.ReceivedBytes < before.ReceivedBytes ||
                    next.SentBytes < before.SentBytes)
                    continue;

                packets = AddSaturating(packets, next.Packets - before.Packets);
                received = AddSaturating(received, next.ReceivedBytes - before.ReceivedBytes);
                sent = AddSaturating(sent, next.SentBytes - before.SentBytes);
            }
        }

        _previous = current;
        TotalReceivedBytes = AddSaturating(TotalReceivedBytes, received);
        TotalSentBytes = AddSaturating(TotalSentBytes, sent);
        return new TrafficSample(packets, received, sent, validInterval ? seconds : 0);
    }

    public void Reset()
    {
        _previous.Clear();
        TotalReceivedBytes = 0;
        TotalSentBytes = 0;
    }

    private static long AddSaturating(long total, long amount)
        => total > long.MaxValue - amount ? long.MaxValue : total + amount;
}
