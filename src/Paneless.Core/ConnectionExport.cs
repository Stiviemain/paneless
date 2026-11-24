using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Paneless.Core;

public enum ExportDisplayMode { Embedded, External }

public enum ExportEntryKind { Mirror, RemoteApp, Screen }

/// <summary>One Paneless target (a device mirror, remote app or second screen) to export into an external connection manager.</summary>
public sealed record ExportEntry(
    string StableKey,
    string Name,
    string? Folder,
    string Description,
    ExportEntryKind Kind,
    string TargetId,
    byte[]? IconPng = null);

/// <summary>Formats Paneless mirrors and remote apps for a third-party connection manager (e.g. Remote Desktop Manager,
/// Royal TS, mRemoteNG). Adding another tool only requires implementing this interface and adding it to <see cref="ConnectionExporters.All"/>.</summary>
public interface IConnectionExporter
{
    string Id { get; }
    string Name { get; }
    string FileExtension { get; }
    string FileTypeName { get; }
    bool SupportsEmbedded { get; }
    bool SupportsIcons { get; }
    string Export(IReadOnlyList<ExportEntry> entries, string exePath, ExportDisplayMode mode, string? rootFolder = null);
}

public static class ConnectionExporters
{
    static readonly Guid NamespaceId = new("8a6b9e3f-7c21-4d58-9e14-3b5f8c7a2d10");

    public static IReadOnlyList<IConnectionExporter> All { get; } = [new RdmConnectionExporter()];

    public static IConnectionExporter? Find(string id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stable RFC 4122 v5 UUID for a Paneless target, so re-importing into a vault updates entries in place.</summary>
    public static Guid DeterministicGuid(string stableKey)
    {
        var ns = NamespaceId.ToByteArray();
        SwapGuidByteOrder(ns);
        var nameBytes = Encoding.UTF8.GetBytes(stableKey);
        var data = new byte[ns.Length + nameBytes.Length];
        Buffer.BlockCopy(ns, 0, data, 0, ns.Length);
        Buffer.BlockCopy(nameBytes, 0, data, ns.Length, nameBytes.Length);

        var hash = SHA1.HashData(data);
        var guid = new byte[16];
        Buffer.BlockCopy(hash, 0, guid, 0, 16);
        guid[6] = (byte)((guid[6] & 0x0F) | 0x50);   // version 5
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);   // RFC 4122 variant
        SwapGuidByteOrder(guid);
        return new Guid(guid);
    }

    static void SwapGuidByteOrder(byte[] b)
    {
        (b[0], b[3]) = (b[3], b[0]);
        (b[1], b[2]) = (b[2], b[1]);
        (b[4], b[5]) = (b[5], b[4]);
        (b[6], b[7]) = (b[7], b[6]);
    }

    public static string BuildArguments(ExportEntry entry, ExportDisplayMode mode)
    {
        var targetFlag = entry.Kind switch
        {
            ExportEntryKind.RemoteApp => "--open-app",
            ExportEntryKind.Screen => "--screen",
            _ => "--mirror",
        };
        var id = entry.TargetId.Contains(' ') ? $"\"{entry.TargetId}\"" : entry.TargetId;
        return mode == ExportDisplayMode.Embedded
            ? $"--embed {targetFlag} {id}"
            : $"{targetFlag} {id}";
    }

    public static string BuildCommandLine(string exePath, ExportEntry entry, ExportDisplayMode mode)
    {
        var exe = exePath.Contains(' ') ? $"\"{exePath}\"" : exePath;
        return $"{exe} {BuildArguments(entry, mode)}";
    }

    public static string CombineFolder(string? rootFolder, string? subFolder)
    {
        var parts = new[] { rootFolder, subFolder }
            .Select(p => (p ?? "").Trim().Trim('\\', '/'))
            .Where(p => p.Length > 0);
        return string.Join('\\', parts);
    }

    /// <summary>Safe default file name (without extension) for an export.</summary>
    public static string SuggestedFileName(IReadOnlyList<ExportEntry> entries, string? fallback = null)
    {
        var raw = entries.Count == 1 ? entries[0].Name : !string.IsNullOrWhiteSpace(fallback) ? fallback : "Paneless";
        var bad = new HashSet<char>(Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']));
        var clean = new string(raw.Select(c => bad.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "Paneless";
        return clean.Length > 80 ? clean[..80] : clean;
    }
}

/// <summary>Devolutions Remote Desktop Manager (.rdm) XML export using native CommandLine entries.</summary>
public sealed class RdmConnectionExporter : IConnectionExporter
{
    public string Id => "rdm";
    public string Name => "Remote Desktop Manager (Devolutions)";
    public string FileExtension => "rdm";
    public string FileTypeName => "Remote Desktop Manager";
    public bool SupportsEmbedded => true;
    public bool SupportsIcons => true;

    public string Export(IReadOnlyList<ExportEntry> entries, string exePath, ExportDisplayMode mode, string? rootFolder = null)
    {
        var connections = new XElement("Connections");
        foreach (var entry in entries)
        {
            var id = ConnectionExporters.DeterministicGuid(entry.StableKey).ToString("D");
            var group = ConnectionExporters.CombineFolder(rootFolder, entry.Folder);
            var cmd = ConnectionExporters.BuildCommandLine(exePath, entry, mode);

            var conn = new XElement("Connection",
                new XElement("ConnectionType", "CommandLine"),
                new XElement("ID", id),
                new XElement("Name", entry.Name));

            if (group.Length > 0) conn.Add(new XElement("Group", group));
            if (!string.IsNullOrWhiteSpace(entry.Description)) conn.Add(new XElement("Description", entry.Description));

            if (entry.IconPng is { Length: > 0 } png)
            {
                var md5 = Convert.ToHexString(MD5.HashData(png)).ToLowerInvariant();
                conn.Add(new XElement("Image", Convert.ToBase64String(png)));
                conn.Add(new XElement("ImageMD5", md5));
            }

            conn.Add(new XElement("CommandLine", cmd));
            conn.Add(new XElement("OpenEmbedded", mode == ExportDisplayMode.Embedded ? "true" : "false"));

            var cmdNode = new XElement("Cmd");
            if (mode == ExportDisplayMode.Embedded)
            {
                cmdNode.Add(new XElement("EmbeddedWaitTime", 3000));
                cmdNode.Add(new XElement("ProcessName", "Paneless"));
            }
            else
            {
                cmdNode.Add(new XElement("UseDefaultWorkingDirectory", "true"));
            }
            conn.Add(cmdNode);
            connections.Add(conn);
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("RDMExport",
                connections,
                new XElement("Version", 2)));

        using var ms = new MemoryStream();
        using (var sw = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            doc.Save(sw);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
