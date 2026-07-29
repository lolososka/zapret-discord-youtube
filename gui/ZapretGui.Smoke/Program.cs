using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZapretGui.Core;

namespace ZapretGui.Smoke;

internal static class Program
{
    private const long MaxZipBytes = 512L * 1024 * 1024;
    private const long MaxExtractedBytes = 1024L * 1024 * 1024;
    private const int MaxArchiveEntries = 10_000;

    private static int _checks;
    private static readonly string ProductVersion =
        VersionPolicy.ProductVersion(typeof(UpdateService).Assembly);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--package", var zipPath, var guiVersion, var upstreamVersion, var tag])
            {
                await ValidateReleasePackageAsync(
                    zipPath,
                    guiVersion,
                    upstreamVersion,
                    tag);
            }
            else if (args.Length == 0)
            {
                RunVersionPolicySmoke();
                RunStrategyParserSmoke();
                await RunManifestSmokeAsync();
                await RunSupportBundleSmokeAsync();
            }
            else
            {
                throw new ArgumentException(
                    "Usage: ZapretGui.Smoke [--package <zip> <gui> <upstream> <tag>]");
            }

            Console.WriteLine($"Smoke checks passed: {_checks}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Smoke check failed:");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void RunVersionPolicySmoke()
    {
        Check(
            UpdateService.LocalVersion == ProductVersion,
            "LocalVersion must come from the product assembly.");
        Check(
            VersionPolicy.ProductVersion(typeof(UpdateService).Assembly) ==
            UpdateService.LocalVersion,
            "Assembly and updater versions must match.");
        Check(
            VersionPolicy.TryParse("2.3.4", out var parsed) &&
            parsed == new Version(2, 3, 4),
            "A canonical x.y.z version must parse.");

        foreach (var invalid in new[]
                 {
                     "",
                     "2.3",
                     "2.3.4.0",
                     "v2.3.4",
                     "2.3.4-beta",
                     "02.3.4",
                     "2.3.4 ",
                     "2147483648.0.0"
                 })
        {
            Check(
                !VersionPolicy.TryParse(invalid, out _),
                $"Invalid version must be rejected: '{invalid}'.");
        }

        Check(
            VersionPolicy.IsNewer("2.3.4", "2.3.3"),
            "A greater GUI version must be newer.");
        Check(
            !VersionPolicy.IsNewer("2.3.4", "2.3.4"),
            "Equal GUI versions must not be newer.");
        ExpectThrows<FormatException>(
            () => VersionPolicy.IsNewer("2.3.4-preview", "2.3.3"),
            "Invalid remote GUI version must fail closed.");

        var remoteTag = "gui-v2.3.4-flowseal-v1.10.0-u0123456789ab";
        Check(
            VersionPolicy.DecidePortableUpdate(
                "2.3.4",
                "2.3.3",
                remoteTag,
                null).Reason == PortableUpdateReason.GuiUpgrade,
            "A greater GUI version must offer an update.");
        Check(
            VersionPolicy.DecidePortableUpdate(
                "2.3.3",
                "2.3.4",
                remoteTag,
                null).Reason == PortableUpdateReason.None,
            "An older remote GUI must never replace a newer local GUI.");
        Check(
            VersionPolicy.DecidePortableUpdate(
                "2.3.4",
                "2.3.4",
                remoteTag,
                null).Reason == PortableUpdateReason.PortableMigration,
            "An unknown installed tag must offer portable migration.");
        Check(
            VersionPolicy.DecidePortableUpdate(
                "2.3.4",
                "2.3.4",
                remoteTag,
                "gui-v2.3.4-flowseal-v1.9.0-uabcdef012345").Reason ==
            PortableUpdateReason.ReleaseChanged,
            "A changed Flowseal release must offer a package refresh.");
        Check(
            VersionPolicy.DecidePortableUpdate(
                "2.3.4",
                "2.3.4",
                remoteTag,
                remoteTag.ToUpperInvariant()).Reason ==
            PortableUpdateReason.None,
            "The same installed release tag must not offer an update.");
    }

    private static void RunStrategyParserSmoke()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "general (ALT2).bat");
            File.WriteAllText(
                path,
                """
                @echo off
                "%BIN%winws.exe" --wf-tcp=80,443 ^
                  --filter-tcp=443 --hostlist="%LISTS%list-general.txt" ^
                  --wf-udp=%GameFilterUDP% --fake="%BIN%quic.bin" ^
                  --root="%~dp0data" --marker=^!
                """,
                new UTF8Encoding(false));

            var strategy = StrategyParser.Load(path);
            Check(strategy is not null, "A valid strategy must parse.");
            Check(
                strategy!.Variant == "ALT 2",
                "ALT2 must receive its natural display name.");
            Check(
                strategy.RawCommandLine.Contains(
                    "--marker=!",
                    StringComparison.Ordinal) &&
                !strategy.RawCommandLine.Contains(
                    "--marker=^!",
                    StringComparison.Ordinal),
                "The cmd ^! escape must be decoded.");

            var arguments = StrategyParser.BuildArguments(
                strategy,
                GameFilterMode.Tcp);
            Check(
                arguments.Contains("--wf-udp=12", StringComparison.Ordinal),
                "GameFilter TCP mode must disable the UDP range.");
            Check(
                arguments.Contains(
                    AppPaths.Lists + "list-general.txt",
                    StringComparison.OrdinalIgnoreCase),
                "%LISTS% must expand.");
            Check(
                arguments.Contains(
                    AppPaths.Bin + "quic.bin",
                    StringComparison.OrdinalIgnoreCase),
                "%BIN% must expand.");
            Check(
                arguments.Contains(
                    AppPaths.Root + @"\data",
                    StringComparison.OrdinalIgnoreCase),
                "%~dp0 must expand.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunManifestSmokeAsync()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var packageRoot = Path.Combine(
                temporaryRoot,
                $"zapret-control-center-{ProductVersion}-flowseal-1.10.0-win-x64");
            Directory.CreateDirectory(Path.Combine(packageRoot, "bin"));
            File.WriteAllText(
                Path.Combine(packageRoot, "ZapretGUI.exe"),
                "gui",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(packageRoot, "bin", "winws.exe"),
                "winws",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(packageRoot, "service.bat"),
                "@echo off",
                Encoding.UTF8);

            var manifest = CreateManifest(packageRoot);
            var manifestPath = Path.Combine(
                packageRoot,
                PortableUpdateInstaller.ManifestFileName);
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(
                    manifest,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            var loaded = await PortableUpdateInstaller.ReadManifestAsync(
                manifestPath);
            await PortableUpdateInstaller.ValidatePackageAsync(
                packageRoot,
                loaded,
                ProductVersion,
                "1.10.0",
                loaded.Tag);
            Check(true, "A valid package manifest must pass.");

            var traversal = CloneManifest(manifest);
            traversal.Files[0].Path = @"..\ZapretGUI.exe";
            await ExpectThrowsAsync<InvalidDataException>(
                () => PortableUpdateInstaller.ValidatePackageAsync(
                    packageRoot,
                    traversal,
                    ProductVersion,
                    "1.10.0",
                    traversal.Tag),
                "Manifest traversal paths must be rejected.");

            var duplicate = CloneManifest(manifest);
            duplicate.Files.Add(new PortablePackageFile
            {
                Path = "SERVICE.BAT",
                Size = duplicate.Files[^1].Size,
                Sha256 = duplicate.Files[^1].Sha256
            });
            await ExpectThrowsAsync<InvalidDataException>(
                () => PortableUpdateInstaller.ValidatePackageAsync(
                    packageRoot,
                    duplicate,
                    ProductVersion,
                    "1.10.0",
                    duplicate.Tag),
                "Case-insensitive duplicate paths must be rejected.");

            var protectedPath = CloneManifest(manifest);
            protectedPath.Files.Add(new PortablePackageFile
            {
                Path = "lists/list-general-user.txt",
                Size = 0,
                Sha256 = new string('0', 64)
            });
            await ExpectThrowsAsync<InvalidDataException>(
                () => PortableUpdateInstaller.ValidatePackageAsync(
                    packageRoot,
                    protectedPath,
                    ProductVersion,
                    "1.10.0",
                    protectedPath.Tag),
                "User-owned files must be rejected.");

            var invalidVersion = CloneManifest(manifest);
            invalidVersion.GuiVersion = ProductVersion + "-preview";
            await ExpectThrowsAsync<InvalidDataException>(
                () => PortableUpdateInstaller.ValidatePackageAsync(
                    packageRoot,
                    invalidVersion,
                    ProductVersion + "-preview",
                    "1.10.0",
                    invalidVersion.Tag),
                "Invalid manifest GUI versions must be rejected.");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static async Task ValidateReleasePackageAsync(
        string zipPath,
        string guiVersion,
        string upstreamVersion,
        string tag)
    {
        Check(File.Exists(zipPath), "The release ZIP must exist.");
        Check(
            new FileInfo(zipPath).Length <= MaxZipBytes,
            "The release ZIP must fit the updater download limit.");
        Check(
            VersionPolicy.TryParse(guiVersion, out _),
            "Release metadata must contain a strict GUI version.");

        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                Check(
                    archive.Entries.Count <= MaxArchiveEntries,
                    "The release ZIP must fit the updater entry limit.");
                long extractedBytes = 0;
                foreach (var entry in archive.Entries)
                {
                    extractedBytes = checked(extractedBytes + entry.Length);
                    Check(
                        extractedBytes <= MaxExtractedBytes,
                        "The release ZIP must fit the updater extraction limit.");
                }
            }

            ZipFile.ExtractToDirectory(zipPath, temporaryRoot);
            var roots = Directory.GetDirectories(temporaryRoot);
            Check(
                roots.Length == 1 &&
                Directory.GetFiles(
                    temporaryRoot,
                    "*",
                    SearchOption.TopDirectoryOnly).Length == 0,
                "The release ZIP must contain exactly one root directory.");

            var packageRoot = roots[0];
            var manifest = await PortableUpdateInstaller.ReadManifestAsync(
                Path.Combine(
                    packageRoot,
                    PortableUpdateInstaller.ManifestFileName));
            await PortableUpdateInstaller.ValidatePackageAsync(
                packageRoot,
                manifest,
                guiVersion,
                upstreamVersion,
                tag);
            Check(true, "The packaged release manifest must match the ZIP.");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static async Task RunSupportBundleSmokeAsync()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        var bundlePath = Path.Combine(temporaryRoot, "support.zip");
        var marker = Guid.NewGuid().ToString("N");
        var privateListName = $"private-{marker}.txt";
        var privateFakeName = $"private-{marker}.bin";
        var privateUtilityName = $"private-{marker}.enabled";
        var privateStrategyName = $"general (Private {marker})";
        var privateStrategyFileName = privateStrategyName + ".bat";
        var privateListPath = Path.Combine(AppPaths.Lists, privateListName);
        var privateFakePath = Path.Combine(AppPaths.Bin, privateFakeName);
        var privateUtilityPath = Path.Combine(AppPaths.Utils, privateUtilityName);
        var manifestPath = Path.Combine(
            AppPaths.Root,
            PortableUpdateInstaller.ManifestFileName);
        byte[]? previousManifest = File.Exists(manifestPath)
            ? await File.ReadAllBytesAsync(manifestPath)
            : null;
        const string secret = "0123456789abcdef0123456789abcdef";
        const string privateListContent = "private-smoke-domain.invalid";
        const string ipv4 = "203.0.113.42";
        const string ipv6 = "2001:db8:1234::42";

        try
        {
            Directory.CreateDirectory(AppPaths.Lists);
            Directory.CreateDirectory(AppPaths.Bin);
            Directory.CreateDirectory(AppPaths.Utils);
            await File.WriteAllTextAsync(
                privateListPath,
                privateListContent,
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                privateFakePath,
                "fake",
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                privateUtilityPath,
                "enabled",
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    guiVersion = ipv4,
                    upstreamVersion = secret,
                    tag = $"gui-v{ipv4}-flowseal-v1.0.0-u{secret[..12]}",
                    upstreamCommit = secret,
                    forkCommit = secret,
                    files = new[]
                    {
                        new { path = $"bin/{privateFakeName}" }
                    }
                }),
                new UTF8Encoding(false));

            var sensitiveLine =
                $"user={Environment.UserName} path={AppPaths.Root} " +
                $"ip={ipv4} ipv6={ipv6} secret={secret} fingerprint={secret} " +
                $"strategy={privateStrategyFileName} " +
                $"url=tg://proxy?server={ipv4}&port=443&secret={secret}";
            var state = AppState.Instance;
            state.Strategies.Add(new Strategy
            {
                FileName = privateStrategyFileName,
                Name = privateStrategyName,
                DisplayName = privateStrategyName,
                Variant = $"Private {marker}"
            });
            state.Log.Add(new LogLine(
                DateTime.Now,
                sensitiveLine,
                LogLevel.Error));
            state.Diagnostics.Add(new CheckResult
            {
                Id = "support-smoke",
                Title = "Support bundle smoke",
                Status = CheckStatus.Inconclusive,
                Detail = sensitiveLine
            });

            await SupportBundleService.CreateAsync(bundlePath, state);
            Check(File.Exists(bundlePath), "The support ZIP must be created.");

            using var archive = ZipFile.OpenRead(bundlePath);
            var expectedEntries = new[]
            {
                "README.txt",
                "system.txt",
                "diagnostics.txt",
                "connectivity.txt",
                "journal.txt",
                "files.txt"
            };
            Check(
                expectedEntries.All(name =>
                    archive.GetEntry(name) is not null),
                "The support ZIP must contain every documented report.");

            var report = new StringBuilder();
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(
                    entry.Open(),
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                report.AppendLine(await reader.ReadToEndAsync());
            }

            var text = report.ToString();
            Check(
                !text.Contains(secret, StringComparison.OrdinalIgnoreCase) &&
                !text.Contains(ipv4, StringComparison.OrdinalIgnoreCase) &&
                !text.Contains(ipv6, StringComparison.OrdinalIgnoreCase) &&
                !text.Contains(AppPaths.Root, StringComparison.OrdinalIgnoreCase),
                "Secrets, IP addresses, and the installation path must be redacted.");
            if (Environment.UserName.Length >= 2)
            {
                Check(
                    !text.Contains(
                        Environment.UserName,
                        StringComparison.OrdinalIgnoreCase),
                    "The Windows user name must be redacted.");
            }

            Check(
                !text.Contains(privateListName, StringComparison.OrdinalIgnoreCase) &&
                !text.Contains(privateFakeName, StringComparison.OrdinalIgnoreCase) &&
                !text.Contains(privateUtilityName, StringComparison.OrdinalIgnoreCase) &&
                !text.Contains(privateStrategyName, StringComparison.OrdinalIgnoreCase) &&
                !text.Contains(privateListContent, StringComparison.OrdinalIgnoreCase),
                "Unmanaged strategy/file names and list contents must not enter the report.");
            Check(
                text.Contains("<secret>", StringComparison.Ordinal),
                "The support report must mark redacted secrets.");
            Check(
                text.Contains("<ip>", StringComparison.Ordinal),
                "The support report must mark redacted IP addresses.");
            Check(
                text.Contains("<local-path>", StringComparison.Ordinal),
                "The support report must mark redacted local paths.");
            Check(
                text.Contains("custom-file-", StringComparison.Ordinal),
                "The support report must anonymize unmanaged binaries.");
            Check(
                text.Contains("custom-list-", StringComparison.Ordinal),
                "The support report must anonymize unmanaged lists.");
        }
        finally
        {
            TryDelete(privateListPath);
            TryDelete(privateFakePath);
            TryDelete(privateUtilityPath);
            if (previousManifest is null)
            {
                TryDelete(manifestPath);
            }
            else
            {
                try { await File.WriteAllBytesAsync(manifestPath, previousManifest); }
                catch { /* The isolated smoke output is discarded after the run. */ }
            }
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static PortablePackageManifest CreateManifest(string packageRoot)
    {
        var files = Directory
            .EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new PortablePackageFile
            {
                Path = Path.GetRelativePath(packageRoot, path),
                Size = new FileInfo(path).Length,
                Sha256 = Sha256(path)
            })
            .ToList();

        return new PortablePackageManifest
        {
            SchemaVersion = 1,
            Tag = $"gui-v{ProductVersion}-flowseal-v1.10.0-u0123456789ab",
            GuiVersion = ProductVersion,
            UpstreamVersion = "1.10.0",
            UpstreamCommit = new string('a', 40),
            ForkCommit = new string('b', 40),
            PackageRoot = Path.GetFileName(packageRoot),
            Files = files
        };
    }

    private static PortablePackageManifest CloneManifest(
        PortablePackageManifest source) =>
        new()
        {
            SchemaVersion = source.SchemaVersion,
            Tag = source.Tag,
            GuiVersion = source.GuiVersion,
            UpstreamVersion = source.UpstreamVersion,
            UpstreamCommit = source.UpstreamCommit,
            ForkCommit = source.ForkCommit,
            PackageRoot = source.PackageRoot,
            Files = source.Files
                .Select(static file => new PortablePackageFile
                {
                    Path = file.Path,
                    Size = file.Size,
                    Sha256 = file.Sha256
                })
                .ToList()
        };

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "ZapretGui.Smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The smoke process is about to exit; report assertions are more important.
        }
    }

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void ExpectThrows<TException>(
        Action action,
        string message)
        where TException : Exception
    {
        _checks++;
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static async Task ExpectThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        _checks++;
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
