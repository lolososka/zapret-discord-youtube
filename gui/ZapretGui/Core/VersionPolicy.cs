using System.Globalization;
using System.Reflection;

namespace ZapretGui.Core;

public enum PortableUpdateReason
{
    None,
    GuiUpgrade,
    PortableMigration,
    ReleaseChanged
}

public readonly record struct PortableUpdateDecision(PortableUpdateReason Reason)
{
    public bool IsAvailable => Reason != PortableUpdateReason.None;
    public bool IsMigration => Reason == PortableUpdateReason.PortableMigration;
}

/// <summary>
/// Единый строгий контракт версии GUI: ровно три числовых компонента x.y.z
/// без префиксов, суффиксов, пробелов и ведущих нулей.
/// </summary>
public static class VersionPolicy
{
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrEmpty(value))
            return false;

        var parts = value.Split('.');
        if (parts.Length != 3)
            return false;

        Span<int> components = stackalloc int[3];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0 ||
                (part.Length > 1 && part[0] == '0') ||
                !part.All(static character => character is >= '0' and <= '9') ||
                !int.TryParse(
                    part,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out components[index]))
            {
                return false;
            }
        }

        version = new Version(components[0], components[1], components[2]);
        return true;
    }

    public static Version ParseRequired(string? value, string label)
    {
        if (TryParse(value, out var version))
            return version;

        throw new FormatException(
            $"{label} должна иметь строгий числовой формат x.y.z: {value ?? "<null>"}");
    }

    public static bool IsNewer(string candidate, string current) =>
        ParseRequired(candidate, "Версия обновления") >
        ParseRequired(current, "Локальная версия");

    public static string ProductVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var assemblyVersion = assembly.GetName().Version ??
            throw new InvalidOperationException(
                "Сборка не содержит версию продукта.");
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}");
        ParseRequired(value, "Версия сборки");
        return value;
    }

    public static PortableUpdateDecision DecidePortableUpdate(
        string remoteGuiVersion,
        string localGuiVersion,
        string remoteReleaseTag,
        string? installedReleaseTag)
    {
        var remote = ParseRequired(remoteGuiVersion, "Удалённая версия GUI");
        var local = ParseRequired(localGuiVersion, "Локальная версия GUI");
        if (string.IsNullOrWhiteSpace(remoteReleaseTag))
            throw new ArgumentException(
                "Тег portable-релиза не указан.",
                nameof(remoteReleaseTag));

        if (remote > local)
            return new PortableUpdateDecision(PortableUpdateReason.GuiUpgrade);
        if (remote < local)
            return new PortableUpdateDecision(PortableUpdateReason.None);

        if (string.IsNullOrWhiteSpace(installedReleaseTag))
        {
            return new PortableUpdateDecision(
                PortableUpdateReason.PortableMigration);
        }

        return string.Equals(
            installedReleaseTag,
            remoteReleaseTag,
            StringComparison.OrdinalIgnoreCase)
            ? new PortableUpdateDecision(PortableUpdateReason.None)
            : new PortableUpdateDecision(PortableUpdateReason.ReleaseChanged);
    }
}
