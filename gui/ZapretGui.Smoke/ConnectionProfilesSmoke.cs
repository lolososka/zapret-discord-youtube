using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZapretGui.Core;

namespace ZapretGui.Smoke;

internal static class ConnectionProfilesSmoke
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition)
                throw new InvalidOperationException(message);
        }

        // Pure identity tests: never enumerate real networks or call public services.
        var buildKey = typeof(NetworkContext).GetMethod("BuildKey", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Network identity seam missing.");
        string Key(params Guid[] ids) => (string)buildKey.Invoke(null, new object[] { ids })!;
        var homeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var workId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Check(Key(homeId).Length == 64 && Key(homeId).All(char.IsAsciiHexDigit), "Network identity must be SHA-256.");
        Check(Key(homeId) != homeId.ToString("D"), "Raw network GUIDs must not become profile keys.");
        Check(Key(homeId) != Key(workId), "Different Windows networks must not share a profile key.");
        Check(Key(homeId, workId) == Key(workId, homeId), "Network enumeration order must not affect identity.");
        Check(Key(homeId, homeId) == Key(homeId), "Repeated network IDs must not affect identity.");
        Check(Key() == string.Empty && Key(homeId, Guid.Empty) == string.Empty, "Offline/unknown network IDs must fail closed.");
        var networkInterface = typeof(NetworkContext).GetNestedType("INetwork", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Network COM contract missing.");
        Check(networkInterface.GetMethods().OrderBy(method => method.MetadataToken).Select(method => method.Name).SequenceEqual(new[]
        {
            "GetName", "SetName", "GetDescription", "SetDescription", "GetNetworkId", "GetDomainType",
            "GetNetworkConnections", "GetTimeCreatedAndConnected", "get_IsConnectedToInternet", "get_IsConnected",
        }), "COM INetwork declarations must preserve every Microsoft SDK vtable slot through IsConnected.");
        var enumInterface = typeof(NetworkContext).GetNestedType("IEnumNetworks", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Network enumerator COM contract missing.");
        Check(enumInterface.GetMethods().OrderBy(method => method.MetadataToken).Select(method => method.Name)
                .SequenceEqual(new[] { "GetNewEnum", "Next" })
            && (enumInterface.GetMethod("Next")!.GetMethodImplementationFlags() & MethodImplAttributes.PreserveSig) != 0,
            "COM enumeration must preserve its prefix and S_FALSE/end-of-enumeration result.");

        // Every write is explicitly under a unique temporary root. Do not evaluate DefaultPath.
        var tempParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.Combine(tempParent, "zapret-profile-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "profiles.json");
            var store = new ConnectionProfileStore(path);
            var strategy = new Strategy { Name = "general (ALT)", RawCommandLine = "--filter-tcp=443 --dpi-desync=fake" };
            var home = new NetworkContext(Key(homeId), "Display-only Windows network name", true);
            var other = new NetworkContext(Key(workId), home.Name, true);
            var now = DateTime.UtcNow;
            ConnectionProfile Make(string name = "Дом", ConnectionGoal goal = ConnectionGoal.All, string? key = null, DateTime? saved = null) => new()
            {
                Name = name,
                NetworkKey = key ?? home.Key,
                StrategyName = strategy.Name,
                StrategyFingerprint = StrategyTestHistory.Fingerprint(strategy),
                Mode = GameFilterMode.All,
                Goal = goal,
                ProbeSuiteFingerprint = StrategyTestHistory.CurrentProbeSuiteFingerprint(),
                SavedAtUtc = saved ?? now,
            };

            Check(store.Profiles.Count == 0 && !File.Exists(path), "Load must not create an empty file.");
            var profile = Make();
            Check(store.Save(profile), "A valid explicitly named recommendation must save.");
            profile.Name = "Mutated caller";
            Check(store.Profiles.Single().Name == "Дом", "Save must detach the caller's mutable DTO.");
            var snapshot = store.Profiles;
            snapshot[0].Name = "Mutated snapshot";
            Check(store.Profiles.Single().Name == "Дом", "Returned profiles must be detached snapshots.");
            Check(snapshot is ICollection<ConnectionProfile> { IsReadOnly: true }, "The profile collection must be read-only.");
            var persisted = File.ReadAllText(path);
            Check(!persisted.Contains(home.Name, StringComparison.Ordinal)
                && !persisted.Contains(homeId.ToString("D"), StringComparison.OrdinalIgnoreCase),
                "Persistence must not automatically include Windows network names or raw GUIDs.");
            Check(!persisted.Contains("Running", StringComparison.OrdinalIgnoreCase)
                && !persisted.Contains("Scanning", StringComparison.OrdinalIgnoreCase), "Profiles must contain no transient restore state.");
            var reloaded = new ConnectionProfileStore(path);
            Check(reloaded.Profiles.Count == 1 && reloaded.Profiles[0].Name == "Дом", "Saved profiles must survive reload.");
            Check(ConnectionProfileStore.IsCompatible(reloaded.Profiles[0], home, strategy), "Unchanged exact network/strategy/suite must be compatible.");
            Check(!ConnectionProfileStore.IsCompatible(reloaded.Profiles[0], other, strategy), "A same-name different network must not match.");
            Check(!ConnectionProfileStore.IsCompatible(reloaded.Profiles[0], home with { IsAvailable = false }, strategy), "An unavailable network must not match.");
            Check(!ConnectionProfileStore.IsCompatible(reloaded.Profiles[0], home with { Key = string.Empty }, strategy), "Unknown network identity must not match.");
            var edited = new Strategy { Name = strategy.Name, RawCommandLine = strategy.RawCommandLine + " --new-option" };
            Check(!ConnectionProfileStore.IsCompatible(reloaded.Profiles[0], home, edited), "Edited strategy commands must invalidate a profile.");
            var renamed = new Strategy { Name = "renamed", RawCommandLine = strategy.RawCommandLine };
            Check(!ConnectionProfileStore.IsCompatible(reloaded.Profiles[0], home, renamed), "A missing/renamed strategy must not match.");
            var wrongSuite = Make();
            wrongSuite.ProbeSuiteFingerprint = new string('A', 64);
            Check(!ConnectionProfileStore.IsCompatible(wrongSuite, home, strategy), "Old probe suites must require a fresh scan.");

            Check(store.Save(Make("YouTube дома", ConnectionGoal.YouTube)), "Different goals on one network must coexist.");
            Check(store.Profiles.Count == 2, "Profiles must deduplicate by network plus goal, not network alone.");
            var stale = store.Profiles.Single(item => item.Goal == ConnectionGoal.All);
            Check(store.Save(Make("Новый дом", saved: now.AddMinutes(1))) && store.Profiles.Count == 2, "Saving the same network/goal must replace, not duplicate.");
            Check(!store.Remove(stale), "A stale snapshot must not delete a newer replacement.");
            Check(store.Remove(store.Profiles.Single(item => item.Goal == ConnectionGoal.YouTube)), "Explicit removal must persist.");
            Check(new ConnectionProfileStore(path).Profiles.Count == 1, "Removal must survive reload.");

            foreach (var invalid in new Action<ConnectionProfile>[]
            {
                item => item.Name = " ",
                item => item.Name = new string('x', ConnectionProfileStore.MaxNameLength + 1),
                item => item.Name = "bad\nname",
                item => item.StrategyName = new string('x', ConnectionProfileStore.MaxStrategyNameLength + 1),
                item => item.NetworkKey = string.Empty,
                item => item.NetworkKey = homeId.ToString("D"),
                item => item.StrategyFingerprint = new string('G', 64),
                item => item.ProbeSuiteFingerprint = "not-a-hash",
                item => item.Mode = (GameFilterMode)999,
                item => item.Goal = (ConnectionGoal)999,
                item => item.SavedAtUtc = default,
                item => item.SavedAtUtc = now.AddDays(2),
            })
            {
                var bad = Make();
                invalid(bad);
                var before = File.ReadAllText(path);
                Check(!store.Save(bad) && File.ReadAllText(path) == before && store.Profiles.Count == 1,
                    "Invalid profile input must not alter committed memory or disk.");
                Check(!ConnectionProfileStore.IsCompatible(bad, home, strategy), "Malformed profile input must never be compatible.");
            }

            var lower = Make("  Нормализован  ");
            lower.NetworkKey = "  " + lower.NetworkKey.ToLowerInvariant() + "  ";
            lower.StrategyFingerprint = lower.StrategyFingerprint.ToLowerInvariant();
            lower.ProbeSuiteFingerprint = lower.ProbeSuiteFingerprint.ToLowerInvariant();
            lower.SavedAtUtc = DateTime.SpecifyKind(now, DateTimeKind.Unspecified);
            Check(store.Save(lower), "Valid lowercase fingerprints and UTC-unspecified dates must normalize.");
            Check(store.Profiles.Single().Name == "Нормализован" && store.Profiles.Single().NetworkKey == home.Key
                && store.Profiles.Single().SavedAtUtc.Kind == DateTimeKind.Utc, "Normalized copies must remain bounded and canonical.");

            var boundedPath = Path.Combine(root, "bounded.json");
            var bounded = new ConnectionProfileStore(boundedPath);
            for (int i = 0; i < 25; i++)
                Check(bounded.Save(Make("Network " + i, key: Hash("network-" + i), saved: now.AddSeconds(i))), "Bounded saves must succeed.");
            Check(bounded.Profiles.Count == ConnectionProfileStore.MaxProfiles, "No more than twenty profiles may be retained.");
            Check(bounded.Profiles[0].Name == "Network 24" && bounded.Profiles[^1].Name == "Network 5", "Retention must keep the newest twenty profiles.");
            Check(new ConnectionProfileStore(boundedPath).Profiles.Count == ConnectionProfileStore.MaxProfiles, "The disk representation must also be bounded.");
            Check(!bounded.Save(Make("Discarded stale", key: Hash("stale"), saved: now.AddDays(-1))), "Discarded stale saves must not falsely report success.");

            var duplicates = new List<ConnectionProfile?> { Make("Old", saved: now.AddMinutes(-2)), null, Make("Latest", saved: now.AddMinutes(-1)) };
            var badLoaded = Make();
            badLoaded.Mode = (GameFilterMode)900;
            duplicates.Add(badLoaded);
            duplicates.Add(Make("Separate goal", ConnectionGoal.Discord));
            var loadPath = Path.Combine(root, "untrusted.json");
            File.WriteAllText(loadPath, JsonSerializer.Serialize(new { SchemaVersion = 1, Profiles = duplicates }));
            var normalized = new ConnectionProfileStore(loadPath);
            Check(normalized.Profiles.Count == 2 && normalized.Profiles.Single(item => item.Goal == ConnectionGoal.All).Name == "Latest",
                "Loading must reject invalid/null entries and select the newest duplicate.");
            var untrustedBytes = File.ReadAllText(loadPath);
            _ = new ConnectionProfileStore(loadPath);
            Check(File.ReadAllText(loadPath) == untrustedBytes, "Load must not automatically rewrite normalization into user data.");
            var tooMany = Enumerable.Range(0, 35).Select(i => Make("Loaded " + i, key: Hash("loaded-" + i), saved: now.AddSeconds(i))).ToArray();
            File.WriteAllText(loadPath, JsonSerializer.Serialize(new { SchemaVersion = 1, Profiles = tooMany }));
            Check(new ConnectionProfileStore(loadPath).Profiles.Count == 20, "Untrusted files must be bounded when loaded.");
            foreach (var json in new[] { "{malformed", "null", "[]", "{\"SchemaVersion\":900,\"Profiles\":[]}", "{\"SchemaVersion\":1,\"Profiles\":null}" })
            {
                File.WriteAllText(loadPath, json);
                Check(new ConnectionProfileStore(loadPath).Profiles.Count == 0 && File.ReadAllText(loadPath) == json,
                    "Malformed or unsupported JSON must fail closed without rewriting.");
            }
            File.WriteAllText(loadPath, new string(' ', ConnectionProfileStore.MaxFileBytes + 1));
            Check(new ConnectionProfileStore(loadPath).Profiles.Count == 0, "Oversized files must be rejected before JSON allocation.");

            // Windows sharing denial deterministically fails replacement without ACL changes.
            var committed = File.ReadAllText(path);
            var beforeName = store.Profiles.Single().Name;
            using (var denied = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check(!store.Save(Make("Must not commit")), "Atomic replacement must report a locked destination failure.");
                Check(store.Profiles.Single().Name == beforeName, "Failed saves must preserve the committed in-memory snapshot.");
                Check(!store.Remove(store.Profiles.Single()) && store.Profiles.Count == 1, "Failed removals must preserve memory.");
            }
            Check(File.ReadAllText(path) == committed, "Failed writes must preserve the previous file bytes.");
            Check(!Directory.EnumerateFiles(root, "*.tmp-*", SearchOption.AllDirectories).Any(), "Failed writes must clean their own adjacent temporary files.");

            var blockedParent = Path.Combine(root, "parent-is-file");
            File.WriteAllText(blockedParent, "sentinel");
            var blocked = new ConnectionProfileStore(Path.Combine(blockedParent, "profiles.json"));
            Check(!blocked.Save(Make()) && blocked.Profiles.Count == 0 && File.ReadAllText(blockedParent) == "sentinel",
                "An invalid destination directory must not commit memory or overwrite its blocking file.");
        }
        finally
        {
            // Validate the exact generated root before recursive test cleanup.
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(root)), tempParent, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).StartsWith("zapret-profile-smoke-", StringComparison.Ordinal))
                Directory.Delete(root, recursive: true);
        }
        return checks;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
