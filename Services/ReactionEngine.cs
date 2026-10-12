using System.Text.RegularExpressions;
using FuriXeus.Tuber.Native.Models;

namespace FuriXeus.Tuber.Native.Services;

public sealed class ReactionEngine
{
    private readonly object _sync = new();
    private readonly AppProfile _profile;
    private readonly Dictionary<string, DateTimeOffset> _recentPhraseTriggers = new(StringComparer.OrdinalIgnoreCase);

    private ReactionState? _activeReaction;
    private DateTimeOffset _activeUntil;
    private string _activeReason = "idle";
    private DateTimeOffset? _voiceStarted;
    private AudioSnapshot? _lastAudio;
    private ReactionRenderState? _lastRender;
    private string _lastTranscript = "";
    private bool _voiceWasActive;
    private bool _utteranceHasNormalSpeech;
    private bool _humGuardLogged;
    private bool _yawnGuardLogged;
    private bool _laughGuardLogged;
    // If a detector lock is suppressed by normal conversation, keep it suppressed
    // until that detector fully releases. This prevents a stale personalized lock from
    // firing a few milliseconds after the speech guard expires.
    private bool _humSuppressedLatch;
    private bool _yawnSuppressedLatch;
    private bool _laughSuppressedLatch;
    private string _currentSpeechTranscript = "";
    private DateTimeOffset _normalSpeechGuardUntil = DateTimeOffset.MinValue;

    public int TriggerLeadMs { get; set; }
    public string CurrentStateId => _lastRender?.StateId ?? _profile.Settings.DefaultStateId;
    public AudioSnapshot? LastAudio => _lastAudio;
    public string LastTranscript => _lastTranscript;

    public event Action<ReactionRenderState>? RenderChanged;
    public event Action<ReactionLogEntry>? LogEntryAdded;
    public event Action<string, bool>? TranscriptChanged;
    public event Action<string, string, string, bool, string?, string?>? RecognitionDebug;
    public event Action<string, string>? AudioGuardDebug;

    public ReactionEngine(AppProfile profile)
    {
        _profile = profile;
        TriggerLeadMs = profile.Settings.TriggerLeadMs;
    }

    public void OnAudio(AudioSnapshot snapshot)
    {
        lock (_sync)
        {
            _lastAudio = snapshot;
            var now = snapshot.Timestamp;

            // Conversation-first rule: once Word Core proves the current utterance contains
            // ordinary language, keep that lexical veto for the ENTIRE voice segment. The
            // previous timer-only guard could expire halfway through a sentence and let raw
            // Event Sense reinterpret a syllable as Laugh/Hum/Yawn. After the voice gate
            // closes we retain a short 360 ms tail so release-edge detector locks cannot flash.
            if (!snapshot.VoiceActive && _utteranceHasNormalSpeech && _voiceWasActive)
                _normalSpeechGuardUntil = now.AddMilliseconds(360);

            if (!_voiceWasActive && !snapshot.VoiceActive && _utteranceHasNormalSpeech && now > _normalSpeechGuardUntil)
            {
                _utteranceHasNormalSpeech = false;
                _currentSpeechTranscript = "";
            }

            if (snapshot.VoiceActive)
            {
                if (!_voiceWasActive)
                {
                    _voiceStarted = now;

                    // A short gate gap is commonly just one sentence being chopped up. Carry
                    // the lexical veto across that gap; only a genuine quiet break resets it.
                    var carrySpeechGuard = _utteranceHasNormalSpeech && now <= _normalSpeechGuardUntil;
                    if (!carrySpeechGuard)
                    {
                        _utteranceHasNormalSpeech = false;
                        _currentSpeechTranscript = "";
                        _normalSpeechGuardUntil = DateTimeOffset.MinValue;
                    }
                    else
                    {
                        _normalSpeechGuardUntil = now.AddMilliseconds(700);
                    }

                    _humGuardLogged = false;
                    _yawnGuardLogged = false;
                    _laughGuardLogged = false;
                }
                else
                {
                    _voiceStarted ??= now;
                }
            }
            else
            {
                _voiceStarted = null;
            }

            _voiceWasActive = snapshot.VoiceActive;

            // Suppression latches clear only after the underlying detector releases.
            // A fresh event must therefore produce a new rising edge after conversation ends.
            if (!snapshot.HumDetected && _humSuppressedLatch)
            {
                _humSuppressedLatch = false;
                AudioGuardDebug?.Invoke("HUM_SUPPRESSION_CLEARED", $"hum={snapshot.HumConfidence:F3}");
            }
            if (!snapshot.YawnDetected && _yawnSuppressedLatch)
            {
                _yawnSuppressedLatch = false;
                AudioGuardDebug?.Invoke("YAWN_SUPPRESSION_CLEARED", $"yawn={snapshot.YawnConfidence:F3}");
            }
            if (!snapshot.LaughDetected && _laughSuppressedLatch)
            {
                _laughSuppressedLatch = false;
                AudioGuardDebug?.Invoke("LAUGH_SUPPRESSION_CLEARED", $"laugh={snapshot.LaughConfidence:F3}");
            }

            var strongest = FindStrongestAudioEvent(snapshot);
            if (strongest is not null)
            {
                var (state, _) = strongest.Value;
                Activate(state, $"{state.AudioEvent} signal", now, GetAudioReleaseMs(state.AudioEvent), stickyAudio: true);
            }
            else if (_activeReaction?.AudioEvent is not null && !IsAudioEventStillAlive(_activeReaction.AudioEvent, snapshot))
            {
                if (_activeUntil < now)
                    _activeReaction = null;
            }
            else if (_activeReaction is not null && _activeReaction.AudioEvent is null && _activeUntil < now)
            {
                _activeReaction = null;
            }

            PublishRender(now);
        }
    }

    public void OnRecognizedText(string text, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var normalized = Normalize(text);
            if (normalized.Length == 0) return;

            // Vosk partials already contain the current utterance. Do not carry words from
            // a previous finalized utterance into trigger matching; stale words like "sleepy" or
            // "thinking" were re-firing reactions several sentences later in the v0.6.3 debug log.
            var combined = KeepLastWords(normalized, 14);
            _lastTranscript = combined;
            _currentSpeechTranscript = combined;
            TranscriptChanged?.Invoke(combined, isFinal);

            // Audio event sensing should not fight clear speech recognition. A normal phrase
            // like "let's see let's go to..." was being classified as a hum while Vosk was
            // already decoding ordinary words. Preserve audio-event inference for nonverbal
            // vocalizations, but mark this voice segment as lexical speech when Vosk has
            // recognizable language.
            if (!IsHumLikeUtterance(normalized) && !IsLaughLikeUtterance(normalized))
            {
                _utteranceHasNormalSpeech = true;
                _normalSpeechGuardUntil = now.AddMilliseconds(isFinal ? 900 : 1200);

                // Ordinary recognized language wins over every inferred acoustic reaction.
                // If Event Sense fired a moment before Vosk produced words, revoke it
                // immediately. Explicit word triggers are handled later and can still force a
                // reaction intentionally.
                if (_activeReaction?.AudioEvent is not null)
                {
                    var eventName = _activeReaction.AudioEvent.ToUpperInvariant();
                    AudioGuardDebug?.Invoke($"{eventName}_REVOKED_BY_SPEECH",
                        $"transcript='{combined}'");
                    _activeReaction = null;
                    _activeReason = "conversation guard";
                    PublishRender(now);
                }
            }

            var match = _profile.States
                .SelectMany(state => state.TriggerPhrases.Select(phrase => new { State = state, Phrase = Normalize(phrase) }))
                .Where(x => PhraseMatches(normalized, combined, x.Phrase, isFinal))
                .OrderByDescending(x => x.State.Priority)
                .ThenByDescending(x => x.Phrase.Length)
                .FirstOrDefault();

            RecognitionDebug?.Invoke(text, normalized, combined, isFinal, match?.State.Id, match?.Phrase);

            if (match is null) return;

            var key = $"{match.State.Id}:{match.Phrase}";
            if (_recentPhraseTriggers.TryGetValue(key, out var last) && (now - last).TotalMilliseconds < 1000)
                return;

            _recentPhraseTriggers[key] = now;
            foreach (var stale in _recentPhraseTriggers.Where(kv => (now - kv.Value).TotalSeconds > 10).Select(kv => kv.Key).ToArray())
                _recentPhraseTriggers.Remove(stale);
            // Explicit phrases are intentional reactions. They should override inferred audio events,
            // even if an event classifier is currently sticky for a moment.
            Activate(match.State, $"heard ‘{match.Phrase}’", now, match.State.HoldMs, stickyAudio: false, force: true);
            PublishRender(now);
        }
    }

    public void RefreshRender()
    {
        lock (_sync)
        {
            _lastRender = null;
            PublishRender(DateTimeOffset.UtcNow);
        }
    }

    public void ForceState(string stateId)
    {
        lock (_sync)
        {
            var state = _profile.GetState(stateId);
            var now = DateTimeOffset.UtcNow;
            Activate(state, "manual orbit", now, _profile.Settings.ManualStateHoldMs, stickyAudio: false, force: true);
            PublishRender(now);
        }
    }

    public void ReturnToIdle()
    {
        lock (_sync)
        {
            _activeReaction = null;
            _activeReason = "idle";
            PublishRender(DateTimeOffset.UtcNow);
        }
    }

    private (ReactionState State, double Confidence)? FindStrongestAudioEvent(AudioSnapshot snapshot)
    {
        var candidates = new List<(ReactionState State, double Confidence)>();

        // Once Word Core has produced ordinary language, acoustic classifiers are not allowed
        // to override that utterance. The veto lasts while Voice/Words remains active plus a
        // short release tail. This intentionally favors a missed subtle reaction over a random
        // reaction during normal conversation.
        var speechGuardActive = _utteranceHasNormalSpeech &&
                                (snapshot.VoiceActive || snapshot.Timestamp <= _normalSpeechGuardUntil);
        var laughText = IsLaughLikeUtterance(_currentSpeechTranscript);
        var humText = IsHumLikeUtterance(_currentSpeechTranscript);

        if (speechGuardActive)
        {
            if (snapshot.HumDetected && !humText)
            {
                _humSuppressedLatch = true;
                if (!_humGuardLogged)
                {
                    _humGuardLogged = true;
                    AudioGuardDebug?.Invoke("HUM_SUPPRESSED_BY_CONVERSATION",
                        $"hum={snapshot.HumConfidence:F3}; transcript='{_currentSpeechTranscript}'");
                }
            }
            if (snapshot.YawnDetected)
            {
                _yawnSuppressedLatch = true;
                if (!_yawnGuardLogged)
                {
                    _yawnGuardLogged = true;
                    AudioGuardDebug?.Invoke("YAWN_SUPPRESSED_BY_CONVERSATION",
                        $"yawn={snapshot.YawnConfidence:F3}; transcript='{_currentSpeechTranscript}'");
                }
            }
            if (snapshot.LaughDetected && !laughText)
            {
                _laughSuppressedLatch = true;
                if (!_laughGuardLogged)
                {
                    _laughGuardLogged = true;
                    AudioGuardDebug?.Invoke("LAUGH_SUPPRESSED_BY_CONVERSATION",
                        $"laugh={snapshot.LaughConfidence:F3}; transcript='{_currentSpeechTranscript}'");
                }
            }

            // A real laugh that Word Core itself is decoding as HA HA may still react. A hum
            // explicitly decoded as a hum-like utterance may also react. Yawns intentionally
            // stay blocked once normal words are present in the same segment.
            if (laughText) AddAudioCandidate(candidates, "laugh", snapshot.LaughConfidence, snapshot.LaughDetected);
            if (humText) AddAudioCandidate(candidates, "hum", snapshot.HumConfidence, snapshot.HumDetected);
        }
        else
        {
            // Before the first recognized word, give Word Core a short chance to establish
            // whether this is speech. Real nonverbal sounds survive the arbitration window;
            // normal sentences usually produce a partial transcript first. If the cleaned
            // Voice/Words feed suppresses the nonverbal sound entirely (VoiceActive=false),
            // the raw Event Sense detector is allowed through immediately.
            var voiceAgeMs = _voiceStarted.HasValue
                ? (snapshot.Timestamp - _voiceStarted.Value).TotalMilliseconds
                : double.MaxValue;

            var humReady = !snapshot.VoiceActive || voiceAgeMs >= 650;
            var yawnReady = !snapshot.VoiceActive || voiceAgeMs >= 900;
            var laughReady = !snapshot.VoiceActive || voiceAgeMs >= 450;

            if (snapshot.HumDetected && humReady && !_humSuppressedLatch)
                AddAudioCandidate(candidates, "hum", snapshot.HumConfidence, true);
            else if (snapshot.HumDetected && _humSuppressedLatch && !_humGuardLogged)
            {
                _humGuardLogged = true;
                AudioGuardDebug?.Invoke("HUM_STALE_LOCK_BLOCKED", $"hum={snapshot.HumConfidence:F3}");
            }
            else if (snapshot.HumDetected && !_humGuardLogged)
            {
                _humGuardLogged = true;
                AudioGuardDebug?.Invoke("HUM_PENDING_WORD_CORE", $"hum={snapshot.HumConfidence:F3}; voiceAge={voiceAgeMs:F0}ms");
            }

            if (snapshot.YawnDetected && yawnReady && !_yawnSuppressedLatch)
                AddAudioCandidate(candidates, "yawn", snapshot.YawnConfidence, true);
            else if (snapshot.YawnDetected && _yawnSuppressedLatch && !_yawnGuardLogged)
            {
                _yawnGuardLogged = true;
                AudioGuardDebug?.Invoke("YAWN_STALE_LOCK_BLOCKED", $"yawn={snapshot.YawnConfidence:F3}");
            }
            else if (snapshot.YawnDetected && !_yawnGuardLogged)
            {
                _yawnGuardLogged = true;
                AudioGuardDebug?.Invoke("YAWN_PENDING_WORD_CORE", $"yawn={snapshot.YawnConfidence:F3}; voiceAge={voiceAgeMs:F0}ms");
            }

            if (snapshot.LaughDetected && laughReady && !_laughSuppressedLatch)
                AddAudioCandidate(candidates, "laugh", snapshot.LaughConfidence, true);
            else if (snapshot.LaughDetected && _laughSuppressedLatch && !_laughGuardLogged)
            {
                _laughGuardLogged = true;
                AudioGuardDebug?.Invoke("LAUGH_STALE_LOCK_BLOCKED", $"laugh={snapshot.LaughConfidence:F3}");
            }
            else if (snapshot.LaughDetected && !_laughGuardLogged)
            {
                _laughGuardLogged = true;
                AudioGuardDebug?.Invoke("LAUGH_PENDING_WORD_CORE", $"laugh={snapshot.LaughConfidence:F3}; voiceAge={voiceAgeMs:F0}ms");
            }
        }

        if (candidates.Count == 0) return null;
        return candidates
            .OrderByDescending(x => x.State.Priority)
            .ThenByDescending(x => x.Confidence)
            .First();
    }

    private void AddAudioCandidate(List<(ReactionState State, double Confidence)> candidates, string audioEvent, double confidence, bool detected)
    {
        if (!detected) return;
        var state = _profile.States.FirstOrDefault(s => string.Equals(s.AudioEvent, audioEvent, StringComparison.OrdinalIgnoreCase));
        if (state is not null) candidates.Add((state, confidence));
    }

    private static bool IsAudioEventStillAlive(string audioEvent, AudioSnapshot snapshot) => audioEvent.ToLowerInvariant() switch
    {
        "hum" => snapshot.HumDetected || snapshot.HumConfidence >= 0.28,
        "yawn" => snapshot.YawnDetected || snapshot.YawnConfidence >= 0.30,
        "laugh" => snapshot.LaughDetected || snapshot.LaughConfidence >= 0.35,
        _ => false
    };

    private int GetAudioReleaseMs(string? audioEvent) => audioEvent?.ToLowerInvariant() switch
    {
        "hum" => _profile.Settings.HumReleaseHoldMs,
        "yawn" => _profile.Settings.YawnReleaseHoldMs,
        "laugh" => _profile.Settings.LaughReleaseHoldMs,
        _ => 1200
    };

    private void Activate(ReactionState state, string reason, DateTimeOffset now, int holdMs, bool stickyAudio, bool force = false)
    {
        if (!force && _activeReaction is not null && _activeReaction.Priority > state.Priority)
        {
            if (_activeReaction.AudioEvent is not null && stickyAudio && string.Equals(_activeReaction.Id, state.Id, StringComparison.OrdinalIgnoreCase))
                _activeUntil = now.AddMilliseconds(holdMs);
            return;
        }

        var sameState = _activeReaction is not null &&
                        string.Equals(_activeReaction.Id, state.Id, StringComparison.OrdinalIgnoreCase);

        if (sameState && stickyAudio)
        {
            _activeUntil = now.AddMilliseconds(holdMs);
            return;
        }

        var changed = !sameState || !string.Equals(_activeReason, reason, StringComparison.Ordinal);
        _activeReaction = state;
        _activeUntil = now.AddMilliseconds(holdMs);
        _activeReason = reason;

        if (changed)
            LogEntryAdded?.Invoke(new ReactionLogEntry(now, state.Name.ToUpperInvariant(), reason));
    }

    private void PublishRender(DateTimeOffset now)
    {
        var state = _activeReaction ?? _profile.GetState(_profile.Settings.DefaultStateId);
        var voiceActive = _lastAudio?.VoiceActive == true;
        var gatePassed = _voiceStarted.HasValue && (now - _voiceStarted.Value).TotalMilliseconds >= TriggerLeadMs;
        var talking = _activeReaction is not null ? voiceActive : voiceActive && gatePassed;
        var asset = talking && !string.IsNullOrWhiteSpace(state.TalkAsset) ? state.TalkAsset : state.IdleAsset;
        var reason = _activeReaction is not null ? _activeReason : talking ? "voice" : "idle";

        var render = new ReactionRenderState(state.Id, state.Name, talking, asset, reason, now);
        if (_lastRender is not null &&
            string.Equals(_lastRender.StateId, render.StateId, StringComparison.OrdinalIgnoreCase) &&
            _lastRender.IsTalking == render.IsTalking &&
            string.Equals(_lastRender.AssetPath, render.AssetPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_lastRender.Reason, render.Reason, StringComparison.Ordinal))
            return;

        _lastRender = render;
        RenderChanged?.Invoke(render);
    }

    private static string Normalize(string text)
    {
        var lower = text.ToLowerInvariant().Replace("’", "'");
        lower = Regex.Replace(lower, "[^a-z0-9' ]+", " ");
        lower = Regex.Replace(lower, "\\s+", " ").Trim();

        // Common Vosk / pronunciation variants for the deliberately small trigger vocabulary.
        lower = Regex.Replace(lower, @"\b(wut|wat)\b", "what");
        lower = Regex.Replace(lower, @"\b(bra|brah)\b", "bruh");
        lower = Regex.Replace(lower, @"\b(nanny|nonny)\b", "nani");
        lower = Regex.Replace(lower, @"\b(na|nah) knee\b", "nani");
        lower = Regex.Replace(lower, @"\blego\b", "lets go");
        lower = Regex.Replace(lower, @"\blet go\b", "lets go");
        return Regex.Replace(lower, "\\s+", " ").Trim();
    }

    private static bool PhraseMatches(string currentUtterance, string transcript, string phrase, bool isFinal)
    {
        if (phrase.Length == 0) return false;

        // "bruh" is short and Vosk consistently renders Dan's pronunciation as nearby
        // one-word finals such as "vera", "berra" and "brad". Accept those only when the
        // WHOLE finalized utterance is that one token. That repairs BRUH without making a
        // normal sentence containing a name like Brad fire Deadpan from an early partial.
        if (string.Equals(phrase, "bruh", StringComparison.OrdinalIgnoreCase))
        {
            if (!isFinal) return false;
            return currentUtterance is "bruh" or "brad" or "berra" or "vera" or "bear" or "burr" or "bruhh";
        }

        // Natural laughter is frequently decoded by Vosk as spaced syllables such as
        // "ha ha ha ha ha" instead of the configured token "hahaha". Three or more HA/HAH
        // syllables are distinctive enough to fire Laugh even from a partial result, which
        // makes the reaction happen during the laugh instead of after it is over.
        if (string.Equals(phrase, "haha", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(phrase, "hahaha", StringComparison.OrdinalIgnoreCase))
        {
            var tokens = currentUtterance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var haRun = 0;
            foreach (var token in tokens)
            {
                if (token is "ha" or "hah")
                {
                    haRun++;
                    if (haRun >= 3) return true;
                }
                else
                {
                    haRun = 0;
                }
            }

            var compact = currentUtterance.Replace(" ", "", StringComparison.Ordinal);
            if (compact.Contains("hahaha", StringComparison.OrdinalIgnoreCase)) return true;
        }

        // "thinking" is common conversational language ("I was thinking we go left").
        // Only a standalone finalized "thinking" may trigger the pose; "let me think" remains
        // the intentional multi-word trigger and humming is handled by Event Sense.
        if (string.Equals(phrase, "thinking", StringComparison.OrdinalIgnoreCase))
            return isFinal && string.Equals(currentUtterance, "thinking", StringComparison.OrdinalIgnoreCase);

        // Do not fuzzy-match "hmm" to words such as "him". Audio hum sensing is the primary
        // Thinking detector; text recognition only gets to claim an explicit finalized hum.
        if (string.Equals(phrase, "hmm", StringComparison.OrdinalIgnoreCase))
            return isFinal && currentUtterance is "hmm" or "hm" or "mmm";

        // "what" is special: it is an extremely common question word, so matching it in
        // rolling/partial recognition makes normal sentences like "what are we playing today"
        // look shocked. Only treat WHAT as a reaction when Vosk has finalized the utterance
        // and the utterance itself is just the single exclamation. Punctuation/case is already
        // stripped by Normalize(), so an emphatic spoken "WHAT?!" arrives here as "what".
        if (string.Equals(phrase, "what", StringComparison.OrdinalIgnoreCase))
            return isFinal && string.Equals(currentUtterance, "what", StringComparison.OrdinalIgnoreCase);

        // "let's go" is also highly contextual. In normal speech it often means navigation
        // or planning ("let's go to this dungeon", "let's go here"). Treat it as Hype only
        // when the recognizer finalizes an utterance that is essentially just the exclamation.
        // This also prevents an early rolling partial of "let's go" from firing before the rest
        // of a longer sentence arrives.
        if (string.Equals(phrase, "let's go", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(phrase, "lets go", StringComparison.OrdinalIgnoreCase))
        {
            var utteranceNoApostrophe = currentUtterance.Replace("'", "", StringComparison.Ordinal);
            return isFinal && string.Equals(utteranceNoApostrophe, "lets go", StringComparison.OrdinalIgnoreCase);
        }

        // Sleepy vocabulary is descriptive language as often as it is a reaction cue. The
        // v0.6.5 debug log showed "I thought I yawned..." and even the recognized word "sleep"
        // firing Sleepy through rolling/fuzzy matching. Keep these cues exact and final-only;
        // actual yawns remain primarily handled by Event Sense.
        if (string.Equals(phrase, "yawn", StringComparison.OrdinalIgnoreCase))
            return isFinal && string.Equals(currentUtterance, "yawn", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(phrase, "sleepy", StringComparison.OrdinalIgnoreCase))
            return isFinal && string.Equals(currentUtterance, "sleepy", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(phrase, "i'm tired", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(phrase, "im tired", StringComparison.OrdinalIgnoreCase))
        {
            var noApostrophe = currentUtterance.Replace("'", "", StringComparison.Ordinal);
            return isFinal && string.Equals(noApostrophe, "im tired", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(transcript, phrase, StringComparison.OrdinalIgnoreCase)) return true;

        // Multi-word / distinctive phrases can still claim rolling Vosk transcripts quickly.
        if (Regex.IsMatch(transcript, $@"(^|\s){Regex.Escape(phrase)}($|\s)", RegexOptions.IgnoreCase))
            return true;

        if (phrase.Contains(' ')) return false;
        return transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(token => IsNearWord(token, phrase));
    }

    private static bool IsNearWord(string token, string phrase)
    {
        if (token.Length < 3 || phrase.Length < 3) return false;
        if (Math.Abs(token.Length - phrase.Length) > 1) return false;
        return LevenshteinDistance(token, phrase) <= 1;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static bool IsHumLikeUtterance(string text)
    {
        var compact = text.Trim();
        return compact is "hmm" or "hm" or "mmm" or "mm";
    }

    private static bool IsLaughLikeUtterance(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;

        var laughTokens = tokens.Count(token => token is "ha" or "hah" or "heh" or "hehe" or "lol");
        return laughTokens >= 2 && laughTokens >= Math.Max(2, tokens.Length - 1);
    }

    private static string KeepLastWords(string text, int count)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= count ? string.Join(" ", words) : string.Join(" ", words.Skip(words.Length - count));
    }
}
