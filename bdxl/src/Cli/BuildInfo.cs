using System.Reflection;

namespace Bdxl;

/// <summary>
/// What this exe is: its version and the git state and time it was built from - put in by the build (the BdxlBuildInfo
/// target in Bdxl.csproj). Full = "0.6.0+8082b22.20261103T2215Z" (semver build metadata; "-dirty" after the commit when
/// it was built with uncommitted changes); CreatedBy = "bdxl " + Full, which every staged session records.
/// </summary>
public static class BuildInfo
{
    static readonly Assembly Me = typeof(BuildInfo).Assembly;

    public static string Full { get; } = Me.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    public static string Version => Full.Split('+')[0];
    public static string CreatedBy => "bdxl " + Full;
    public static string Copyright { get; } = Me.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
    public const string License = "PolyForm Shield 1.0.0";
    public static string Configuration { get; } = Me.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";
    public static string? Commit { get; } = Metadata("GitCommit");
    public static string? Branch { get; } = Metadata("GitBranch");
    public static bool Dirty { get; } = Metadata("GitDirty") == "true";
    public static DateTime? BuiltUtc { get; } =
        DateTime.TryParse(Metadata("BuiltUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    static string? Metadata(string key) =>
        Me.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : null;
}
