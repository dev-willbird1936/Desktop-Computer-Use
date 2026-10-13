// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace ShadowUse.Tools;

/// <summary>On-disk identity of one file, used to notice a binary replaced under a running process.</summary>
public sealed record FileStamp(string Path, long? LengthBytes, DateTimeOffset? LastWriteUtc)
{
    public static FileStamp Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(path, info.Length, info.LastWriteTimeUtc) : new FileStamp(path, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FileStamp(path, null, null);
        }
    }
}

/// <summary>What this running process is: assembly version, build-time metadata, module identity, process, and the
/// binaries it was started from. BuildTimestampUtc comes only from assembly metadata stamped by MSBuild;
/// it is null when the assembly carries none, and is never derived from process start or file times.</summary>
public sealed record RuntimeIdentity(
    string Version,
    string AssemblyVersion,
    string? InformationalVersion,
    string AssemblyName,
    string? BuildTimestampUtc,
    Guid ModuleVersionId,
    int ProcessId,
    DateTimeOffset ProcessStartedUtc,
    FileStamp? Executable,
    FileStamp? AssemblyFile);

public static class RuntimeIdentification
{
    public const string BuildTimestampMetadataKey = "BuildTimestampUtc";
    private const string BuildTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>Captured when first touched; Main touches it before anything else so the file stamps describe startup.</summary>
    public static RuntimeIdentity Current { get; } = Capture(
        typeof(RuntimeIdentification).Assembly,
        Environment.ProcessPath,
        Environment.ProcessId,
        ReadProcessStart());

    public static RuntimeIdentity Capture(Assembly assembly, string? executablePath, int processId, DateTimeOffset processStartedUtc)
    {
        var name = assembly.GetName();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var assemblyFile = string.IsNullOrEmpty(assembly.Location) ? null : FileStamp.Read(assembly.Location);
        return new RuntimeIdentity(
            Version: name.Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : "0.0.0",
            AssemblyVersion: name.Version?.ToString() ?? "0.0.0.0",
            InformationalVersion: informational,
            AssemblyName: name.Name ?? "",
            BuildTimestampUtc: ReadBuildTimestamp(assembly),
            ModuleVersionId: assembly.ManifestModule.ModuleVersionId,
            ProcessId: processId,
            ProcessStartedUtc: processStartedUtc,
            Executable: string.IsNullOrEmpty(executablePath) ? null : FileStamp.Read(executablePath),
            AssemblyFile: assemblyFile);
    }

    public static string? ReadBuildTimestamp(Assembly assembly)
    {
        var raw = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == BuildTimestampMetadataKey)?.Value;
        return DateTimeOffset.TryParseExact(raw, BuildTimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToString(BuildTimestampFormat, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Compares the binaries on disk now with their startup stamps. A true result means a newer build was
    /// written (or the file removed) while this process kept running the old image.</summary>
    public static bool BinariesChangedSinceStart(RuntimeIdentity identity, out List<string> changed)
    {
        changed = [];
        foreach (var (label, stamp) in new[] { ("executable", identity.Executable), ("assembly_file", identity.AssemblyFile) })
            if (stamp is not null && FileStamp.Read(stamp.Path) != stamp) changed.Add(label);
        return changed.Count > 0;
    }

    public static object Describe(RuntimeIdentity identity, DateTimeOffset nowUtc)
    {
        var changed = BinariesChangedSinceStart(identity, out var changedFiles);
        return new
        {
            version = identity.Version,
            assembly_version = identity.AssemblyVersion,
            informational_version = identity.InformationalVersion,
            assembly_name = identity.AssemblyName,
            build_timestamp_utc = identity.BuildTimestampUtc,
            build_timestamp_source = identity.BuildTimestampUtc is null
                ? "unavailable: assembly has no BuildTimestampUtc metadata"
                : "assembly metadata stamped at build time",
            module_version_id = identity.ModuleVersionId.ToString(),
            process_id = identity.ProcessId,
            process_started_utc = identity.ProcessStartedUtc.ToString("O", CultureInfo.InvariantCulture),
            uptime_seconds = Math.Max(0, (long)(nowUtc - identity.ProcessStartedUtc).TotalSeconds),
            executable = Describe(identity.Executable),
            assembly_file = Describe(identity.AssemblyFile),
            stale_executable = changed,
            changed_files = changedFiles,
            stale_hint = changed
                ? "A different binary is on disk than the one this process started from. Restart and reconnect DCU to run it."
                : "On-disk binaries match what this process started from.",
        };
    }

    /// <summary>One line the client sees at initialize, so a connection can be matched to a specific build and process.</summary>
    public static string InstructionsSuffix(RuntimeIdentity identity) =>
        $"RUNTIME: dcu {identity.Version}, build {identity.BuildTimestampUtc ?? "unknown"}, module {identity.ModuleVersionId}, pid {identity.ProcessId}. " +
        "health_check reports the same identity plus the tool schema fingerprint; reconnect if they differ from what you expect.";

    private static object? Describe(FileStamp? stamp) => stamp is null
        ? null
        : new
        {
            path = stamp.Path,
            length_bytes_at_start = stamp.LengthBytes,
            last_write_utc_at_start = stamp.LastWriteUtc?.ToString("O", CultureInfo.InvariantCulture),
        };

    private static DateTimeOffset ReadProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }
}
