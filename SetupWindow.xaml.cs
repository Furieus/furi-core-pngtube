using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FuriXeus.Tuber.Native.Models;
using FuriXeus.Tuber.Native.Services;

namespace FuriXeus.Tuber.Native;

public partial class SetupWindow : Window
{
    private readonly AppProfile _profile;
    private readonly ProfileService _profileService;
    private readonly bool _firstRun;
    private readonly AudioEngine _calibrationAudio = new();
    private readonly TrainingRecorder _recorder = new();
    private readonly DispatcherTimer _calibrationTimer;
    private readonly DispatcherTimer _recordTimer;
    private readonly List<double> _roomLevels = [];

    private int _pageIndex;
    private DateTimeOffset _calibrationStarted;
    private bool _calibrating;
    private DateTimeOffset _recordStarted;
    private double _recordMaxSeconds;
    private string _recordCategory = "";
    private Button? _recordButton;

    private readonly Border[] _stepCards;
    private readonly TextBlock[] _stepDots;
    private readonly Grid[] _pages;

    public SetupWindow(AppProfile profile, ProfileService profileService, bool firstRun = false)
    {
        InitializeComponent();
        _profile = profile;
        _profileService = profileService;
        _firstRun = firstRun;

        _pages = [PageWelcome, PageRouting, PageRoom, PageTraining, PageFinish];
        _stepCards = [Step0Card, Step1Card, Step2Card, Step3Card, Step4Card];
        _stepDots = [Step0Dot, Step1Dot, Step2Dot, Step3Dot, Step4Dot];

        _calibrationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _calibrationTimer.Tick += CalibrationTimer_Tick;
        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _recordTimer.Tick += RecordTimer_Tick;

        _calibrationAudio.SnapshotAvailable += snapshot =>
        {
            if (!_calibrating) return;
            lock (_roomLevels)
                _roomLevels.Add(snapshot.Db);
            Dispatcher.BeginInvoke(() => RoomDbText.Text = $"{snapshot.Db:0.0} dB");
        };
        _calibrationAudio.Faulted += message => Dispatcher.BeginInvoke(() =>
        {
            FooterStatusText.Text = message;
            StopCalibration(false);
        });
        _recorder.Faulted += message => Dispatcher.BeginInvoke(() =>
        {
            TrainingStatusText.Text = message;
            StopTrainingRecording(false);
        });
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var devices = AudioEngine.GetInputDevices();
        SetupVoiceMicComboBox.ItemsSource = devices;
        SetupEventMicComboBox.ItemsSource = devices;
        SelectPreferredRoutes(devices);
        GenericModeButton.Content = _firstRun ? "USE GENERIC MODE" : "CLOSE TRAINER";
        NoiseFloorText.Text = _profile.Settings.CalibratedNoiseFloorDb > -59.9
            ? $"saved floor • {_profile.Settings.CalibratedNoiseFloorDb:0.0} dB"
            : "noise floor • not calibrated";
        RefreshTrainingCounts();
        ShowPage(0);
    }

    private void SelectPreferredRoutes(IReadOnlyList<AudioDeviceInfo> devices)
    {
        if (devices.Count == 0)
        {
            RoutingHintText.Text = "No active capture devices found.";
            return;
        }

        AudioDeviceInfo? voice = null;
        AudioDeviceInfo? events = null;
        var settings = _profile.Settings;

        if (!string.IsNullOrWhiteSpace(settings.VoiceInputDeviceId))
            voice = devices.FirstOrDefault(d => string.Equals(d.DeviceId, settings.VoiceInputDeviceId, StringComparison.OrdinalIgnoreCase));
        if (voice is null && !string.IsNullOrWhiteSpace(settings.VoiceInputDeviceName))
            voice = devices.FirstOrDefault(d => string.Equals(d.Name, settings.VoiceInputDeviceName, StringComparison.OrdinalIgnoreCase));
        voice ??= devices.FirstOrDefault(d => d.Name.Contains("Krisp", StringComparison.OrdinalIgnoreCase));
        voice ??= devices.FirstOrDefault(d => d.IsDefault);
        voice ??= devices[0];

        if (!string.IsNullOrWhiteSpace(settings.EventInputDeviceId))
            events = devices.FirstOrDefault(d => string.Equals(d.DeviceId, settings.EventInputDeviceId, StringComparison.OrdinalIgnoreCase));
        if (events is null && !string.IsNullOrWhiteSpace(settings.EventInputDeviceName))
            events = devices.FirstOrDefault(d => string.Equals(d.Name, settings.EventInputDeviceName, StringComparison.OrdinalIgnoreCase));
        events ??= devices.FirstOrDefault(d => d.IsDefault && !d.Name.Contains("Krisp", StringComparison.OrdinalIgnoreCase));
        events ??= devices.FirstOrDefault(d => !d.Name.Contains("Krisp", StringComparison.OrdinalIgnoreCase));
        events ??= voice;

        SetupVoiceMicComboBox.SelectedItem = voice;
        SetupEventMicComboBox.SelectedItem = events;
        RoutingHintText.Text = string.Equals(voice.DeviceId, events.DeviceId, StringComparison.OrdinalIgnoreCase)
            ? "Shared input selected. For subtle yawns/laughs, a raw Event Sense mic is usually better."
            : "Split route ready • cleaned Voice/Words + raw Event Sense";
    }

    private void ShowPage(int index)
    {
        _pageIndex = Math.Clamp(index, 0, _pages.Length - 1);
        for (var i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visibility = i == _pageIndex ? Visibility.Visible : Visibility.Collapsed;
            _stepCards[i].BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(i == _pageIndex ? "#613049" : "#25222D"));
            _stepCards[i].Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(i == _pageIndex ? "#171019" : "#0F0E14"));
            _stepDots[i].Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(i <= _pageIndex ? "#FF2D8B" : "#514B57"));
        }

        BackButton.IsEnabled = _pageIndex > 0;
        NextButton.Content = _pageIndex switch
        {
            0 => "GET STARTED",
            3 => "REVIEW",
            4 => "FINISH SETUP",
            _ => "CONTINUE"
        };
        GenericModeButton.Visibility = _pageIndex == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_pageIndex == 4)
            UpdateFinishSummary();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pageIndex == 1 && !await SaveAudioRoutesAsync())
            return;

        if (_pageIndex == 4)
        {
            _profile.Settings.SetupCompleted = true;
            await _profileService.SaveAsync(_profile);
            DialogResult = true;
            Close();
            return;
        }

        ShowPage(_pageIndex + 1);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrating) StopCalibration(false);
        if (_recorder.IsRecording) StopTrainingRecording(true);
        ShowPage(_pageIndex - 1);
    }

    private async Task<bool> SaveAudioRoutesAsync()
    {
        if (SetupVoiceMicComboBox.SelectedItem is not AudioDeviceInfo voice || SetupEventMicComboBox.SelectedItem is not AudioDeviceInfo events)
        {
            FooterStatusText.Text = "Select both a Voice / Words and Event Sense microphone.";
            return false;
        }

        _profile.Settings.VoiceInputDeviceId = voice.DeviceId;
        _profile.Settings.VoiceInputDeviceName = voice.Name;
        _profile.Settings.EventInputDeviceId = events.DeviceId;
        _profile.Settings.EventInputDeviceName = events.Name;
        await _profileService.SaveAsync(_profile);
        FooterStatusText.Text = "audio routes saved";
        return true;
    }

    private async void GenericModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_firstRun)
        {
            Close();
            return;
        }

        if (SetupVoiceMicComboBox.SelectedItem is AudioDeviceInfo voice && SetupEventMicComboBox.SelectedItem is AudioDeviceInfo events)
        {
            _profile.Settings.VoiceInputDeviceId = voice.DeviceId;
            _profile.Settings.VoiceInputDeviceName = voice.Name;
            _profile.Settings.EventInputDeviceId = events.DeviceId;
            _profile.Settings.EventInputDeviceName = events.Name;
        }
        _profile.Settings.SetupCompleted = true;
        await _profileService.SaveAsync(_profile);
        DialogResult = true;
        Close();
    }

    private async void CalibrateRoomButton_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrating)
        {
            StopCalibration(true);
            return;
        }

        if (SetupEventMicComboBox.SelectedItem is not AudioDeviceInfo device)
        {
            FooterStatusText.Text = "Choose an Event Sense microphone first.";
            return;
        }

        if (_recorder.IsRecording)
            StopTrainingRecording(true);

        try
        {
            lock (_roomLevels) _roomLevels.Clear();
            _calibrating = true;
            _calibrationStarted = DateTimeOffset.UtcNow;
            RoomProgressFill.Width = 0;
            CalibrateRoomButton.Content = "CALIBRATING...";
            CalibrateRoomButton.IsEnabled = false;
            FooterStatusText.Text = "stay quiet • leave fan / AC running normally";
            _calibrationAudio.Start(device.DeviceId);
            _calibrationTimer.Start();
        }
        catch (Exception ex)
        {
            _calibrating = false;
            FooterStatusText.Text = $"calibration failed • {ex.Message}";
        }
    }

    private void CalibrationTimer_Tick(object? sender, EventArgs e)
    {
        if (!_calibrating) return;
        var elapsed = (DateTimeOffset.UtcNow - _calibrationStarted).TotalSeconds;
        RoomProgressFill.Width = Math.Clamp(elapsed / 5.0, 0, 1) * 540;
        if (elapsed >= 5.0)
            StopCalibration(true);
    }

    private async void StopCalibration(bool keepResult)
    {
        if (!_calibrating && !_calibrationAudio.IsRunning) return;
        _calibrating = false;
        _calibrationTimer.Stop();
        _calibrationAudio.Stop();
        CalibrateRoomButton.Content = "CALIBRATE AGAIN";
        CalibrateRoomButton.IsEnabled = true;
        RoomProgressFill.Width = keepResult ? 540 : 0;

        if (!keepResult) return;

        double[] values;
        lock (_roomLevels)
            values = _roomLevels.Where(v => double.IsFinite(v) && v > -115).OrderBy(v => v).ToArray();

        if (values.Length < 5)
        {
            FooterStatusText.Text = "not enough room audio captured • try again";
            return;
        }

        var median = values[values.Length / 2];
        var recommended = Math.Clamp(median + 14.0, -50.0, -28.0);
        _profile.Settings.CalibratedNoiseFloorDb = median;
        _profile.Settings.TalkThresholdDb = recommended;
        NoiseFloorText.Text = $"noise floor • {median:0.0} dB   →   suggested talk threshold • {recommended:0.0} dB";
        FooterStatusText.Text = "room calibration saved";
        await _profileService.SaveAsync(_profile);
    }

    private void TrainingRecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;

        if (_recorder.IsRecording)
        {
            var same = ReferenceEquals(button, _recordButton);
            StopTrainingRecording(true);
            if (same) return;
        }

        if (SetupEventMicComboBox.SelectedItem is not AudioDeviceInfo eventDevice)
        {
            TrainingStatusText.Text = "select an Event Sense mic on Audio Routing first";
            return;
        }

        var parts = tag.Split('|');
        if (parts.Length != 2 || !double.TryParse(parts[1], out var maxSeconds))
            return;

        try
        {
            _recordCategory = parts[0];
            _recordMaxSeconds = maxSeconds;
            _recordButton = button;
            _recordStarted = DateTimeOffset.UtcNow;
            _recorder.Start(eventDevice.DeviceId, _recordCategory);
            button.Content = "STOP";
            SetTrainingButtonsEnabled(false, button);
            TrainingStatusText.Text = $"recording {_recordCategory.ToLowerInvariant()} • stop when the sound is finished";
            _recordTimer.Start();
        }
        catch (Exception ex)
        {
            TrainingStatusText.Text = $"record failed • {ex.Message}";
            StopTrainingRecording(false);
        }
    }

    private void RecordTimer_Tick(object? sender, EventArgs e)
    {
        if (!_recorder.IsRecording) return;
        var elapsed = (DateTimeOffset.UtcNow - _recordStarted).TotalSeconds;
        if (_recordButton is not null)
            _recordButton.Content = $"STOP  {elapsed:0.0}s";
        TrainingStatusText.Text = $"recording {_recordCategory.ToLowerInvariant()} • {elapsed:0.0}s / {_recordMaxSeconds:0}s max";
        if (elapsed >= _recordMaxSeconds)
            StopTrainingRecording(true);
    }

    private void StopTrainingRecording(bool keepSample)
    {
        _recordTimer.Stop();
        var elapsed = (DateTimeOffset.UtcNow - _recordStarted).TotalSeconds;
        var path = _recorder.Stop();

        if ((!keepSample || elapsed < 0.4) && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }

        if (_recordButton is not null)
            _recordButton.Content = "RECORD";
        SetTrainingButtonsEnabled(true, null);
        TrainingStatusText.Text = keepSample && elapsed >= 0.4
            ? $"saved {_recordCategory.ToLowerInvariant()} sample • {elapsed:0.0}s"
            : "sample discarded";
        _recordButton = null;
        _recordCategory = "";
        RefreshTrainingCounts();
    }

    private void SetTrainingButtonsEnabled(bool enabled, Button? active)
    {
        foreach (var button in new[] { NormalRecordButton, LaughRecordButton, YawnRecordButton, HmmRecordButton, NoiseRecordButton, KeyboardRecordButton })
            button.IsEnabled = enabled || ReferenceEquals(button, active);
    }

    private static int CountSamples(string category)
    {
        var folder = Path.Combine(ModelInstaller.AppDataRoot, "Training", category);
        if (!Directory.Exists(folder)) return 0;
        return Directory.EnumerateFiles(folder, "*.wav", SearchOption.TopDirectoryOnly).Count();
    }

    private void RefreshTrainingCounts()
    {
        NormalCountText.Text = $"{CountSamples("Normal")} / 4 samples";
        LaughCountText.Text = $"{CountSamples("Laugh")} / 5 samples";
        YawnCountText.Text = $"{CountSamples("Yawn")} / 3 samples";
        HmmCountText.Text = $"{CountSamples("Hmm")} / 5 samples";
        NoiseCountText.Text = $"{CountSamples("Noise")} / 2 samples";
        KeyboardCountText.Text = $"{CountSamples("Keyboard")} / 3 samples";
    }

    private void UpdateFinishSummary()
    {
        var normal = CountSamples("Normal");
        var laugh = CountSamples("Laugh");
        var yawn = CountSamples("Yawn");
        var hmm = CountSamples("Hmm");
        var noise = CountSamples("Noise");
        var keyboard = CountSamples("Keyboard");
        var total = normal + laugh + yawn + hmm + noise + keyboard;
        var quality = normal >= 2 && laugh >= 2 && yawn >= 2 && hmm >= 2 && (noise + keyboard) >= 1
            ? (keyboard >= 2 ? "personal ready" : "ready • add keyboard")
            : "needs samples";
        FinishSummaryText.Text =
            $"Training set • {total} WAV samples • {quality.ToUpperInvariant()} baseline\n\n" +
            $"Normal {normal}   •   Laugh {laugh}   •   Yawn {yawn}   •   Hmm {hmm}   •   Noise {noise}   •   Keyboard {keyboard}\n" +
            $"Room floor {_profile.Settings.CalibratedNoiseFloorDb:0.0} dB   •   Talk threshold {_profile.Settings.TalkThresholdDb:0.0} dB";
    }

    private void OpenTrainingFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.Combine(ModelInstaller.AppDataRoot, "Training");
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object? sender, EventArgs e)
    {
        _calibrationTimer.Stop();
        _recordTimer.Stop();
        _calibrationAudio.Dispose();
        _recorder.Dispose();
    }
}
