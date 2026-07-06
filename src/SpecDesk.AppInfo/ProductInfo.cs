namespace SpecDesk.AppInfo;

/// <summary>
/// The single source of the product's identity: its name and version. Before this type existed, both
/// were hand-duplicated across the tree — the AppData root, the window title, the log file prefix, the
/// git fallback commit identity, and the GitHub API User-Agent each spelled "SpecDesk" independently, and
/// the User-Agent additionally hard-coded a second, unrelated "1.0" version literal even though the
/// product's actual <c>&lt;Version&gt;</c> (Directory.Build.props) was 0.1.0. Every one of those sites now
/// reads from here instead.
/// </summary>
public static class ProductInfo
{
    /// <summary>The product name. Used verbatim for: the AppData root folder (<see cref="AppPaths"/>),
    /// the window title, the log file prefix, and the git fallback commit identity.</summary>
    public const string Name = "SpecDesk";

    /// <summary>
    /// The product version, read from this assembly's own build-time <c>&lt;Version&gt;</c>
    /// (Directory.Build.props sets it centrally for every project) rather than a second hard-coded
    /// literal — a release bump only ever needs to touch Directory.Build.props. Falls back to "0.0.0"
    /// in the vanishingly unlikely case the assembly carries no version at all (never observed in
    /// practice: the SDK always stamps one), so a caller like the GitHub User-Agent still gets a
    /// well-formed value instead of a null-reference.
    /// </summary>
    public static string Version { get; } =
        typeof(ProductInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
