using FuriXeus.Tuber.Native.Models;

namespace FuriXeus.Tuber.Native.Services;

public sealed class DebugLogService : IDisposable
{
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private sealed class AudioLogState
    {
        public DateTimeOffset LastWrite { get; set; } = DateTimeOffset.MinValue;
        public bool LastVoiceActive { get; set; }
        public bool LastHumDetected { get; set; }
        public bool LastYawnDetected { get; set; }
        public bool LastLaughDetected { get; set; }
    }

    private readonly Dictionary<string, AudioLogState> _audioLogStates = new(StringComparer.OrdinalIgnoreCase);
    private string _lastRenderState = "";
    private string _lastRenderReason = "";
    private bool _lastRenderTalking;
    private DateTimeOffset _lastClassifierWrite = DateTimeOffset.MinValue;
    private string _lastClassifierTop = "";
    private bool _lastClassifierLaugh;
    private bool _lastClassifierYawn;
    private bool _lastClassifierHmm;

    public static string LogDirectory => Path.Combine(ModelInstaller.AppDataRoot, "Logs");
    public bool IsEnabled => _writer is not null;
    public string? CurrentLogPath { get; private set; }

    public string Start(AppProfile? profile = null)
    {
        lock (_sync)
        {
            StopUnsafe();
            _audioLogStates.Clear();
            Directory.CreateDirectory(LogDirectory);
            CurrentLogPath = Path.Combine(LogDirectory, $"furi-core-debug-v{BuildInfo.Version}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.csv");
            _writer = new StreamWriter(File.Open(CurrentLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true
            };

            _writer.WriteLine("timestamp_local,timestamp_utc,build_version,category,event,detail,state_id,state_name,is_talking,reason,asset_path,db,rms,peak,pitch_hz,pitch_clarity,zcr,voice_active,hum_confidence,yawn_confidence,laugh_confidence,hum_detected,yawn_detected,laugh_detected,transcript,is_final");
            WriteRowUnsafe(DateTimeOffset.Now, "SESSION", "START", profile is null ? $"debug logging started • build {BuildInfo.Version}" : $"debug logging started • build {BuildInfo.Version} • profile {profile.ProfileName} {profile.Version}");
            if (profile is not null)
            {
                WriteRowUnsafe(DateTimeOffset.Now, "PROFILE", "SETTINGS", $"threshold={profile.Settings.TalkThresholdDb:F1} dB; triggerLead={profile.Settings.TriggerLeadMs} ms; humAttack={profile.Settings.HumAttackMs} ms; yawnAttack={profile.Settings.YawnAttackMs} ms; voiceRoute='{profile.Settings.VoiceInputDeviceName}'; eventRoute='{profile.Settings.EventInputDeviceName}'");
                foreach (var state in profile.States)
                    WriteRowUnsafe(DateTimeOffset.Now, "PROFILE", "STATE", $"{state.Id}; priority={state.Priority}; hold={state.HoldMs} ms; event={state.AudioEvent ?? "none"}; phrases={string.Join(" | ", state.TriggerPhrases)}", state.Id, state.Name);
            }

            return CurrentLogPath;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_writer is not null)
                WriteRowUnsafe(DateTimeOffset.Now, "SESSION", "STOP", "debug logging stopped");
            StopUnsafe();
        }
    }

    public void LogAction(string eventName, string detail = "") => Write(DateTimeOffset.Now, "ACTION", eventName, detail);
    public void LogStatus(string eventName, string detail = "") => Write(DateTimeOffset.Now, "STATUS", eventName, detail);
    public void LogFault(string detail) => Write(DateTimeOffset.Now, "FAULT", "ERROR", detail);
    public void LogEventGuard(string eventName, string detail = "") => Write(DateTimeOffset.Now, "EVENT_GUARD", eventName, detail);
    public void LogReaction(ReactionLogEntry entry) => Write(entry.Timestamp, "REACTION", entry.Label, entry.Detail);

    public void LogSpeech(string observedText, bool isFinal)
    {
        Write(DateTimeOffset.Now, "WORD_CORE", isFinal ? "FINAL_TEXT" : "PARTIAL_TEXT", observedText, transcript: observedText, isFinal: isFinal);
    }

    public void LogClassifier(PersonalizedEventClassification classification)
    {
        lock (_sync)
        {
            if (_writer is null) return;
            var lockChanged = classification.LaughDetected != _lastClassifierLaugh ||
                              classification.YawnDetected != _lastClassifierYawn ||
                              classification.HmmDetected != _lastClassifierHmm;
            var topChanged = !string.Equals(classification.TopLabel, _lastClassifierTop, StringComparison.OrdinalIgnoreCase);
            var due = (classification.Timestamp - _lastClassifierWrite).TotalMilliseconds >= 250;
            if (!lockChanged && !topChanged && !due) return;

            _lastClassifierWrite = classification.Timestamp;
            _lastClassifierTop = classification.TopLabel;
            _lastClassifierLaugh = classification.LaughDetected;
            _lastClassifierYawn = classification.YawnDetected;
            _lastClassifierHmm = classification.HmmDetected;

            WriteRowUnsafe(classification.Timestamp,
                "CLASSIFIER",
                classification.TopLabel.ToUpperInvariant(),
                classification.Detail,
                humConfidence: classification.HmmScore,
                yawnConfidence: classification.YawnScore,
                laughConfidence: classification.LaughScore,
                humDetected: classification.HmmDetected,
                yawnDetected: classification.YawnDetected,
                laughDetected: classification.LaughDetected);
        }
    }

    public void LogRecognitionDecision(string raw, string normalized, string combined, bool isFinal, string? matchedState, string? matchedPhrase)
    {
        var decision = matchedState is null
            ? "NO_TRIGGER_MATCH"
            : $"MATCH {matchedState} <= '{matchedPhrase}'";
        var detail = $"raw='{raw}' | normalized='{normalized}' | buffer='{combined}' | {decision}";
        Write(DateTimeOffset.Now, "TRIGGER_MATCH", matchedState is null ? "NO_MATCH" : "MATCH", detail, stateId: matchedState ?? "", transcript: combined, isFinal: isFinal);
    }

    public void LogRender(ReactionRenderState render)
    {
        lock (_sync)
        {
            if (_writer is null) return;

            var changed = !string.Equals(_lastRenderState, render.StateId, StringComparison.OrdinalIgnoreCase) ||
                          !string.Equals(_lastRenderReason, render.Reason, StringComparison.Ordinal) ||
                          _lastRenderTalking != render.IsTalking;
            if (!changed) return;

            _lastRenderState = render.StateId;
            _lastRenderReason = render.Reason;
            _lastRenderTalking = render.IsTalking;

            WriteRowUnsafe(render.Timestamp, "RENDER", "STATE_CHANGE", "avatar render changed", render.StateId, render.StateName, render.IsTalking, render.Reason, render.AssetPath);
        }
    }

    public void LogAudio(AudioSnapshot snapshot, string source = "MERGED")
    {
        lock (_sync)
        {
            if (_writer is null) return;

            if (!_audioLogStates.TryGetValue(source, out var state))
            {
                state = new AudioLogState();
                _audioLogStates[source] = state;
            }

            var now = snapshot.Timestamp;
            var voiceChanged = snapshot.VoiceActive != state.LastVoiceActive;
            var lockChanged = snapshot.HumDetected != state.LastHumDetected || snapshot.YawnDetected != state.LastYawnDetected || snapshot.LaughDetected != state.LastLaughDetected;
            var dueForSample = (now - state.LastWrite).TotalMilliseconds >= 250;
            if (!voiceChanged && !lockChanged && !dueForSample) return;

            state.LastWrite = now;
            state.LastVoiceActive = snapshot.VoiceActive;
            state.LastHumDetected = snapshot.HumDetected;
            state.LastYawnDetected = snapshot.YawnDetected;
            state.LastLaughDetected = snapshot.LaughDetected;

            var eventName = lockChanged
                ? "LOCK_CHANGE"
                : voiceChanged
                    ? "VOICE_CHANGE"
                    : "SAMPLE";
            var category = string.Equals(source, "MERGED", StringComparison.OrdinalIgnoreCase) ? "AUDIO" : $"AUDIO_{source.ToUpperInvariant()}";
            WriteRowUnsafe(now, category, eventName, $"{source.ToLowerInvariant()} telemetry sample",
                db: snapshot.Db,
                rms: snapshot.Rms,
                peak: snapshot.Peak,
                pitchHz: snapshot.PitchHz,
                pitchClarity: snapshot.PitchClarity,
                zcr: snapshot.ZeroCrossingRate,
                voiceActive: snapshot.VoiceActive,
                humConfidence: snapshot.HumConfidence,
                yawnConfidence: snapshot.YawnConfidence,
                laughConfidence: snapshot.LaughConfidence,
                humDetected: snapshot.HumDetected,
                yawnDetected: snapshot.YawnDetected,
                laughDetected: snapshot.LaughDetected);
        }
    }

    private void Write(DateTimeOffset timestamp, string category, string eventName, string detail,
        string stateId = "", string stateName = "", bool? isTalking = null, string reason = "", string assetPath = "",
        double? db = null, double? rms = null, double? peak = null, double? pitchHz = null, double? pitchClarity = null, double? zcr = null,
        bool? voiceActive = null, double? humConfidence = null, double? yawnConfidence = null, double? laughConfidence = null,
        bool? humDetected = null, bool? yawnDetected = null, bool? laughDetected = null, string transcript = "", bool? isFinal = null)
    {
        lock (_sync)
        {
            if (_writer is null) return;
            WriteRowUnsafe(timestamp, category, eventName, detail, stateId, stateName, isTalking, reason, assetPath,
                db, rms, peak, pitchHz, pitchClarity, zcr, voiceActive, humConfidence, yawnConfidence, laughConfidence,
                humDetected, yawnDetected, laughDetected, transcript, isFinal);
        }
    }

    private void WriteRowUnsafe(DateTimeOffset timestamp, string category, string eventName, string detail,
        string stateId = "", string stateName = "", bool? isTalking = null, string reason = "", string assetPath = "",
        double? db = null, double? rms = null, double? peak = null, double? pitchHz = null, double? pitchClarity = null, double? zcr = null,
        bool? voiceActive = null, double? humConfidence = null, double? yawnConfidence = null, double? laughConfidence = null,
        bool? humDetected = null, bool? yawnDetected = null, bool? laughDetected = null, string transcript = "", bool? isFinal = null)
    {
        _writer?.WriteLine(string.Join(',', new[]
        {
            Csv(timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz")),
            Csv(timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff")),
            Csv(BuildInfo.Version),
            Csv(category),
            Csv(eventName),
            Csv(detail),
            Csv(stateId),
            Csv(stateName),
            Csv(isTalking?.ToString() ?? ""),
            Csv(reason),
            Csv(assetPath),
            Csv(db?.ToString("F2") ?? ""),
            Csv(rms?.ToString("F6") ?? ""),
            Csv(peak?.ToString("F6") ?? ""),
            Csv(pitchHz?.ToString("F1") ?? ""),
            Csv(pitchClarity?.ToString("F3") ?? ""),
            Csv(zcr?.ToString("F4") ?? ""),
            Csv(voiceActive?.ToString() ?? ""),
            Csv(humConfidence?.ToString("F3") ?? ""),
            Csv(yawnConfidence?.ToString("F3") ?? ""),
            Csv(laughConfidence?.ToString("F3") ?? ""),
            Csv(humDetected?.ToString() ?? ""),
            Csv(yawnDetected?.ToString() ?? ""),
            Csv(laughDetected?.ToString() ?? ""),
            Csv(transcript),
            Csv(isFinal?.ToString() ?? "")
        }));
    }

    private void StopUnsafe()
    {
        _writer?.Dispose();
        _writer = null;
    }

    private static string Csv(string value)
    {
        value = value.Replace("\r", " ").Replace("\n", " ");
        return value.Contains(',') || value.Contains('"') || value.Contains('\'')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    public void Dispose() => Stop();
}
