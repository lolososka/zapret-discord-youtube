using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZapretGui.Core;

public sealed record ForkRelease(
    string Tag,
    string GuiVersion,
    string UpstreamVersion,
    string PageUrl,
    string ZipName,
    string ZipUrl,
    long ZipSize,
    string ZipDigest,
    string ChecksumsUrl);

public sealed record UpdatePreparation(
    bool Success,
    string Message,
    ForkRelease? Release = null,
    string? PlanPath = null,
    string? PlanSha256 = null);

/// <summary>
/// Проверяет и подготавливает обновления community-форка. В отличие от диагностических
/// сетевых проб этот клиент всегда использует штатную проверку TLS.
/// </summary>
public static partial class ForkUpdateService
{
    public const string ReleasesUrl =
        "https://github.com/lolososka/zapret-discord-youtube/releases/latest";

    private const string LatestReleaseApi =
        "https://api.github.com/repos/lolososka/zapret-discord-youtube/releases/latest";

    private const long MaxZipBytes = 512L * 1024 * 1024;
    private const int MaxChecksumsBytes = 128 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Zapret-Control-Center/" + UpdateService.LocalVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd(
            "application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    public static async Task<(bool ok, ForkRelease? release, bool updateAvailable)>
        CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var release = await GetLatestReleaseAsync(ct).ConfigureAwait(false);
            return (
                true,
                release,
                IsUpdateAvailable(release));
        }
        catch
        {
            return (false, null, false);
        }
    }

    public static async Task<ForkRelease> GetLatestReleaseAsync(
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };

        using var response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(cts.Token)
            .ConfigureAwait(false);
        using var document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cts.Token)
            .ConfigureAwait(false);

        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            throw new InvalidDataException("Последний релиз ещё является черновиком.");
        if (root.TryGetProperty("prerelease", out var prerelease) &&
            prerelease.GetBoolean())
            throw new InvalidDataException("Предварительный релиз нельзя устанавливать автоматически.");

        var tag = RequiredString(root, "tag_name");
        var pageUrl = RequiredString(root, "html_url");
        var match = ReleaseTagRegex().Match(tag);
        if (!match.Success)
            throw new InvalidDataException("Неожиданный формат тега релиза: " + tag);

        var guiVersion = match.Groups["gui"].Value;
        var upstreamVersion = match.Groups["upstream"].Value;
        if (!VersionPolicy.TryParse(guiVersion, out _))
        {
            throw new InvalidDataException(
                "Версия GUI в релизе должна иметь числовой формат x.y.z: " +
                guiVersion);
        }
        var expectedZip =
            $"zapret-control-center-{guiVersion}-flowseal-{upstreamVersion}-win-x64.zip";

        JsonElement? zipAsset = null;
        JsonElement? checksumsAsset = null;
        if (root.TryGetProperty("assets", out var assets) &&
            assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = OptionalString(asset, "name");
                if (string.Equals(name, expectedZip, StringComparison.Ordinal))
                    zipAsset = asset.Clone();
                else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.Ordinal))
                    checksumsAsset = asset.Clone();
            }
        }

        if (zipAsset is null || checksumsAsset is null)
            throw new InvalidDataException(
                "В релизе отсутствует portable ZIP или SHA256SUMS.txt.");

        var zip = zipAsset.Value;
        var checksum = checksumsAsset.Value;
        var zipSize = zip.TryGetProperty("size", out var sizeElement)
            ? sizeElement.GetInt64()
            : 0;
        if (zipSize <= 0 || zipSize > MaxZipBytes)
            throw new InvalidDataException("Размер ZIP в релизе выглядит некорректно.");

        var digest = OptionalString(zip, "digest");
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
            digest.Length != 71)
            throw new InvalidDataException("GitHub не вернул SHA-256 digest portable ZIP.");

        return new ForkRelease(
            tag,
            guiVersion,
            upstreamVersion,
            pageUrl,
            expectedZip,
            RequiredString(zip, "browser_download_url"),
            zipSize,
            digest[7..].ToLowerInvariant(),
            RequiredString(checksum, "browser_download_url"));
    }

    public static async Task<UpdatePreparation> PrepareLatestAsync(
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        string? pendingRoot = null;
        try
        {
            var release = await GetLatestReleaseAsync(ct).ConfigureAwait(false);
            if (!IsUpdateAvailable(release))
            {
                return new UpdatePreparation(
                    false,
                    $"Уже установлена последняя версия {UpdateService.LocalVersion}.",
                    release);
            }

            if (Directory.Exists(Path.Combine(AppPaths.Root, ".git")))
            {
                return new UpdatePreparation(
                    false,
                    "Автообновление отключено для исходного git-репозитория. Обновите его через git.",
                    release);
            }

            SecureUpdateDirectory.EnsureRoot(AppPaths.UpdatesDir);
            pendingRoot = SecureUpdateDirectory.CreateUniqueChild(
                AppPaths.UpdatesDir,
                "pending-");

            var checksumsText = await DownloadSmallTextAsync(
                release.ChecksumsUrl,
                MaxChecksumsBytes,
                ct).ConfigureAwait(false);
            var checksumHash = FindChecksum(checksumsText, release.ZipName);
            if (checksumHash is null)
                throw new InvalidDataException(
                    $"SHA256SUMS.txt не содержит {release.ZipName}.");
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(checksumHash),
                    Convert.FromHexString(release.ZipDigest)))
            {
                throw new InvalidDataException(
                    "SHA-256 из GitHub API не совпал с SHA256SUMS.txt.");
            }

            var zipPath = Path.Combine(pendingRoot, release.ZipName);
            var actualHash = await DownloadFileAsync(
                release.ZipUrl,
                zipPath,
                release.ZipSize,
                progress,
                ct).ConfigureAwait(false);

            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(checksumHash)))
            {
                throw new InvalidDataException(
                    "Контрольная сумма загруженного ZIP не совпала.");
            }

            var extractedRoot = Path.Combine(pendingRoot, "extracted");
            var packageRoot = await ExtractVerifiedPackageAsync(
                zipPath,
                extractedRoot,
                release,
                ct).ConfigureAwait(false);

            var helperSource = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(helperSource) ||
                !File.Exists(helperSource))
                throw new InvalidOperationException("Не найден исполняемый файл GUI.");

            var helperPath = Path.Combine(
                pendingRoot,
                "ZapretGUI.UpdateHelper.exe");
            File.Copy(helperSource, helperPath, overwrite: true);

            var targetParent = Directory.GetParent(AppPaths.Root)?.FullName ??
                               throw new InvalidOperationException(
                                   "Не удалось определить родительскую папку установки.");
            var backupRoot = Path.Combine(
                targetParent,
                Path.GetFileName(AppPaths.Root) +
                ".backup-" +
                release.GuiVersion +
                "-" +
                DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            var markerPath = Path.Combine(pendingRoot, "healthy.marker");
            var plan = new PortableUpdatePlan
            {
                SchemaVersion = 1,
                Tag = release.Tag,
                GuiVersion = release.GuiVersion,
                SourceRoot = packageRoot,
                TargetRoot = AppPaths.Root,
                BackupRoot = backupRoot,
                HelperPath = helperPath,
                MarkerPath = markerPath,
                PackageManifestSha256 =
                    await PortableUpdateInstaller.ComputeFileSha256Async(
                        Path.Combine(
                            packageRoot,
                            PortableUpdateInstaller.ManifestFileName),
                        ct).ConfigureAwait(false),
                OriginalProcessId = Environment.ProcessId,
                WasServiceRunning =
                    await ZapretServiceManager.QueryAsync().ConfigureAwait(false) ==
                    ServiceState.Running
            };

            var planPath = Path.Combine(pendingRoot, "update-plan.json");
            await PortableUpdateInstaller.WritePlanAsync(planPath, plan, ct)
                .ConfigureAwait(false);
            var planSha256 =
                await PortableUpdateInstaller.ComputeFileSha256Async(
                    planPath,
                    ct).ConfigureAwait(false);

            return new UpdatePreparation(
                true,
                $"Версия {release.GuiVersion} загружена и проверена.",
                release,
                planPath,
                planSha256);
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(pendingRoot);
            return new UpdatePreparation(false, "Обновление отменено.");
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(pendingRoot);
            return new UpdatePreparation(
                false,
                "Безопасная подготовка обновления не удалась: " + ex.Message);
        }
    }

    private static async Task<string> DownloadSmallTextAsync(
        string url,
        int maximumBytes,
        CancellationToken ct)
    {
        using var response = await Http.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long length &&
            length > maximumBytes)
            throw new InvalidDataException("Служебный файл релиза слишком большой.");

        await using var source = await response.Content
            .ReadAsStreamAsync(ct)
            .ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > maximumBytes)
                    throw new InvalidDataException("Служебный файл релиза слишком большой.");
                await memory.WriteAsync(buffer.AsMemory(0, read), ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static async Task<string> DownloadFileAsync(
        string url,
        string destination,
        long expectedSize,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        using var response = await Http.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength != expectedSize)
            throw new InvalidDataException("Размер ZIP изменился во время загрузки.");

        var temporary = destination + ".download";
        await using var source = await response.Content
            .ReadAsStreamAsync(ct)
            .ConfigureAwait(false);
        var output = new FileStream(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long received = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > expectedSize || received > MaxZipBytes)
                    throw new InvalidDataException("ZIP превысил ожидаемый размер.");

                await output.WriteAsync(buffer.AsMemory(0, read), ct)
                    .ConfigureAwait(false);
                sha.AppendData(buffer, 0, read);
                progress?.Report(expectedSize == 0
                    ? 0
                    : Math.Clamp((double)received / expectedSize, 0, 1));
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            output.Close();
            TryDeleteFile(temporary);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            await output.DisposeAsync().ConfigureAwait(false);
        }

        if (received != expectedSize)
        {
            TryDeleteFile(temporary);
            throw new InvalidDataException("Загружен неполный ZIP.");
        }

        File.Move(temporary, destination);
        progress?.Report(1);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> ExtractVerifiedPackageAsync(
        string zipPath,
        string destination,
        ForkRelease release,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var destinationFull = Path.GetFullPath(destination)
            .TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        long extractedBytes = 0;
        var entryCount = 0;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (++entryCount > 10_000)
                throw new InvalidDataException("В ZIP слишком много файлов.");
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("Символические ссылки в ZIP запрещены.");

            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(destinationFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ZIP содержит небезопасный путь.");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            extractedBytes += entry.Length;
            if (extractedBytes > 1024L * 1024 * 1024)
                throw new InvalidDataException("Распакованный релиз слишком большой.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
        }

        var roots = Directory.GetDirectories(destination);
        if (roots.Length != 1 ||
            Directory.GetFiles(destination, "*", SearchOption.TopDirectoryOnly).Length != 0)
            throw new InvalidDataException("ZIP должен содержать одну корневую папку.");

        var packageRoot = roots[0];
        var manifestPath = Path.Combine(packageRoot, PortableUpdateInstaller.ManifestFileName);
        var manifest = await PortableUpdateInstaller.ReadManifestAsync(
            manifestPath,
            ct).ConfigureAwait(false);
        await PortableUpdateInstaller.ValidatePackageAsync(
            packageRoot,
            manifest,
            release.GuiVersion,
            release.UpstreamVersion,
            release.Tag,
            ct).ConfigureAwait(false);
        return packageRoot;
    }

    private static string? FindChecksum(string text, string fileName)
    {
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            var split = line.IndexOf("  ", StringComparison.Ordinal);
            if (split != 64 || line.Length <= split + 2)
                continue;
            var hash = line[..64];
            var name = line[(split + 2)..].Trim();
            if (!string.Equals(name, fileName, StringComparison.Ordinal))
                continue;
            if (hash.All(Uri.IsHexDigit))
                return hash.ToLowerInvariant();
        }
        return null;
    }

    internal static bool IsNewer(string remote, string local)
        => VersionPolicy.IsNewer(remote, local);

    internal static bool IsUpdateAvailable(ForkRelease release)
    {
        var installedTag = ReadInstalledReleaseTag();
        return VersionPolicy.DecidePortableUpdate(
            release.GuiVersion,
            UpdateService.LocalVersion,
            release.Tag,
            installedTag).IsAvailable;
    }

    private static string? ReadInstalledReleaseTag()
    {
        try
        {
            var manifestPath = Path.Combine(
                AppPaths.Root,
                PortableUpdateInstaller.ManifestFileName);
            if (File.Exists(manifestPath))
            {
                using var document = JsonDocument.Parse(
                    File.ReadAllText(manifestPath, Encoding.UTF8));
                var tag = OptionalString(document.RootElement, "Tag");
                if (!string.IsNullOrWhiteSpace(tag))
                    return tag;
            }
        }
        catch
        {
            // Старые portable-сборки ещё не содержали манифест.
        }

        try
        {
            var buildInfo = Path.Combine(AppPaths.Root, "BUILD_INFO.txt");
            if (!File.Exists(buildInfo))
                return null;

            var values = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(buildInfo, Encoding.UTF8))
            {
                var split = line.IndexOf(':');
                if (split <= 0)
                    continue;
                values[line[..split].Trim()] = line[(split + 1)..].Trim();
            }

            if (!values.TryGetValue(
                    "Zapret Control Center version",
                    out var gui) ||
                !values.TryGetValue("Flowseal version", out var upstream) ||
                !values.TryGetValue(
                    "Flowseal upstream commit",
                    out var commit) ||
                commit.Length < 12)
                return null;
            return $"gui-v{gui}-flowseal-v{upstream}-u{commit[..12]}";
        }
        catch
        {
            return null;
        }
    }

    private static string RequiredString(JsonElement element, string property)
    {
        var value = OptionalString(element, property);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"В ответе GitHub отсутствует {property}.");
        return value;
    }

    private static string OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Следующая проверка обновления сможет удалить старую staging-папку.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Временный файл останется только до следующей очистки.
        }
    }

    [GeneratedRegex(
        "^gui-v(?<gui>[0-9A-Za-z][0-9A-Za-z._+-]{0,63})-flowseal-v(?<upstream>[0-9A-Za-z][0-9A-Za-z._+-]{0,63})-u[0-9a-fA-F]{12}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseTagRegex();

}
