using ZapretGui.Core;

namespace ZapretGui.Smoke;

internal static class TrafficCounterSmoke
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException(message);
        }

        var sampler = new TrafficCounterSampler();
        var initial = sampler.Take(new[] { new AdapterTrafficSnapshot("ethernet", 10_000, 5_000_000, 800_000) }, 0.5);
        Check(initial.Packets == 0 && initial.ReceivedBytes == 0 && initial.SentBytes == 0,
            "Historic counters at launch must not be counted as session traffic.");

        var normal = sampler.Take(new[] { new AdapterTrafficSnapshot("ethernet", 10_020, 5_002_000, 800_500) }, 0.5);
        Check(normal.PacketsPerSecond == 40 && normal.ReceiveBytesPerSecond == 4000 && normal.SendBytesPerSecond == 1000,
            "Receive/send rates must use the actual sample interval.");
        Check(sampler.TotalReceivedBytes == 2000 && sampler.TotalSentBytes == 500,
            "Session totals must count only traffic collected since the baseline.");

        var added = sampler.Take(new[]
        {
            new AdapterTrafficSnapshot("ethernet", 10_030, 5_003_000, 800_750),
            new AdapterTrafficSnapshot("wifi", 90_000, 200_000_000, 20_000_000),
        }, 0.5);
        Check(added.Packets == 10 && added.ReceivedBytes == 1000 && added.SentBytes == 250,
            "Connecting Wi-Fi must not add its historical counters to the live graph.");

        var removed = sampler.Take(new[] { new AdapterTrafficSnapshot("wifi", 90_010, 200_001_000, 20_000_500) }, 0.5);
        Check(removed.Packets == 10 && removed.ReceivedBytes == 1000 && removed.SentBytes == 500,
            "Removing Ethernet must not lose traffic from the remaining Wi-Fi interface.");

        var reset = sampler.Take(new[] { new AdapterTrafficSnapshot("wifi", 1, 200_002_000, 20_001_000) }, 0.5);
        Check(reset.Packets == 0 && reset.ReceivedBytes == 0 && reset.SentBytes == 0,
            "A reset in any interface counter must rebaseline that interface as a whole.");
        var afterReset = sampler.Take(new[] { new AdapterTrafficSnapshot("wifi", 11, 200_003_000, 20_001_100) }, 0.5);
        Check(afterReset.Packets == 10 && afterReset.ReceivedBytes == 1000 && afterReset.SentBytes == 100,
            "Traffic after a counter reset must resume from the new baseline.");

        var disconnected = sampler.Take(Array.Empty<AdapterTrafficSnapshot>(), 0.5);
        Check(disconnected.PacketsPerSecond == 0 && disconnected.ReceiveBytesPerSecond == 0,
            "Disconnecting every interface must clear live speed.");
        var reconnected = sampler.Take(new[] { new AdapterTrafficSnapshot("wifi", 200, 300_000_000, 30_000_000) }, 0.5);
        Check(reconnected.Packets == 0 && reconnected.ReceivedBytes == 0,
            "Reconnecting an interface must not count traffic from the unobserved interval.");

        sampler.Reset();
        Check(sampler.TotalReceivedBytes == 0 && sampler.TotalSentBytes == 0,
            "Restarting the monitor must reset session totals.");
        sampler.Take(new[] { new AdapterTrafficSnapshot("wifi", 10, 100, 100) }, 0.5);
        var invalidInterval = sampler.Take(new[] { new AdapterTrafficSnapshot("wifi", 20, 200, 200) }, double.NaN);
        Check(double.IsFinite(invalidInterval.PacketsPerSecond) && invalidInterval.Packets == 0,
            "An invalid time interval must not poison graph values or totals.");
        var afterInvalid = sampler.Take(new[] { new AdapterTrafficSnapshot("wifi", 30, 300, 300) }, 0.5);
        Check(afterInvalid.Packets == 10 && sampler.TotalReceivedBytes == 100,
            "Sampling must recover after an invalid interval without recounting its bytes.");

        return checks;
    }
}
