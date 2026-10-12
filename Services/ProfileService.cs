using System.Text.Json;
using FuriXeus.Tuber.Native.Models;

namespace FuriXeus.Tuber.Native.Services;

public sealed class ProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string UserProfilePath => Path.Combine(ModelInstaller.AppDataRoot, "profile.json");
    public string BundledProfilePath => Path.Combine(AppContext.BaseDirectory, "profile.json");

    public async Task<AppProfile> LoadAsync()
    {
        Directory.CreateDirectory(ModelInstaller.AppDataRoot);
        var bundled = await ReadAsync(BundledProfilePath);

        if (!File.Exists(UserProfilePath))
            return bundled;

        AppProfile user;
        try
        {
            user = await ReadAsync(UserProfilePath);
        }
        catch
        {
            // A broken user profile should never stop the core from starting.
            return bundled;
        }

        if (string.Equals(user.Version, bundled.Version, StringComparison.OrdinalIgnoreCase))
            return user;

        // Source updates can change assets, state IDs, detector mappings and trigger phrases.
        // Older builds saved the whole profile to AppData, which accidentally pinned those old
        // definitions forever. Migrate user tuning plus the preferred audio routes, then
        // take the actual state graph from the newly bundled profile.
        bundled.Settings.TalkThresholdDb = user.Settings.TalkThresholdDb;
        bundled.Settings.TriggerLeadMs = user.Settings.TriggerLeadMs;
        bundled.Settings.VoiceInputDeviceId = user.Settings.VoiceInputDeviceId;
        bundled.Settings.VoiceInputDeviceName = user.Settings.VoiceInputDeviceName;
        bundled.Settings.EventInputDeviceId = user.Settings.EventInputDeviceId;
        bundled.Settings.EventInputDeviceName = user.Settings.EventInputDeviceName;
        bundled.Settings.CharacterSkinFolder = user.Settings.CharacterSkinFolder;
        bundled.Settings.AssetOverrides = new Dictionary<string, string>(user.Settings.AssetOverrides ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        bundled.Settings.SetupCompleted = user.Settings.SetupCompleted;
        bundled.Settings.CalibratedNoiseFloorDb = user.Settings.CalibratedNoiseFloorDb;
        bundled.Settings.TrainingProfileName = string.IsNullOrWhiteSpace(user.Settings.TrainingProfileName) ? "Default" : user.Settings.TrainingProfileName;
        await SaveAsync(bundled);
        return bundled;
    }

    public async Task SaveAsync(AppProfile profile)
    {
        Directory.CreateDirectory(ModelInstaller.AppDataRoot);
        await using var stream = File.Create(UserProfilePath);
        await JsonSerializer.SerializeAsync(stream, profile, JsonOptions);
    }

    private static async Task<AppProfile> ReadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AppProfile>(stream, JsonOptions)
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} could not be read.");
    }
}
