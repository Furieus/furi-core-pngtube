using System.Windows.Media.Imaging;

namespace FuriXeus.Tuber.Native.Services;

public sealed class AvatarAssetService
{
    private readonly Dictionary<string, BitmapImage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _assetOverrides = new(StringComparer.OrdinalIgnoreCase);
    private string? _overrideFolder;

    public string? OverrideFolder => _overrideFolder;
    public IReadOnlyDictionary<string, string> AssetOverrides => _assetOverrides;

    public void SetOverrideFolder(string? folder)
    {
        _overrideFolder = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
            ? Path.GetFullPath(folder)
            : null;
        _cache.Clear();
    }

    public void SetAssetOverrides(IDictionary<string, string>? overrides)
    {
        _assetOverrides.Clear();
        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
                _assetOverrides[NormalizeKey(pair.Key)] = Path.GetFullPath(pair.Value);
            }
        }
        _cache.Clear();
    }

    public void SetAssetOverride(string relativePath, string? filePath)
    {
        var key = NormalizeKey(relativePath);
        if (string.IsNullOrWhiteSpace(filePath))
            _assetOverrides.Remove(key);
        else
            _assetOverrides[key] = Path.GetFullPath(filePath);
        _cache.Clear();
    }

    public void ClearAssetOverride(string relativePath)
    {
        _assetOverrides.Remove(NormalizeKey(relativePath));
        _cache.Clear();
    }

    public void ClearAllAssetOverrides()
    {
        _assetOverrides.Clear();
        _cache.Clear();
    }

    public string? GetAssetOverride(string relativePath) =>
        _assetOverrides.TryGetValue(NormalizeKey(relativePath), out var path) ? path : null;

    public void Reload() => _cache.Clear();

    public string Resolve(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, normalized));
    }

    public string ResolveEffective(string relativePath)
    {
        var direct = TryResolveDirect(relativePath);
        if (direct is not null) return direct;

        var restPath = GetRestFallbackPath(relativePath);
        if (restPath is not null)
        {
            var rest = TryResolveDirect(restPath);
            if (rest is not null) return rest;
        }

        // Return the normal built-in path so callers still get a useful missing-file exception.
        return Resolve(relativePath);
    }

    public AssetResolution Describe(string relativePath)
    {
        var explicitPath = GetAssetOverride(relativePath);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (File.Exists(explicitPath))
                return new AssetResolution(Path.GetFullPath(explicitPath), "CUSTOM", true, true);

            var fallback = ResolveFallbackWithSource(relativePath);
            return new AssetResolution(fallback.Path, $"CUSTOM MISSING → {fallback.Source}", true, false);
        }

        var folderCandidate = ResolveFromFolder(relativePath);
        if (folderCandidate is not null)
            return new AssetResolution(folderCandidate, "SKIN FOLDER", false, true);

        var builtIn = Resolve(relativePath);
        if (File.Exists(builtIn))
            return new AssetResolution(builtIn, "BUILT-IN", false, true);

        var restPath = GetRestFallbackPath(relativePath);
        if (restPath is not null)
        {
            var restExplicit = GetAssetOverride(restPath);
            if (!string.IsNullOrWhiteSpace(restExplicit) && File.Exists(restExplicit))
                return new AssetResolution(Path.GetFullPath(restExplicit), "REST FALLBACK • CUSTOM", false, true);

            var restFolder = ResolveFromFolder(restPath);
            if (restFolder is not null)
                return new AssetResolution(restFolder, "REST FALLBACK • SKIN FOLDER", false, true);

            var restBuiltIn = Resolve(restPath);
            if (File.Exists(restBuiltIn))
                return new AssetResolution(restBuiltIn, "REST FALLBACK • BUILT-IN", false, true);
        }

        return new AssetResolution(builtIn, "BUILT-IN MISSING", false, false);
    }

    private string? TryResolveDirect(string relativePath)
    {
        if (_assetOverrides.TryGetValue(NormalizeKey(relativePath), out var explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        var folderCandidate = ResolveFromFolder(relativePath);
        if (folderCandidate is not null)
            return folderCandidate;

        var builtIn = Resolve(relativePath);
        return File.Exists(builtIn) ? builtIn : null;
    }

    private (string Path, string Source) ResolveFallbackWithSource(string relativePath)
    {
        var folderCandidate = ResolveFromFolder(relativePath);
        if (folderCandidate is not null) return (folderCandidate, "SKIN FOLDER");

        var builtIn = Resolve(relativePath);
        if (File.Exists(builtIn)) return (builtIn, "BUILT-IN");

        var restPath = GetRestFallbackPath(relativePath);
        if (restPath is not null)
        {
            var restExplicit = GetAssetOverride(restPath);
            if (!string.IsNullOrWhiteSpace(restExplicit) && File.Exists(restExplicit))
                return (Path.GetFullPath(restExplicit), "REST FALLBACK • CUSTOM");

            var restFolder = ResolveFromFolder(restPath);
            if (restFolder is not null) return (restFolder, "REST FALLBACK • SKIN FOLDER");

            var restBuiltIn = Resolve(restPath);
            if (File.Exists(restBuiltIn)) return (restBuiltIn, "REST FALLBACK • BUILT-IN");
        }

        return (builtIn, "BUILT-IN MISSING");
    }

    private static string? GetRestFallbackPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var directory = slash >= 0 ? normalized[..(slash + 1)] : "";
        var file = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        var dot = file.LastIndexOf('.');
        if (dot <= 0) return null;

        var stem = file[..dot];
        if (!stem.EndsWith("_talk", StringComparison.OrdinalIgnoreCase)) return null;

        return directory + stem[..^5] + file[dot..];
    }

    public BitmapImage Load(string relativePath)
    {
        var fullPath = ResolveEffective(relativePath);
        if (_cache.TryGetValue(fullPath, out var cached)) return cached;
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Avatar asset not found.", fullPath);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        _cache[fullPath] = bitmap;
        return bitmap;
    }

    private string? ResolveFromFolder(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(_overrideFolder)) return null;

        var fileName = Path.GetFileName(relativePath);
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var candidates = new[]
        {
            Path.Combine(_overrideFolder, fileName),
            Path.Combine(_overrideFolder, normalized),
            Path.Combine(_overrideFolder, "Assets", "ChaosCritter", fileName)
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);

        return null;
    }

    private static string NormalizeKey(string relativePath) =>
        relativePath.Replace('\\', '/').Trim();
}

public sealed record AssetResolution(
    string EffectivePath,
    string Source,
    bool HasExplicitOverride,
    bool ExplicitOverrideExists);
