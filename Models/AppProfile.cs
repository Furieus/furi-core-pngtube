using System.Text.Json.Serialization;

namespace FuriXeus.Tuber.Native.Models;

public sealed class AppProfile
{
    public string App { get; set; } = "FuriXeus Tuber";
    public string Version { get; set; } = "0.7.3";
    public string ProfileName { get; set; } = "Chaos Critter";
    public TuberSettings Settings { get; set; } = new();
    public List<ReactionState> States { get; set; } = [];

    [JsonIgnore]
    public Dictionary<string, ReactionState> StateMap =>
        States.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

    public ReactionState GetState(string id)
    {
        return States.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? States.First();
    }

    public IEnumerable<string> GetAllPhrases() =>
        States.SelectMany(s => s.TriggerPhrases)
              .Where(p => !string.IsNullOrWhiteSpace(p))
              .Select(p => p.Trim().ToLowerInvariant())
              .Distinct(StringComparer.OrdinalIgnoreCase);
}

public sealed class TuberSettings
{
    public string DefaultStateId { get; set; } = "idle";
    public double TalkThresholdDb { get; set; } = -42;
    public int TalkReleaseMs { get; set; } = 130;
    public int TriggerLeadMs { get; set; } = 220;
    public int ManualStateHoldMs { get; set; } = 2200;
    public int HumAttackMs { get; set; } = 360;
    public int HumReleaseHoldMs { get; set; } = 1400;
    public int YawnAttackMs { get; set; } = 1100;
    public int YawnReleaseHoldMs { get; set; } = 1800;
    public int LaughReleaseHoldMs { get; set; } = 1400;
    public string VoiceInputDeviceId { get; set; } = "";
    public string VoiceInputDeviceName { get; set; } = "";
    public string EventInputDeviceId { get; set; } = "";
    public string EventInputDeviceName { get; set; } = "";
    public string CharacterSkinFolder { get; set; } = "";
    public Dictionary<string, string> AssetOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // v0.7.0 reaction trainer / first-run setup
    public bool SetupCompleted { get; set; } = false;
    public double CalibratedNoiseFloorDb { get; set; } = -60;
    public string TrainingProfileName { get; set; } = "Default";
}

public sealed class ReactionState
{
    public string Id { get; set; } = "idle";
    public string Name { get; set; } = "Idle";
    public string IdleAsset { get; set; } = "";
    public string TalkAsset { get; set; } = "";
    public List<string> TriggerPhrases { get; set; } = [];
    public string? AudioEvent { get; set; }
    public int Priority { get; set; }
    public int HoldMs { get; set; } = 2200;
    public string Accent { get; set; } = "pink";
}
