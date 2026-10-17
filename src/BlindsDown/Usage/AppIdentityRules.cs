using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using BlindsDown.Core.Usage;
using BlindsDown.Interop.SystemApis;

namespace BlindsDown.Usage;

/// <summary>
/// The rules that turn an exe path or a Store package into an <see cref="AppIdentity"/>. Shared by
/// <see cref="ProcessIdentityResolver"/> (live processes) and <see cref="ConsentStoreMonitor"/> (registry
/// entries), so one app gets the same Key and DisplayName from both and the usage tracker can de-duplicate.
/// Thread-safe. Display names are cached for a few minutes because reading version resources and
/// package manifests costs file I/O.
/// </summary>
internal static class AppIdentityRules
{
    private const string MsResourcePrefix = "ms-resource:";
    private static readonly SearchValues<char> IndirectStringSyntax = SearchValues.Create("{}?@\\");
    private static readonly TimeSpan NameLifetime = TimeSpan.FromMinutes(5);
    private static readonly ExpiringCache<string, string> DesktopNames = new(NameLifetime, 512, StringComparer.OrdinalIgnoreCase);
    private static readonly ExpiringCache<string, string> PackageNames = new(NameLifetime, 256, StringComparer.OrdinalIgnoreCase);

    public static AppIdentity Unknown(int processId) => new(
        "pid:" + processId.ToString(CultureInfo.InvariantCulture),
        string.Create(CultureInfo.InvariantCulture, $"Unknown app (PID {processId})"),
        null,
        null);

    /// <summary>A classic desktop app. Key = the full exe path, lower-case.</summary>
    public static AppIdentity ForExePath(string exePath) => new(ExeKey(exePath), DesktopDisplayName(exePath), exePath, null);

    /// <summary>
    /// A Store app. Key = the package family name, which survives app updates.
    /// <paramref name="packageFullName"/> is the exact package when known (a running process); otherwise the
    /// installed package of the family is used.
    /// </summary>
    public static AppIdentity ForPackage(string packageFamilyName, string? packageFullName = null, string? exePath = null) =>
        new(packageFamilyName, PackageDisplayName(packageFamilyName, packageFullName), exePath, packageFamilyName);

    public static string ExeKey(string exePath)
    {
        string full = exePath;
        try
        {
            full = Path.GetFullPath(exePath);
        }
        catch (ArgumentException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (PathTooLongException)
        {
        }

        return full.ToLowerInvariant();
    }

    /// <summary>
    /// FileDescription, else ProductName, else the file name without ".exe". Version info is read only from
    /// files whose content is on a local disk: for anything else (\\server\share, a mapped network drive, a
    /// OneDrive placeholder) the file name is all we use.
    /// </summary>
    public static string DesktopDisplayName(string exePath)
    {
        if (DesktopNames.TryGet(exePath, out string? cached))
        {
            return cached;
        }

        string? description = null;
        string? product = null;
        if (ImagePaths.IsSafeToOpen(exePath))
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(exePath);
                description = info.FileDescription;
                product = info.ProductName;
            }
            catch (IOException)
            {
                // Includes FileNotFoundException: the exe was deleted, or a registry entry is stale.
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        string name = ChooseDesktopName(description, product, exePath);
        DesktopNames.Set(exePath, name);
        return name;
    }

    /// <summary>The package's own display name (resolving "ms-resource:" references), else a prettified
    /// family name ("Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe" -> "Windows Sound Recorder").</summary>
    public static string PackageDisplayName(string packageFamilyName, string? packageFullName)
    {
        if (PackageNames.TryGet(packageFamilyName, out string? cached))
        {
            return cached;
        }

        var candidates = new List<string>();
        if (packageFullName is not null)
        {
            candidates.Add(packageFullName);
        }

        // Main packages first: resource packages share the family but carry only translations.
        candidates.AddRange(SystemQueries.GetPackageFullNames(packageFamilyName).OrderBy(n => IsResourcePackage(n) ? 1 : 0));

        string? name = null;
        foreach (string fullName in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            name = TryReadPackageDisplayName(fullName);
            if (name is not null)
            {
                break;
            }
        }

        name ??= PrettifyFamilyName(packageFamilyName);
        PackageNames.Set(packageFamilyName, name);
        return name;
    }

    /// <summary>SteelSeriesSonar.exe has a FileDescription of " ", hence the whitespace checks.</summary>
    internal static string ChooseDesktopName(string? fileDescription, string? productName, string exePath)
    {
        if (!string.IsNullOrWhiteSpace(fileDescription))
        {
            return fileDescription.Trim();
        }

        if (!string.IsNullOrWhiteSpace(productName))
        {
            return productName.Trim();
        }

        string fileName = Path.GetFileNameWithoutExtension(exePath);
        return string.IsNullOrWhiteSpace(fileName) ? exePath : fileName;
    }

    internal static string PrettifyFamilyName(string packageFamilyName)
    {
        // "Publisher.AppName_publisherid" -> "AppName" -> "App Name".
        string name = packageFamilyName;
        int underscore = name.IndexOf('_', StringComparison.Ordinal);
        if (underscore > 0)
        {
            name = name[..underscore];
        }

        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
        {
            name = name[(dot + 1)..];
        }

        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '-')
            {
                sb.Append(' ');
                continue;
            }

            // Break "SoundRecorder" before 'R', and "MSTeams" before 'T' (the end of an acronym).
            bool wordStart = i > 0 && char.IsUpper(c)
                && (char.IsLower(name[i - 1]) || (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1])));
            if (wordStart)
            {
                sb.Append(' ');
            }

            sb.Append(c);
        }

        string pretty = sb.ToString().Trim();
        return pretty.Length == 0 ? packageFamilyName : char.ToUpperInvariant(pretty[0]) + pretty[1..];
    }

    /// <summary>
    /// Reads Package/Properties/DisplayName from an AppxManifest.xml. DTDs are prohibited and no resolver is
    /// set, so a hostile manifest can neither expand entities nor make the parser fetch anything.
    /// </summary>
    internal static string? ReadManifestDisplayName(Stream manifest)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = 4 * 1024 * 1024,
            CloseInput = false,
        };

        try
        {
            using XmlReader reader = XmlReader.Create(manifest, settings);
            bool inProperties = false;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.Depth == 1)
                {
                    inProperties = reader.LocalName == "Properties";
                }
                else if (inProperties && reader.Depth == 2 && reader.LocalName == "DisplayName")
                {
                    string value = reader.ReadElementContentAsString().Trim();
                    return value.Length == 0 ? null : value;
                }
            }
        }
        catch (XmlException)
        {
            // Malformed, or it contained a DTD.
        }

        return null;
    }

    /// <summary>
    /// SHLoadIndirectString sources to try for a manifest value like "ms-resource:AppName". Packages write the
    /// reference in several shapes ("AppName", "Resources/AppName", "/Resources/AppName", or a full
    /// "ms-resource://Name/..." URI) and the resource may live under /Resources or at the map root, so the
    /// likely spellings are tried in order.
    /// </summary>
    internal static IReadOnlyList<string> IndirectStringCandidates(string manifestValue, string packageFullName)
    {
        if (!manifestValue.StartsWith(MsResourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        string packageName = packageFullName.Split('_')[0];
        string reference = manifestValue[MsResourcePrefix.Length..].Trim();
        string key = reference.TrimStart('/');
        if (key.Length == 0 || reference.AsSpan().ContainsAny(IndirectStringSyntax) || reference.Any(char.IsControl))
        {
            // The reference is spliced into "@{<package>?<uri>}". Braces, '?', '@' or '\' in it could change
            // which file SHLoadIndirectString opens (even a \\server\share PRI file, i.e. the network), so such
            // a value is never resolved; the caller falls back to the family name.
            return [];
        }

        var uris = new List<string>();
        if (reference.StartsWith("//", StringComparison.Ordinal))
        {
            uris.Add(MsResourcePrefix + reference);
        }
        else
        {
            if (key.Contains('/', StringComparison.Ordinal))
            {
                uris.Add($"{MsResourcePrefix}//{packageName}/{key}");
                uris.Add($"{MsResourcePrefix}///{key}");
                uris.Add($"{MsResourcePrefix}//{packageName}/Resources/{key}");
            }
            else
            {
                uris.Add($"{MsResourcePrefix}//{packageName}/Resources/{key}");
                uris.Add($"{MsResourcePrefix}///Resources/{key}");
                uris.Add($"{MsResourcePrefix}//{packageName}/{key}");
            }
        }

        return uris.Select(uri => "@{" + packageFullName + "?" + uri + "}").ToList();
    }

    /// <summary>Full names are Name_Version_Arch_ResourceId_PublisherId; main packages have no ResourceId.</summary>
    private static bool IsResourcePackage(string packageFullName)
    {
        string[] parts = packageFullName.Split('_');
        return parts.Length >= 5 && parts[3].Length > 0;
    }

    private static string? TryReadPackageDisplayName(string packageFullName)
    {
        string? folder = SystemQueries.GetPackagePath(packageFullName);
        string? manifestPath = folder is null ? null : Path.Join(folder, "AppxManifest.xml");
        if (manifestPath is null || !ImagePaths.IsSafeToOpen(manifestPath))
        {
            // Sparse packages can live anywhere, including a share or a OneDrive folder.
            return null;
        }

        string? raw;
        try
        {
            using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            raw = ReadManifestDisplayName(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (raw is null)
        {
            return null;
        }

        if (!raw.StartsWith(MsResourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return raw;
        }

        foreach (string source in IndirectStringCandidates(raw, packageFullName))
        {
            string? resolved = SystemQueries.LoadIndirectString(source);
            if (!string.IsNullOrWhiteSpace(resolved) && !resolved.StartsWith(MsResourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return resolved.Trim();
            }
        }

        return null;
    }
}
