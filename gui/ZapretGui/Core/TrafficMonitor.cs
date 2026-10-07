using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows.Threading;

namespace ZapretGui.Core;

/// <summary>
/// Живые метрики для панели: процесс winws.exe и трафик сетевых интерфейсов.
/// Счётчики пакетов — общесистемные (иначе их взять неоткуда), поэтому в UI они
/// подписаны как трафик интерфейса, а не как «трафик zapret».
/// </summary>
public sealed class TrafficMonitor : ObservableObject
{
    public const int SampleCapacity = 240;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private static TrafficMonitor? _instance;
    public static TrafficMonitor Instance => _instance ??= new TrafficMonitor();

    private readonly DispatcherTimer _timer;
    private readonly double[] _samples = new double[SampleCapacity];
    private readonly TrafficCounterSampler _counter = new();
    private long _prevStamp;
    private int _generation;
    private bool _sampling;

    private TrafficMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
        _timer.Tick += async (_, _) => await SampleAsync();
    }

    /// <summary>Кольцевой буфер значений «пакетов/с», индекс 0 — самое старое.</summary>
    public IReadOnlyList<double> Samples => _samples;

    private int? _winwsPid;
    public int? WinwsPid
    {
        get => _winwsPid;
        private set { if (Set(ref _winwsPid, value)) RaiseMany(nameof(WinwsPidText), nameof(IsWinwsAlive)); }
    }

    public bool IsWinwsAlive => _winwsPid.HasValue;
    public string WinwsPidText => _winwsPid?.ToString() ?? "—";

    private double _winwsMemoryMb;
    public double WinwsMemoryMb
    {
        get => _winwsMemoryMb;
        private set { if (Set(ref _winwsMemoryMb, value)) Raise(nameof(WinwsMemoryText)); }
    }

    public string WinwsMemoryText => _winwsPid.HasValue ? $"{_winwsMemoryMb:0} МБ · winws.exe" : "процесс не запущен";

    private double _packetsPerSecond;
    public double PacketsPerSecond
    {
        get => _packetsPerSecond;
        private set { if (Set(ref _packetsPerSecond, value)) Raise(nameof(PacketsText)); }
    }

    public string PacketsText => _packetsPerSecond >= 1000
        ? $"{_packetsPerSecond / 1000:0.0}k"
        : $"{_packetsPerSecond:0}";

    private double _bytesPerSecond;
    public double BytesPerSecond
    {
        get => _bytesPerSecond;
        private set { if (Set(ref _bytesPerSecond, value)) Raise(nameof(SpeedText)); }
    }

    public string SpeedText
    {
        get
        {
            var bits = _bytesPerSecond * 8;
            if (bits >= 1_000_000) return $"{bits / 1_000_000:0.0} Мбит/с";
            if (bits >= 1_000) return $"{bits / 1_000:0} Кбит/с";
            return $"{bits:0} бит/с";
        }
    }

    private string _transferRatesText = "↓ 0 бит/с · ↑ 0 бит/с";
    public string TransferRatesText
    {
        get => _transferRatesText;
        private set => Set(ref _transferRatesText, value);
    }

    private string _sessionTrafficText = "↓ 0 Б · ↑ 0 Б";
    public string SessionTrafficText
    {
        get => _sessionTrafficText;
        private set => Set(ref _sessionTrafficText, value);
    }

    /// <summary>Пик за окно выборок — нужен, чтобы нормировать график.</summary>
    public double PeakPackets
    {
        get
        {
            double max = 1;
            foreach (var s in _samples) if (s > max) max = s;
            return max;
        }
    }

    /// <summary>Взведён после каждой удачной выборки — по нему перерисовывается график.</summary>
    public event EventHandler? Sampled;

    public void Start()
    {
        if (_timer.IsEnabled) return;
        _generation++;
        _prevStamp = 0;
        _counter.Reset();
        Array.Clear(_samples);
        PacketsPerSecond = 0;
        BytesPerSecond = 0;
        TransferRatesText = "↓ 0 бит/с · ↑ 0 бит/с";
        SessionTrafficText = "↓ 0 Б · ↑ 0 Б";
        _timer.Start();
    }

    public void Stop()
    {
        _generation++;
        _timer.Stop();
    }

    private async Task SampleAsync()
    {
        if (_sampling) return;
        _sampling = true;
        var generation = _generation;
        try
        {
            var snapshot = await Task.Run(Collect);
            if (snapshot is null || generation != _generation || !_timer.IsEnabled) return;

            var (adapters, pid, memoryMb, stamp) = snapshot.Value;

            WinwsPid = pid;
            WinwsMemoryMb = memoryMb;

            var seconds = _prevStamp == 0 ? 0 : Stopwatch.GetElapsedTime(_prevStamp, stamp).TotalSeconds;
            var sample = _counter.Take(adapters, seconds);
            _prevStamp = stamp;
            PacketsPerSecond = sample.PacketsPerSecond;
            BytesPerSecond = sample.ReceiveBytesPerSecond + sample.SendBytesPerSecond;
            TransferRatesText = $"↓ {FormatRate(sample.ReceiveBytesPerSecond)} · ↑ {FormatRate(sample.SendBytesPerSecond)}";
            SessionTrafficText = $"↓ {FormatBytes(_counter.TotalReceivedBytes)} · ↑ {FormatBytes(_counter.TotalSentBytes)}";
            Push(PacketsPerSecond);
            Sampled?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
        }
        finally
        {
            _sampling = false;
        }
    }

    private static (IReadOnlyList<AdapterTrafficSnapshot> adapters, int? pid, double memoryMb, long stamp)? Collect()
    {
        var adapters = new List<AdapterTrafficSnapshot>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                try
                {
                    var s = nic.GetIPStatistics();
                    adapters.Add(new AdapterTrafficSnapshot(
                        nic.Id,
                        s.UnicastPacketsReceived + s.UnicastPacketsSent,
                        s.BytesReceived,
                        s.BytesSent));
                }
                catch (NetworkInformationException)
                {
                    // Адаптер мог исчезнуть во время перечисления; остальные продолжают учитываться.
                }
            }
        }
        catch
        {
            return null;
        }

        int? pid = null;
        double memoryMb = 0;
        try
        {
            var procs = Process.GetProcessesByName("winws");
            try
            {
                if (procs.Length > 0)
                {
                    var currentPid = procs[0].Id;
                    var currentMemoryMb = procs[0].WorkingSet64 / 1024d / 1024d;
                    pid = currentPid;
                    memoryMb = currentMemoryMb;
                }
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        catch
        {
            // Процесс мог умереть между перечислением и чтением — не страшно.
        }

        return (adapters, pid, memoryMb, Stopwatch.GetTimestamp());
    }

    private static string FormatRate(double bytesPerSecond)
    {
        var bits = bytesPerSecond * 8;
        if (bits >= 1_000_000) return $"{bits / 1_000_000:0.0} Мбит/с";
        if (bits >= 1_000) return $"{bits / 1_000:0} Кбит/с";
        return $"{bits:0} бит/с";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024 * 1024):0.00} ГБ";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024d * 1024):0.0} МБ";
        if (bytes >= 1024) return $"{bytes / 1024d:0.0} КБ";
        return $"{bytes} Б";
    }

    private void Push(double value)
    {
        Array.Copy(_samples, 1, _samples, 0, SampleCapacity - 1);
        _samples[SampleCapacity - 1] = value;
    }
}
