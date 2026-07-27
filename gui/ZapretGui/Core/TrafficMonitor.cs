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
    private long _prevPackets = -1;
    private long _prevBytes = -1;
    private DateTime _prevStamp = DateTime.MinValue;
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
        _prevPackets = -1;
        _prevBytes = -1;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private async Task SampleAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var snapshot = await Task.Run(Collect);
            if (snapshot is null) return;

            var (packets, bytes, pid, memoryMb, stamp) = snapshot.Value;

            WinwsPid = pid;
            WinwsMemoryMb = memoryMb;

            if (_prevPackets >= 0 && _prevStamp != DateTime.MinValue)
            {
                var seconds = (stamp - _prevStamp).TotalSeconds;
                if (seconds > 0.05)
                {
                    // Счётчики интерфейса могут обнулиться при переподключении адаптера.
                    var dp = Math.Max(0, packets - _prevPackets);
                    var db = Math.Max(0, bytes - _prevBytes);
                    PacketsPerSecond = dp / seconds;
                    BytesPerSecond = db / seconds;
                    Push(PacketsPerSecond);
                    Sampled?.Invoke(this, EventArgs.Empty);
                }
            }

            _prevPackets = packets;
            _prevBytes = bytes;
            _prevStamp = stamp;
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

    private static (long packets, long bytes, int? pid, double memoryMb, DateTime stamp)? Collect()
    {
        long packets = 0, bytes = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var s = nic.GetIPStatistics();
                packets += s.UnicastPacketsReceived + s.UnicastPacketsSent;
                bytes += s.BytesReceived + s.BytesSent;
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
            if (procs.Length > 0)
            {
                pid = procs[0].Id;
                memoryMb = procs[0].WorkingSet64 / 1024d / 1024d;
            }
            foreach (var p in procs) p.Dispose();
        }
        catch
        {
            // Процесс мог умереть между перечислением и чтением — не страшно.
        }

        return (packets, bytes, pid, memoryMb, DateTime.UtcNow);
    }

    private void Push(double value)
    {
        Array.Copy(_samples, 1, _samples, 0, SampleCapacity - 1);
        _samples[SampleCapacity - 1] = value;
    }
}
