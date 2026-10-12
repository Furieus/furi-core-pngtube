using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using FuriXeus.Tuber.Native.Models;
using FuriXeus.Tuber.Native.Services;

namespace FuriXeus.Tuber.Native;

public partial class MainWindow : Window
{
    private readonly ProfileService _profileService = new();
    private readonly AvatarAssetService _assets = new();
    private readonly ModelInstaller _modelInstaller = new();
    private readonly AudioEngine _voiceAudio = new();
    private readonly AudioEngine _eventAudio = new();
    private readonly PersonalizedEventClassifier _personalClassifier = new();
    private readonly VoskKeywordRecognizer _keywords = new();
    private readonly DebugLogService _debug = new();
    private readonly DispatcherTimer _visualTimer;
    private readonly Queue<ReactionLogEntry> _eventTape = new();

    private AppProfile? _profile;
    private ReactionEngine? _reactions;
    private OverlayWindow? _overlay;
    private AudioSnapshot? _latestAudio;
    private AudioSnapshot? _latestVoiceAudio;
    private AudioSnapshot? _latestEventAudio;
    private bool _sharedAudioInput;
    private double _orbitAngleA;
    private double _orbitAngleB;
    private bool _closing;
    private bool _lastKeywordVoiceActive;

    private Dictionary<string, Button> _stateNodes = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        BuildVersionText.Text = $"v{BuildInfo.Version} • PERSONAL CLASSIFIER";
        Title = $"FuriXeus Tuber v{BuildInfo.Version}";

        _visualTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _visualTimer.Tick += VisualTimer_Tick;

        _voiceAudio.SnapshotAvailable += snapshot =>
        {
            _latestVoiceAudio = snapshot;
            _debug.LogAudio(snapshot, "VOICE");
            if (_sharedAudioInput)
                _latestEventAudio = snapshot;

            // Force Vosk to finish short utterances the instant the VOICE/WORDS gate closes.
            // Event Sense may be listening to a different raw microphone and must not control
            // where Word Core decides an utterance ends.
            if (_lastKeywordVoiceActive && !snapshot.VoiceActive)
                _keywords.FlushUtterance();
            _lastKeywordVoiceActive = snapshot.VoiceActive;

            PublishMergedAudio();
        };
        _voiceAudio.Pcm16FrameAvailable += frame =>
        {
            _keywords.PushAudio(frame);
            if (_sharedAudioInput)
                _personalClassifier.PushAudio(frame);
        };
        _voiceAudio.Faulted += message =>
        {
            _debug.LogFault($"Voice/Words input: {message}");
            Dispatcher.BeginInvoke(() => ShowMicError($"Voice/Words input: {message}"));
        };

        _eventAudio.SnapshotAvailable += snapshot =>
        {
            _latestEventAudio = snapshot;
            _debug.LogAudio(snapshot, "EVENT");
            PublishMergedAudio();
        };
        _eventAudio.Pcm16FrameAvailable += frame => _personalClassifier.PushAudio(frame);
        _personalClassifier.ClassificationAvailable += classification => _debug.LogClassifier(classification);
        _personalClassifier.StatusChanged += status =>
        {
            _debug.LogStatus("PERSONAL_CLASSIFIER", status);
            Dispatcher.BeginInvoke(() => UpdateClassifierStatus());
        };
        _eventAudio.Faulted += message =>
        {
            _debug.LogFault($"Event Sense input: {message}");
            Dispatcher.BeginInvoke(() => ShowMicError($"Event Sense input: {message}"));
        };

        _keywords.TextObserved += (text, final) =>
        {
            _debug.LogSpeech(text, final);
            _reactions?.OnRecognizedText(text, final);
        };
        _keywords.StatusChanged += status =>
        {
            _debug.LogStatus("WORD_CORE", status);
            Dispatcher.BeginInvoke(() => UpdateModelStatus(status));
        };
        _keywords.DiagnosticObserved += (eventName, detail) =>
        {
            _debug.LogStatus($"WORD_{eventName}", detail);
            Dispatcher.BeginInvoke(() => UpdateWordHealth());
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _profile = await _profileService.LoadAsync();

            if (!_profile.Settings.SetupCompleted)
            {
                var setup = new SetupWindow(_profile, _profileService, firstRun: true) { Owner = this };
                setup.ShowDialog();
                _profile = await _profileService.LoadAsync();
            }

            _personalClassifier.NoiseFloorDb = _profile.Settings.CalibratedNoiseFloorDb;
            _personalClassifier.Reload();
            _assets.SetOverrideFolder(_profile.Settings.CharacterSkinFolder);
            _assets.SetAssetOverrides(_profile.Settings.AssetOverrides);
            UpdateSkinStatus();
            _reactions = new ReactionEngine(_profile);
            _reactions.RenderChanged += render =>
            {
                _debug.LogRender(render);
                Dispatcher.BeginInvoke(() => ApplyRender(render));
            };
            _reactions.LogEntryAdded += entry =>
            {
                _debug.LogReaction(entry);
                Dispatcher.BeginInvoke(() => AddEventTapeEntry(entry));
            };
            _reactions.RecognitionDebug += (raw, normalized, combined, final, matchedState, matchedPhrase) =>
                _debug.LogRecognitionDecision(raw, normalized, combined, final, matchedState, matchedPhrase);
            _reactions.AudioGuardDebug += (eventName, detail) =>
                _debug.LogEventGuard(eventName, detail);
            _reactions.TranscriptChanged += (text, final) => Dispatcher.BeginInvoke(() =>
            {
                TranscriptText.Text = $"heard: {text}{(final ? "  [final]" : "")}";
            });

            _stateNodes = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase)
            {
                ["idle"] = IdleNode,
                ["thinking"] = ThinkingNode,
                ["deadpan"] = DeadpanNode,
                ["shocked"] = ShockedNode,
                ["hype"] = HypeNode,
                ["rage"] = RageNode,
                ["sleepy"] = SleepyNode,
                ["laugh"] = LaughNode
            };

            TriggerLeadSlider.Value = _profile.Settings.TriggerLeadMs;
            ThresholdSlider.Value = _profile.Settings.TalkThresholdDb;
            ConfigureAudioEngine(_voiceAudio);
            ConfigureAudioEngine(_eventAudio);
            _reactions.TriggerLeadMs = _profile.Settings.TriggerLeadMs;

            var devices = AudioEngine.GetInputDevices();
            VoiceMicComboBox.ItemsSource = devices;
            EventMicComboBox.ItemsSource = devices;
            SelectPreferredAudioRoutes(devices);

            if (ModelInstaller.IsInstalled)
                _keywords.Initialize(ModelInstaller.ModelPath, _profile.GetAllPhrases());
            RefreshModelState();
            UpdateWordHealth();

            _reactions.ReturnToIdle();
            _visualTimer.Start();
            _debug.LogStatus("CORE_READY", $"native reaction engine loaded • {_personalClassifier.Status}");
            AddEventTapeEntry(new ReactionLogEntry(DateTimeOffset.Now, "CORE READY", _personalClassifier.Status));
            UpdateClassifierStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "FuriXeus Tuber failed to start", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async void ArmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceAudio.IsRunning || _eventAudio.IsRunning)
        {
            StopAudioRouting();
            ArmButton.Content = "ARM CORE";
            CoreStatusText.Text = "CORE IDLE";
            CoreStatusDot.Fill = new SolidColorBrush(Color.FromRgb(91, 86, 99));
            VoiceMicComboBox.IsEnabled = true;
            EventMicComboBox.IsEnabled = true;
            _reactions?.ReturnToIdle();
            _debug.LogAction("DISARMED", "voice + event capture stopped");
            AddEventTapeEntry(new ReactionLogEntry(DateTimeOffset.Now, "DISARMED", "audio routing stopped"));
            return;
        }

        if (VoiceMicComboBox.SelectedItem is not AudioDeviceInfo voiceDevice)
        {
            ShowMicError("No Voice / Words input is selected.");
            return;
        }

        if (EventMicComboBox.SelectedItem is not AudioDeviceInfo eventDevice)
        {
            ShowMicError("No Event Sense input is selected.");
            return;
        }

        if (!ModelInstaller.IsInstalled)
        {
            var choice = MessageBox.Show(
                this,
                "Word Core is not installed, so phrase triggers like BRUH, WHAT, NANI and LET'S GO cannot work.\n\nInstall the local Word Core now? (~40 MB download)",
                "FURI//CORE • Word Core required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (choice == MessageBoxResult.Yes)
            {
                if (!await EnsureWordCoreReadyAsync())
                    return;
            }
            else
            {
                ModelStatusText.Text = "WORDS OFFLINE • TRIGGERS DISABLED";
                WordHealthText.Text = "word core • NOT INSTALLED • phrase triggers disabled";
                _debug.LogStatus("WORD_CORE_BYPASSED", "user armed microphone without Word Core");
            }
        }
        else if (!_keywords.IsReady)
        {
            _keywords.Initialize(ModelInstaller.ModelPath, _profile!.GetAllPhrases());
            if (!_keywords.IsReady)
            {
                ShowMicError($"Word Core files are present but could not be armed. {_keywords.Status}");
                return;
            }
        }

        try
        {
            MicErrorText.Text = "";
            ConfigureAudioEngine(_voiceAudio);
            ConfigureAudioEngine(_eventAudio);
            _voiceAudio.TalkThresholdDb = ThresholdSlider.Value;
            _eventAudio.TalkThresholdDb = ThresholdSlider.Value;
            _reactions!.TriggerLeadMs = (int)TriggerLeadSlider.Value;
            if (ModelInstaller.IsInstalled && !_keywords.IsReady && _profile is not null)
                _keywords.Initialize(ModelInstaller.ModelPath, _profile.GetAllPhrases());

            _sharedAudioInput = string.Equals(voiceDevice.DeviceId, eventDevice.DeviceId, StringComparison.OrdinalIgnoreCase);
            _latestVoiceAudio = null;
            _latestEventAudio = null;
            _lastKeywordVoiceActive = false;

            var route = $"voice='{voiceDevice.Name}' | event='{eventDevice.Name}' | shared={_sharedAudioInput}";
            _debug.LogAction("ARM_REQUEST", route);

            // Start Event Sense first when split so the raw detector is already warm by the time
            // Word Core/talking starts receiving frames. Shared mode uses only one WASAPI capture.
            if (!_sharedAudioInput)
                _eventAudio.Start(eventDevice.DeviceId);
            _voiceAudio.Start(voiceDevice.DeviceId);

            ArmButton.Content = "DISARM";
            CoreStatusText.Text = _sharedAudioInput ? "CORE ARMED • SHARED" : "CORE ARMED • SPLIT";
            CoreStatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 45, 139));
            VoiceMicComboBox.IsEnabled = false;
            EventMicComboBox.IsEnabled = false;
            RouteHintText.Text = _sharedAudioInput
                ? "shared input • words + events on one mic"
                : "split route • clean words + raw event sense";
            _debug.LogAction("AUDIO_ROUTE", route);
            _debug.LogAction("ARMED", route);
            AddEventTapeEntry(new ReactionLogEntry(DateTimeOffset.Now, "ARMED", _sharedAudioInput ? "shared audio route" : "split audio route"));
        }
        catch (Exception ex)
        {
            StopAudioRouting();
            VoiceMicComboBox.IsEnabled = true;
            EventMicComboBox.IsEnabled = true;
            ShowMicError(ex.Message);
        }
    }

    private void ConfigureAudioEngine(AudioEngine engine)
    {
        if (_profile is null) return;
        engine.TalkThresholdDb = _profile.Settings.TalkThresholdDb;
        engine.TalkReleaseMs = _profile.Settings.TalkReleaseMs;
        engine.HumAttackMs = _profile.Settings.HumAttackMs;
        engine.YawnAttackMs = _profile.Settings.YawnAttackMs;
    }

    private void StopAudioRouting()
    {
        _voiceAudio.Stop();
        _eventAudio.Stop();
        _sharedAudioInput = false;
        _latestVoiceAudio = null;
        _latestEventAudio = null;
        _latestAudio = null;
        _lastKeywordVoiceActive = false;
        _personalClassifier.ResetLiveState();
    }

    private void PublishMergedAudio()
    {
        var voice = _latestVoiceAudio;
        if (voice is null) return;

        var events = _sharedAudioInput ? voice : _latestEventAudio;
        if (events is null) return;

        var timestamp = voice.Timestamp >= events.Timestamp ? voice.Timestamp : events.Timestamp;
        var personal = _personalClassifier.LastClassification;
        var personalFresh = _personalClassifier.IsReady && personal is not null &&
                            (timestamp - personal.Timestamp).Duration().TotalMilliseconds <= 450;

        // Once a personalized model is ready, it completely owns nonverbal event decisions.
        // Do not fall back to the old burst heuristic during its first second of warm-up: that
        // was enough for keyboard typing to flash Laugh before the personal classifier spoke.
        var humConfidence = _personalClassifier.IsReady ? (personalFresh ? personal!.HmmScore : 0.0) : events.HumConfidence;
        var yawnConfidence = _personalClassifier.IsReady ? (personalFresh ? personal!.YawnScore : 0.0) : events.YawnConfidence;
        var laughConfidence = _personalClassifier.IsReady ? (personalFresh ? personal!.LaughScore : 0.0) : events.LaughConfidence;
        var humDetected = _personalClassifier.IsReady ? (personalFresh && personal!.HmmDetected) : events.HumDetected;
        var yawnDetected = _personalClassifier.IsReady ? (personalFresh && personal!.YawnDetected) : events.YawnDetected;
        var laughDetected = _personalClassifier.IsReady ? (personalFresh && personal!.LaughDetected) : events.LaughDetected;

        var merged = new AudioSnapshot(
            timestamp,
            voice.Rms,
            voice.Db,
            voice.Peak,
            voice.PitchHz,
            voice.PitchClarity,
            voice.ZeroCrossingRate,
            voice.VoiceActive,
            humConfidence,
            yawnConfidence,
            laughConfidence,
            humDetected,
            yawnDetected,
            laughDetected,
            voice.Waveform);

        _latestAudio = merged;
        _debug.LogAudio(merged);
        _reactions?.OnAudio(merged);
        Dispatcher.BeginInvoke(() => UpdateTelemetry(merged));
    }

    private void SelectPreferredAudioRoutes(IReadOnlyList<AudioDeviceInfo> devices)
    {
        if (devices.Count == 0) return;

        AudioDeviceInfo? voice = null;
        AudioDeviceInfo? events = null;
        var settings = _profile?.Settings;

        if (!string.IsNullOrWhiteSpace(settings?.VoiceInputDeviceId))
            voice = devices.FirstOrDefault(d => string.Equals(d.DeviceId, settings.VoiceInputDeviceId, StringComparison.OrdinalIgnoreCase));
        if (voice is null && !string.IsNullOrWhiteSpace(settings?.VoiceInputDeviceName))
            voice = devices.FirstOrDefault(d => string.Equals(d.Name, settings.VoiceInputDeviceName, StringComparison.OrdinalIgnoreCase));
        voice ??= devices.FirstOrDefault(d => d.Name.Contains("Krisp", StringComparison.OrdinalIgnoreCase));
        voice ??= devices.FirstOrDefault(d => d.IsDefault);
        voice ??= devices[0];

        if (!string.IsNullOrWhiteSpace(settings?.EventInputDeviceId))
            events = devices.FirstOrDefault(d => string.Equals(d.DeviceId, settings.EventInputDeviceId, StringComparison.OrdinalIgnoreCase));
        if (events is null && !string.IsNullOrWhiteSpace(settings?.EventInputDeviceName))
            events = devices.FirstOrDefault(d => string.Equals(d.Name, settings.EventInputDeviceName, StringComparison.OrdinalIgnoreCase));

        // Event Sense wants the physical/raw feed. Prefer a non-Krisp default endpoint, then any
        // non-Krisp capture device. The user can always override this explicitly.
        events ??= devices.FirstOrDefault(d => d.IsDefault && !d.Name.Contains("Krisp", StringComparison.OrdinalIgnoreCase));
        events ??= devices.FirstOrDefault(d => !d.Name.Contains("Krisp", StringComparison.OrdinalIgnoreCase));
        events ??= voice;

        VoiceMicComboBox.SelectedItem = voice;
        EventMicComboBox.SelectedItem = events;
        UpdateRouteHint();
    }

    private void AudioRoute_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRouteHint();

    private void UpdateRouteHint()
    {
        if (RouteHintText is null) return;
        if (VoiceMicComboBox?.SelectedItem is not AudioDeviceInfo voice || EventMicComboBox?.SelectedItem is not AudioDeviceInfo events)
        {
            RouteHintText.Text = "voice → words/talk • event → hum/yawn/laugh";
            return;
        }

        RouteHintText.Text = string.Equals(voice.DeviceId, events.DeviceId, StringComparison.OrdinalIgnoreCase)
            ? "shared input • choose raw Event Sense for best yawns/laughs"
            : "split route • Voice/Words cleaned • Event Sense raw";
    }

    private async void TrainerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        if (_voiceAudio.IsRunning || _eventAudio.IsRunning)
        {
            ShowMicError("Disarm the core before opening Reaction Trainer.");
            return;
        }

        var trainer = new SetupWindow(_profile, _profileService, firstRun: false) { Owner = this };
        trainer.ShowDialog();
        _profile = await _profileService.LoadAsync();

        ThresholdSlider.Value = _profile.Settings.TalkThresholdDb;
        TriggerLeadSlider.Value = _profile.Settings.TriggerLeadMs;
        var devices = AudioEngine.GetInputDevices();
        VoiceMicComboBox.ItemsSource = devices;
        EventMicComboBox.ItemsSource = devices;
        SelectPreferredAudioRoutes(devices);
        ConfigureAudioEngine(_voiceAudio);
        ConfigureAudioEngine(_eventAudio);
        _personalClassifier.NoiseFloorDb = _profile.Settings.CalibratedNoiseFloorDb;
        _personalClassifier.Reload();
        UpdateClassifierStatus();
        _debug.LogAction("TRAINER_CLOSED", $"setupComplete={_profile.Settings.SetupCompleted}; floor={_profile.Settings.CalibratedNoiseFloorDb:0.0} dB; classifier={_personalClassifier.Status}");
    }

    private async void SkinFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;

        var dialog = new OpenFolderDialog
        {
            Title = "Choose a FURI//CORE skin folder",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(_assets.OverrideFolder) && Directory.Exists(_assets.OverrideFolder))
            dialog.InitialDirectory = _assets.OverrideFolder;

        if (dialog.ShowDialog(this) != true) return;

        _assets.SetOverrideFolder(dialog.FolderName);
        _profile.Settings.CharacterSkinFolder = _assets.OverrideFolder ?? "";
        _profile.Settings.AssetOverrides = new Dictionary<string, string>(_assets.AssetOverrides, StringComparer.OrdinalIgnoreCase);
        await _profileService.SaveAsync(_profile);
        UpdateSkinStatus();
        _reactions?.RefreshRender();
        _debug.LogAction("SKIN_CHANGED", _assets.OverrideFolder ?? "built-in");
        AddEventTapeEntry(new ReactionLogEntry(DateTimeOffset.Now, "SKIN", System.IO.Path.GetFileName(_assets.OverrideFolder) ?? "built-in"));
    }

    private void AssetManagerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;

        var manager = new AssetManagerWindow(_profile, _profileService, _assets, () =>
        {
            UpdateSkinStatus();
            _reactions?.RefreshRender();
            _debug.LogAction("ASSET_MANAGER_CHANGED", $"folder={_assets.OverrideFolder ?? "built-in"}; fileOverrides={_assets.AssetOverrides.Count}");
        })
        {
            Owner = this
        };
        manager.ShowDialog();
    }

    private void ReloadSkinButton_Click(object sender, RoutedEventArgs e)
    {
        _assets.Reload();
        UpdateSkinStatus();
        _reactions?.RefreshRender();
        _debug.LogAction("SKIN_RELOADED", _assets.OverrideFolder ?? "built-in");
    }

    private async void DefaultSkinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        _assets.SetOverrideFolder(null);
        _assets.ClearAllAssetOverrides();
        _profile.Settings.CharacterSkinFolder = "";
        _profile.Settings.AssetOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await _profileService.SaveAsync(_profile);
        UpdateSkinStatus();
        _reactions?.RefreshRender();
        _debug.LogAction("SKIN_CHANGED", "built-in; all file overrides cleared");
    }

    private void UpdateSkinStatus()
    {
        if (SkinStatusText is null) return;
        var customCount = _assets.AssetOverrides.Count;
        var baseName = string.IsNullOrWhiteSpace(_assets.OverrideFolder)
            ? "BUILT-IN CHAOS CRITTER"
            : System.IO.Path.GetFileName(_assets.OverrideFolder);
        SkinStatusText.Text = customCount > 0
            ? $"skin • {baseName} • {customCount} custom"
            : $"skin • {baseName}";
        SkinStatusText.ToolTip = $"Base: {_assets.OverrideFolder ?? "Built-in Assets/ChaosCritter"}\nPer-file overrides: {customCount}";
    }

    private void OverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay is null || !_overlay.IsLoaded)
        {
            _overlay = new OverlayWindow();
            _overlay.Closed += (_, _) => _overlay = null;
            _overlay.Show();
            _debug.LogAction("OUTPUT_OPENED", "FuriXeus Critter Output");
            if (AvatarImage.Source is BitmapImage current)
                _overlay.SetImage(current);
        }
        else
        {
            _overlay.Activate();
        }
    }

    private async void InstallModelButton_Click(object sender, RoutedEventArgs e)
    {
        await EnsureWordCoreReadyAsync(forceReinstall: ModelInstaller.IsInstalled);
    }

    private async Task<bool> EnsureWordCoreReadyAsync(bool forceReinstall = false)
    {
        if (_profile is null) return false;

        if (!forceReinstall && ModelInstaller.IsInstalled)
        {
            if (!_keywords.IsReady)
                _keywords.Initialize(ModelInstaller.ModelPath, _profile.GetAllPhrases());
            RefreshModelState();
            UpdateWordHealth();
            return _keywords.IsReady;
        }

        InstallModelButton.IsEnabled = false;
        InstallProgressText.Text = forceReinstall
            ? "repairing local word core…"
            : "downloading local word core…";
        ModelStatusText.Text = "WORD CORE INSTALLING…";
        ModelStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 138, 31));
        _debug.LogAction(forceReinstall ? "WORD_CORE_REPAIR" : "WORD_CORE_INSTALL", "download started");

        try
        {
            var progress = new Progress<double>(p => InstallProgressText.Text = $"word core: {p:P0}");
            await _modelInstaller.InstallAsync(progress);

            if (!ModelInstaller.IsInstalled)
                throw new InvalidDataException($"Word Core download finished, but validation failed: {ModelInstaller.InstallationDetail}");

            _keywords.Initialize(ModelInstaller.ModelPath, _profile.GetAllPhrases());
            if (!_keywords.IsReady)
                throw new InvalidOperationException(_keywords.Status);

            InstallProgressText.Text = "word core installed • local/offline • ready";
            _debug.LogAction("WORD_CORE_INSTALLED", ModelInstaller.ModelPath);
            AddEventTapeEntry(new ReactionLogEntry(DateTimeOffset.Now, "WORD CORE READY", "phrase triggers are armed"));
            RefreshModelState();
            UpdateWordHealth();
            return true;
        }
        catch (Exception ex)
        {
            InstallProgressText.Text = $"install failed: {ex.Message}";
            ModelStatusText.Text = "WORD CORE FAILED";
            ModelStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 109, 139));
            _debug.LogFault($"Word core install failed: {ex}");
            MessageBox.Show(this,
                $"Word Core could not be installed or armed.\n\n{ex.Message}\n\nPhrase triggers will remain disabled until this is fixed.",
                "FURI//CORE • Word Core error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            InstallModelButton.IsEnabled = true;
        }
    }

    private async void SaveTuningButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null || _reactions is null) return;
        _profile.Settings.TriggerLeadMs = (int)TriggerLeadSlider.Value;
        _profile.Settings.TalkThresholdDb = ThresholdSlider.Value;
        _voiceAudio.TalkThresholdDb = ThresholdSlider.Value;
        _eventAudio.TalkThresholdDb = ThresholdSlider.Value;
        _reactions.TriggerLeadMs = (int)TriggerLeadSlider.Value;
        if (VoiceMicComboBox.SelectedItem is AudioDeviceInfo voiceDevice)
        {
            _profile.Settings.VoiceInputDeviceId = voiceDevice.DeviceId;
            _profile.Settings.VoiceInputDeviceName = voiceDevice.Name;
        }
        if (EventMicComboBox.SelectedItem is AudioDeviceInfo eventDevice)
        {
            _profile.Settings.EventInputDeviceId = eventDevice.DeviceId;
            _profile.Settings.EventInputDeviceName = eventDevice.Name;
        }
        _profile.Settings.CharacterSkinFolder = _assets.OverrideFolder ?? "";
        _profile.Settings.AssetOverrides = new Dictionary<string, string>(_assets.AssetOverrides, StringComparer.OrdinalIgnoreCase);
        await _profileService.SaveAsync(_profile);
        _debug.LogAction("TUNING_SAVED", $"lead {_profile.Settings.TriggerLeadMs} ms • threshold {_profile.Settings.TalkThresholdDb:F0} dB");
        AddEventTapeEntry(new ReactionLogEntry(DateTimeOffset.Now, "TUNING SAVED", $"lead {_profile.Settings.TriggerLeadMs} ms • {_profile.Settings.TalkThresholdDb:F0} dB"));
    }

    private void StateNode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string stateId }) return;
        _debug.LogAction("MANUAL_STATE", stateId);
        if (string.Equals(stateId, "idle", StringComparison.OrdinalIgnoreCase))
            _reactions?.ReturnToIdle();
        else
            _reactions?.ForceState(stateId);
    }

    private void TuningSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TriggerLeadValue is null || ThresholdValue is null) return;
        TriggerLeadValue.Text = $"{TriggerLeadSlider.Value:F0} ms";
        ThresholdValue.Text = $"{ThresholdSlider.Value:F0} dB";
        if (_reactions is not null) _reactions.TriggerLeadMs = (int)TriggerLeadSlider.Value;
        _voiceAudio.TalkThresholdDb = ThresholdSlider.Value;
        _eventAudio.TalkThresholdDb = ThresholdSlider.Value;
    }

    private void ApplyRender(ReactionRenderState render)
    {
        try
        {
            var bitmap = _assets.Load(render.AssetPath);
            AvatarImage.Source = bitmap;
            _overlay?.SetImage(bitmap);
            CurrentStateText.Text = render.StateName.ToUpperInvariant();
            CurrentReasonText.Text = render.IsTalking ? $"{render.Reason} • talking" : render.Reason;
            HighlightStateNode(render.StateId);
        }
        catch (Exception ex)
        {
            ShowMicError(ex.Message);
        }
    }

    private void HighlightStateNode(string stateId)
    {
        foreach (var (id, button) in _stateNodes)
        {
            var active = string.Equals(id, stateId, StringComparison.OrdinalIgnoreCase);
            button.Background = new SolidColorBrush(active ? Color.FromRgb(255, 45, 139) : Color.FromRgb(21, 20, 28));
            button.BorderBrush = new SolidColorBrush(active ? Color.FromRgb(255, 138, 31) : Color.FromRgb(51, 47, 61));
            button.Foreground = new SolidColorBrush(active ? Colors.White : Color.FromRgb(247, 245, 250));
        }
    }

    private void UpdateTelemetry(AudioSnapshot snapshot)
    {
        DbText.Text = $"{snapshot.Db:F1} dB";
        if (EventSignalText is not null)
        {
            var eventDb = (_sharedAudioInput ? _latestVoiceAudio : _latestEventAudio)?.Db;
            var mode = _personalClassifier.IsReady ? "PERSONAL" : "GENERIC";
            var classified = _personalClassifier.LastClassification;
            var classText = _personalClassifier.IsReady && classified is not null
                ? $" • {classified.TopLabel.ToUpperInvariant()} {classified.TopScore:P0}"
                : "";
            EventSignalText.Text = eventDb.HasValue
                ? $"raw mic • {eventDb.Value:F1} dB • {mode}{classText}"
                : $"raw mic • waiting • {mode}";
        }
        HumBar.Width = 292 * Math.Clamp(snapshot.HumConfidence, 0, 1);
        YawnBar.Width = 292 * Math.Clamp(snapshot.YawnConfidence, 0, 1);
        LaughBar.Width = 292 * Math.Clamp(snapshot.LaughConfidence, 0, 1);
        HumSenseText.Text = $"HUM / THINKING  {snapshot.HumConfidence:P0}{(snapshot.HumDetected ? "  • LOCK" : "")}";
        YawnSenseText.Text = $"YAWN / SLEEPY  {snapshot.YawnConfidence:P0}{(snapshot.YawnDetected ? "  • LOCK" : "")}";
        LaughSenseText.Text = $"LAUGH / BURST  {snapshot.LaughConfidence:P0}{(snapshot.LaughDetected ? "  • LOCK" : "")}";
        HumSenseText.Foreground = new SolidColorBrush(snapshot.HumDetected ? Color.FromRgb(255, 138, 31) : Color.FromRgb(168, 163, 174));
        YawnSenseText.Foreground = new SolidColorBrush(snapshot.YawnDetected ? Color.FromRgb(255, 45, 139) : Color.FromRgb(168, 163, 174));
        LaughSenseText.Foreground = new SolidColorBrush(snapshot.LaughDetected ? Color.FromRgb(255, 138, 31) : Color.FromRgb(168, 163, 174));

        var width = Math.Max(280, WaveCanvas.ActualWidth);
        var height = Math.Max(80, WaveCanvas.ActualHeight);
        var points = new PointCollection();
        for (var i = 0; i < snapshot.Waveform.Length; i++)
        {
            var x = i * width / Math.Max(1, snapshot.Waveform.Length - 1);
            var amplitude = Math.Clamp(snapshot.Waveform[i] * 3.2, 0, 1);
            var direction = i % 2 == 0 ? 1 : -1;
            var y = height / 2 + direction * amplitude * height * 0.42;
            points.Add(new Point(x, y));
        }
        WaveformLine.Points = points;
    }


    private void UpdateClassifierStatus()
    {
        if (EventSignalText is null) return;
        var eventDb = (_sharedAudioInput ? _latestVoiceAudio : _latestEventAudio)?.Db;
        var mode = _personalClassifier.IsReady ? "PERSONAL" : "GENERIC";
        var classified = _personalClassifier.LastClassification;
        var classText = _personalClassifier.IsReady && classified is not null
            ? $" • {classified.TopLabel.ToUpperInvariant()} {classified.TopScore:P0}"
            : "";
        EventSignalText.Text = eventDb.HasValue
            ? $"raw mic • {eventDb.Value:F1} dB • {mode}{classText}"
            : $"raw mic • waiting • {mode}";
    }

    private void VisualTimer_Tick(object? sender, EventArgs e)
    {
        _orbitAngleA = (_orbitAngleA + 0.18) % 360;
        _orbitAngleB = (_orbitAngleB - 0.12) % 360;
        OrbitRotateA.Angle = _orbitAngleA;
        OrbitRotateB.Angle = _orbitAngleB;

        var db = _latestAudio?.Db ?? -90;
        var pulse = Math.Clamp((db + 55) / 38, 0, 1);
        PulseRingOuter.Opacity = 0.12 + pulse * 0.62;
        PulseRingInner.Opacity = 0.08 + pulse * 0.46;
        PulseRingOuter.StrokeThickness = 1.5 + pulse * 3.5;
        PulseRingInner.StrokeThickness = 1.0 + pulse * 2.5;
    }

    private void AddEventTapeEntry(ReactionLogEntry entry)
    {
        _eventTape.Enqueue(entry);
        while (_eventTape.Count > 7) _eventTape.Dequeue();

        EventTapePanel.Children.Clear();
        foreach (var item in _eventTape.Reverse())
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = item.Label, FontSize = 10, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas") });
            stack.Children.Add(new TextBlock { Text = item.Detail, FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(132, 126, 140)), FontFamily = new FontFamily("Consolas"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 175 });

            var chip = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromRgb(20, 19, 27)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(47, 43, 55)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(13, 8, 13, 8),
                Margin = new Thickness(0, 0, 8, 0),
                Child = stack,
                MinWidth = 135
            };
            EventTapePanel.Children.Add(chip);
        }
    }

    private void RefreshModelState()
    {
        if (ModelInstaller.IsInstalled)
        {
            ModelStatusText.Text = _keywords.IsReady ? "WORDS ARMED" : "MODEL READY • NOT ARMED";
            ModelStatusText.Foreground = new SolidColorBrush(_keywords.IsReady ? Color.FromRgb(255, 138, 31) : Color.FromRgb(255, 109, 139));
            InstallModelButton.Content = _keywords.IsReady ? "REINSTALL WORD CORE" : "REPAIR / ARM WORD CORE";
            InstallModelButton.Background = new SolidColorBrush(Color.FromRgb(28, 27, 37));
        }
        else
        {
            ModelStatusText.Text = "WORDS OFFLINE • TRIGGERS DISABLED";
            ModelStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 109, 139));
            InstallModelButton.Content = "INSTALL WORD CORE";
            InstallModelButton.Background = new SolidColorBrush(Color.FromRgb(93, 20, 55));
            InstallProgressText.Text = "required for BRUH / WHAT / NANI / LET'S GO triggers";
        }
        UpdateWordHealth();
    }

    private void UpdateModelStatus(string status)
    {
        ModelStatusText.Text = status;
        ModelStatusText.Foreground = new SolidColorBrush(status.Contains("ARMED", StringComparison.OrdinalIgnoreCase)
            ? Color.FromRgb(255, 138, 31)
            : Color.FromRgb(131, 125, 138));
        UpdateWordHealth();
    }

    private void UpdateWordHealth()
    {
        if (WordHealthText is null) return;
        WordHealthText.Text = ModelInstaller.IsInstalled
            ? $"word core • {_keywords.HealthSummary}"
            : $"word core • OFFLINE • {ModelInstaller.InstallationDetail}";
        WordHealthText.Foreground = new SolidColorBrush(_keywords.IsReady
            ? Color.FromRgb(143, 137, 150)
            : Color.FromRgb(255, 109, 139));
    }

    private void ShowMicError(string message)
    {
        MicErrorText.Text = message;
        _debug.LogFault(message);
        AddEventTapeEntry(new ReactionLogEntry(DateTimeOffset.Now, "FAULT", message));
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;

        try
        {
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if the mouse state changes between the preview event and this call.
        }
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Button or ComboBox or ComboBoxItem or Slider or ScrollViewer)
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void DebugLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_debug.IsEnabled)
        {
            var path = _debug.CurrentLogPath;
            _debug.Stop();
            DebugLogButton.Content = "DEBUG OFF";
            DebugLogButton.Background = new SolidColorBrush(Color.FromRgb(28, 27, 37));
            AddEventTapeEntry(new ReactionLogEntry(DateTimeOffset.Now, "DEBUG OFF", path ?? "log stopped"));
            return;
        }

        var logPath = _debug.Start(_profile);
        _debug.LogStatus("WORD_CORE_SNAPSHOT", $"status='{_keywords.Status}'; installed={ModelInstaller.IsInstalled}; modelPath='{ModelInstaller.ModelPath}'; {_keywords.HealthSummary}");
        _debug.LogStatus("PERSONAL_CLASSIFIER_SNAPSHOT", $"status='{_personalClassifier.Status}'; {_personalClassifier.HealthSummary}");
        if (VoiceMicComboBox.SelectedItem is AudioDeviceInfo voiceDevice && EventMicComboBox.SelectedItem is AudioDeviceInfo eventDevice)
            _debug.LogStatus("AUDIO_ROUTE_SNAPSHOT", $"voice='{voiceDevice.Name}' | event='{eventDevice.Name}' | shared={string.Equals(voiceDevice.DeviceId, eventDevice.DeviceId, StringComparison.OrdinalIgnoreCase)}");
        DebugLogButton.Content = "DEBUG ON";
        DebugLogButton.Background = new SolidColorBrush(Color.FromRgb(255, 45, 139));
        AddEventTapeEntry(new ReactionLogEntry(DateTimeOffset.Now, "DEBUG ON", logPath));
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DebugLogService.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = DebugLogService.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowMicError($"Could not open logs folder: {ex.Message}");
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closed(object? sender, EventArgs e)
    {
        if (_closing) return;
        _closing = true;
        _visualTimer.Stop();
        _voiceAudio.Dispose();
        _eventAudio.Dispose();
        _lastKeywordVoiceActive = false;
        _debug.Dispose();
        if (_overlay is not null) _overlay.Close();
        await _keywords.DisposeAsync();
    }
}
