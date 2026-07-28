using System.Security.AccessControl;
using System.Security.Principal;

namespace ZapretGui.Core;

/// <summary>
/// Keeps update payloads in an administrator-only directory on the same volume as
/// the portable installation.  A medium-integrity process must not be able to
/// replace a package after its hashes have been checked.
/// </summary>
internal static class SecureUpdateDirectory
{
    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);

    private const InheritanceFlags ChildInheritance =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    public static void EnsureRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
            throw new IOException(
                "Путь временных обновлений занят файлом: " + fullPath);

        var info = new DirectoryInfo(fullPath);
        if (!info.Exists)
        {
            info.Create(BuildAdministratorOnlySecurity());
        }
        else
        {
            RejectReparsePoint(info);
            info.SetAccessControl(BuildAdministratorOnlySecurity());
        }

        Validate(fullPath);
        try
        {
            info.Attributes |= FileAttributes.Hidden;
        }
        catch
        {
            // Скрытый атрибут косметический и не влияет на безопасность.
        }
    }

    public static string CreateUniqueChild(
        string parent,
        string prefix)
    {
        Validate(parent);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var path = Path.Combine(
                parent,
                prefix + Guid.NewGuid().ToString("N"));
            try
            {
                new DirectoryInfo(path)
                    .Create(BuildAdministratorOnlySecurity());
                Validate(path);
                return path;
            }
            catch (IOException) when (
                Directory.Exists(path) ||
                File.Exists(path))
            {
                // Практически невозможно, но новый GUID безопаснее перезаписи.
            }
        }

        throw new IOException(
            "Не удалось создать уникальную защищённую папку обновления.");
    }

    public static void Validate(string path)
    {
        var info = new DirectoryInfo(Path.GetFullPath(path));
        if (!info.Exists)
            throw new DirectoryNotFoundException(
                "Защищённая папка обновления не найдена: " + info.FullName);
        RejectReparsePoint(info);

        var security = info.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException(
                "Папка обновления наследует небезопасные права доступа.");

        var owner = security.GetOwner(typeof(SecurityIdentifier))
            as SecurityIdentifier;
        if (owner is null ||
            (!owner.Equals(Administrators) &&
             !owner.Equals(LocalSystem)))
        {
            throw new UnauthorizedAccessException(
                "Владелец папки обновления не подтверждён.");
        }

        var administratorsAllowed = false;
        var systemAllowed = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     targetType: typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;

            var sid = (SecurityIdentifier)rule.IdentityReference;
            var grantsWrite =
                (rule.FileSystemRights &
                 (FileSystemRights.Write |
                  FileSystemRights.Modify |
                  FileSystemRights.FullControl |
                  FileSystemRights.ChangePermissions |
                  FileSystemRights.TakeOwnership)) != 0;
            if (sid.Equals(Administrators))
            {
                administratorsAllowed |=
                    (rule.FileSystemRights & FileSystemRights.FullControl) ==
                    FileSystemRights.FullControl;
                continue;
            }

            if (sid.Equals(LocalSystem))
            {
                systemAllowed |=
                    (rule.FileSystemRights & FileSystemRights.FullControl) ==
                    FileSystemRights.FullControl;
                continue;
            }

            if (grantsWrite)
                throw new UnauthorizedAccessException(
                    "Папка обновления доступна для записи постороннему SID.");
        }

        if (!administratorsAllowed || !systemAllowed)
            throw new UnauthorizedAccessException(
                "Права папки обновления не дают полный доступ администратору и SYSTEM.");
    }

    public static string CapturePortableRootSecurity(string root)
    {
        var info = new DirectoryInfo(root);
        RejectReparsePoint(info);
        return info.GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner)
            .GetSecurityDescriptorSddlForm(
                AccessControlSections.Access | AccessControlSections.Owner);
    }

    public static void RestorePortableTreeSecurity(
        string root,
        string rootSecuritySddl)
    {
        var rootInfo = new DirectoryInfo(root);
        RejectReparsePoint(rootInfo);

        var rootSecurity = new DirectorySecurity();
        rootSecurity.SetSecurityDescriptorSddlForm(
            rootSecuritySddl,
            AccessControlSections.Access | AccessControlSections.Owner);
        rootInfo.SetAccessControl(rootSecurity);

        foreach (var directory in Directory.EnumerateDirectories(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            var info = new DirectoryInfo(directory);
            RejectReparsePoint(info);
            var security = info.GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(
                isProtected: false,
                preserveInheritance: false);
            info.SetAccessControl(security);
        }

        foreach (var file in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(
                    "Reparse point запрещён в установленном пакете: " +
                    info.FullName);
            var security = info.GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(
                isProtected: false,
                preserveInheritance: false);
            info.SetAccessControl(security);
        }
    }

    private static DirectorySecurity BuildAdministratorOnlySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.SetOwner(Administrators);
        security.AddAccessRule(new FileSystemAccessRule(
            Administrators,
            FileSystemRights.FullControl,
            ChildInheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            LocalSystem,
            FileSystemRights.FullControl,
            ChildInheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    private static void RejectReparsePoint(FileSystemInfo info)
    {
        info.Refresh();
        if (!info.Exists)
            throw new DirectoryNotFoundException(info.FullName);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException(
                "Reparse point запрещён для обновления: " + info.FullName);
    }
}
