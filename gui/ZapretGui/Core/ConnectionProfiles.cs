using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZapretGui.Core;

/// <summary>
/// A display-only Windows network name and a hash of the connected NLM network IDs.
/// No address, adapter identifier, SSID or raw network ID is persisted.
/// </summary>
public sealed record NetworkContext(string Key, string Name, bool IsAvailable)
{
    private const int MaxConnectedNetworks = 64;
    private static readonly Guid NetworkListManagerClass = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    /// <summary>
    /// Reads Windows Network List Manager locally. Unknown/offline identities fail closed:
    /// an adapter or gateway fallback could incorrectly reuse a different Wi-Fi profile.
    /// </summary>
    public static NetworkContext Capture()
    {
        if (!OperatingSystem.IsWindows())
            return Unavailable();

        object? managerObject = null;
        IEnumNetworks? networks = null;
        try
        {
            var type = Type.GetTypeFromCLSID(NetworkListManagerClass, throwOnError: true);
            managerObject = Activator.CreateInstance(type!);
            var manager = (INetworkListManager)managerObject!;
            networks = manager.GetNetworks(1); // NLM_ENUM_NETWORK_CONNECTED
            var ids = new List<Guid>();
            var names = new List<string>();
            for (int i = 0; i <= MaxConnectedNetworks; i++)
            {
                INetwork? network = null;
                try
                {
                    int result = networks.Next(1, out network, out uint fetched);
                    if (result < 0)
                        Marshal.ThrowExceptionForHR(result);
                    if (fetched == 0)
                        break;
                    if (fetched != 1 || network is null || i == MaxConnectedNetworks)
                        return Unavailable();
                    if (!network.IsConnected)
                        return Unavailable(); // Connectivity changed while enumerating.

                    var id = network.GetNetworkId();
                    if (id == Guid.Empty)
                        return Unavailable();
                    ids.Add(id);
                    var name = DisplayName(network.GetName());
                    if (name.Length > 0)
                        names.Add(name);
                }
                finally
                {
                    Release(network);
                }
            }

            var key = BuildKey(ids);
            if (key.Length == 0)
                return Unavailable();
            var label = string.Join(" · ", names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            return new NetworkContext(key, DisplayName(label), true);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or
                                   ArgumentException or TypeLoadException or
                                   MissingMethodException or NotSupportedException or
                                   UnauthorizedAccessException or SecurityException)
        {
            return Unavailable();
        }
        finally
        {
            Release(networks);
            Release(managerObject);
        }
    }

    private static NetworkContext Unavailable() => new(string.Empty, string.Empty, false);

    private static string BuildKey(IEnumerable<Guid> networkIds)
    {
        var ids = networkIds.ToArray();
        if (ids.Length == 0 || ids.Any(id => id == Guid.Empty))
            return string.Empty;
        var source = "windows-nlm-networks-v1\n" + string.Join(
            "\n", ids.Select(id => id.ToString("D")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private static string DisplayName(string? source)
    {
        var text = new string((source ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length <= 96 ? text : text[..96];
    }

    private static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;
        try { Marshal.ReleaseComObject(value); }
        catch (Exception ex) when (ex is InvalidComObjectException or COMException or ArgumentException) { }
    }

    // Exact prefix/vtable order from the Microsoft Windows SDK's netlistmgr.h:
    // https://learn.microsoft.com/windows/win32/api/netlistmgr/nf-netlistmgr-inetworklistmanager-getnetworks
    // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/netlistmgr.h
    // InterfaceIsDual accounts for the inherited IDispatch slots. Unused members
    // between GetName and IsConnected preserve their slots; no mutator is called.
    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        IEnumNetworks GetNetworks(int flags);
    }

    [ComImport, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworks
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNewEnum();

        [PreserveSig]
        int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out INetwork? network, out uint fetched);
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    }
}

/// <summary>A user-named recommendation, not a persisted running/scan state.</summary>
public sealed class ConnectionProfile
{
    public string Name { get; set; } = string.Empty;
    public string NetworkKey { get; set; } = string.Empty;
    public string StrategyName { get; set; } = string.Empty;
    public string StrategyFingerprint { get; set; } = string.Empty;
    public GameFilterMode Mode { get; set; }
    public ConnectionGoal Goal { get; set; }
    public string ProbeSuiteFingerprint { get; set; } = string.Empty;
    public DateTime SavedAtUtc { get; set; }
}

/// <summary>
/// Bounded local profiles; explicit paths keep tests away from actual user data.
/// Only user-triggered Save/Remove writes. Loading never repairs or rewrites a file.
/// </summary>
public sealed class ConnectionProfileStore
{
    public const int MaxProfiles = 20;
    public const int MaxNameLength = 80;
    public const int MaxStrategyNameLength = 260;
    public const int MaxFileBytes = 256 * 1024;
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16,
    };
    private readonly object _gate = new();
    private readonly string _path;
    private List<ConnectionProfile> _profiles;

    public static string DefaultPath => Path.Combine(AppPaths.DataDir, "connection-profiles.json");

    public ConnectionProfileStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _profiles = Load();
    }

    /// <summary>Detached read-only snapshots cannot mutate the committed store.</summary>
    public IReadOnlyList<ConnectionProfile> Profiles
    {
        get
        {
            lock (_gate)
                return Array.AsReadOnly(_profiles.Select(Clone).ToArray());
        }
    }

    /// <summary>One recommendation per network and goal; oldest entries are bounded out.</summary>
    public bool Save(ConnectionProfile profile)
    {
        var clean = Normalize(profile);
        if (clean is null)
            return false;
        lock (_gate)
        {
            var next = _profiles.Where(item => !SameIdentity(item, clean)).Select(Clone).ToList();
            next.Add(clean);
            next = Bound(next);
            // Do not report success for a valid but stale item discarded by retention.
            if (!next.Any(item => SameIdentity(item, clean)))
                return false;
            if (!TryWrite(next))
                return false;
            _profiles = next;
            return true;
        }
    }

    public bool Remove(ConnectionProfile profile)
    {
        var clean = Normalize(profile);
        if (clean is null)
            return false;
        lock (_gate)
        {
            // A stale UI snapshot must not delete a newer replacement.
            var next = _profiles.Where(item => !SameSavedProfile(item, clean)).Select(Clone).ToList();
            if (next.Count == _profiles.Count || !TryWrite(next))
                return false;
            _profiles = next;
            return true;
        }
    }

    public static bool IsCompatible(ConnectionProfile? profile, NetworkContext? network, Strategy? current)
    {
        var clean = Normalize(profile);
        return clean is not null && network is { IsAvailable: true } && current is not null
            && string.Equals(clean.NetworkKey, network.Key, StringComparison.Ordinal)
            && string.Equals(clean.StrategyName, current.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(clean.StrategyFingerprint, StrategyTestHistory.Fingerprint(current), StringComparison.Ordinal)
            && string.Equals(clean.ProbeSuiteFingerprint, StrategyTestHistory.CurrentProbeSuiteFingerprint(), StringComparison.Ordinal);
    }

    private List<ConnectionProfile> Load()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaxFileBytes)
                return new();
            var file = JsonSerializer.Deserialize<ProfileFile>(stream, JsonOptions);
            if (file is null || file.SchemaVersion != SchemaVersion || file.Profiles is null)
                return new();
            return Bound(file.Profiles.Select(Normalize).OfType<ConnectionProfile>());
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return new();
        }
    }

    private bool TryWrite(List<ConnectionProfile> profiles)
    {
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, Path.GetFileName(_path) + ".tmp-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new ProfileFile
                {
                    SchemaVersion = SchemaVersion,
                    Profiles = profiles.Cast<ConnectionProfile?>().ToList(),
                }, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory replace is atomic. Never delete the destination first,
            // and never downgrade a replacement failure to an in-place overwrite.
            if (File.Exists(_path))
                File.Replace(temporary, _path, destinationBackupFileName: null);
            else
                File.Move(temporary, _path);
            return true;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (IsStorageFailure(ex)) { }
            }
        }
    }

    private static List<ConnectionProfile> Bound(IEnumerable<ConnectionProfile> profiles)
        => profiles.OrderByDescending(item => item.SavedAtUtc)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .DistinctBy(item => (item.NetworkKey, item.Goal))
            .Take(MaxProfiles).Select(Clone).ToList();

    private static ConnectionProfile? Normalize(ConnectionProfile? profile)
    {
        if (profile is null)
            return null;
        var name = profile.Name?.Trim() ?? string.Empty;
        var strategyName = profile.StrategyName?.Trim() ?? string.Empty;
        var network = profile.NetworkKey?.Trim() ?? string.Empty;
        var strategy = profile.StrategyFingerprint?.Trim() ?? string.Empty;
        var suite = profile.ProbeSuiteFingerprint?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl)
            || strategyName.Length is 0 or > MaxStrategyNameLength || strategyName.Any(char.IsControl)
            || !IsFingerprint(network) || !IsFingerprint(strategy) || !IsFingerprint(suite)
            || !Enum.IsDefined(profile.Mode) || !Enum.IsDefined(profile.Goal))
            return null;
        DateTime saved;
        try
        {
            saved = profile.SavedAtUtc.Kind switch
            {
                DateTimeKind.Local => profile.SavedAtUtc.ToUniversalTime(),
                DateTimeKind.Utc => profile.SavedAtUtc,
                _ => DateTime.SpecifyKind(profile.SavedAtUtc, DateTimeKind.Utc),
            };
        }
        catch (ArgumentException) { return null; }
        if (saved == default || saved > DateTime.UtcNow.AddDays(1))
            return null;
        return new ConnectionProfile
        {
            Name = name,
            NetworkKey = network.ToUpperInvariant(),
            StrategyName = strategyName,
            StrategyFingerprint = strategy.ToUpperInvariant(),
            Mode = profile.Mode,
            Goal = profile.Goal,
            ProbeSuiteFingerprint = suite.ToUpperInvariant(),
            SavedAtUtc = saved,
        };
    }

    private static bool IsFingerprint(string value)
        => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static bool SameIdentity(ConnectionProfile first, ConnectionProfile second)
        => first.NetworkKey == second.NetworkKey && first.Goal == second.Goal;

    private static bool SameSavedProfile(ConnectionProfile first, ConnectionProfile second)
        => SameIdentity(first, second) && first.Name == second.Name
            && first.StrategyName == second.StrategyName && first.StrategyFingerprint == second.StrategyFingerprint
            && first.Mode == second.Mode && first.ProbeSuiteFingerprint == second.ProbeSuiteFingerprint
            && first.SavedAtUtc == second.SavedAtUtc;

    private static ConnectionProfile Clone(ConnectionProfile profile) => new()
    {
        Name = profile.Name,
        NetworkKey = profile.NetworkKey,
        StrategyName = profile.StrategyName,
        StrategyFingerprint = profile.StrategyFingerprint,
        Mode = profile.Mode,
        Goal = profile.Goal,
        ProbeSuiteFingerprint = profile.ProbeSuiteFingerprint,
        SavedAtUtc = profile.SavedAtUtc,
    };

    private static bool IsStorageFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or SecurityException or
            JsonException or NotSupportedException or ArgumentException;

    private sealed class ProfileFile
    {
        public int SchemaVersion { get; set; }
        public List<ConnectionProfile?>? Profiles { get; set; }
    }
}
