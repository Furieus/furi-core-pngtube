using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FuriXeus.Tuber.Native.Models;
using FuriXeus.Tuber.Native.Services;

namespace FuriXeus.Tuber.Native;

public partial class AssetManagerWindow : Window
{
    private readonly AppProfile _profile;
    private readonly ProfileService _profileService;
    private readonly AvatarAssetService _assets;
    private readonly Action _assetsChanged;
    private readonly ObservableCollection<AssetSlotRow> _rows = [];

    public AssetManagerWindow(
        AppProfile profile,
        ProfileService profileService,
        AvatarAssetService assets,
        Action assetsChanged)
    {
        InitializeComponent();
        _profile = profile;
        _profileService = profileService;
        _assets = assets;
        _assetsChanged = assetsChanged;
        Title = $"FURI//CORE v{BuildInfo.Version} — Asset Manager";
        AssetItemsControl.ItemsSource = _rows;
        BuildRows();
        RefreshAll();
    }

    private void BuildRows()
    {
        _rows.Clear();
        var index = new Dictionary<string, AssetSlotRow>(StringComparer.OrdinalIgnoreCase);

        foreach (var state in _profile.States)
        {
            AddSlot(index, state.IdleAsset, $"{state.Name} • REST");
            if (string.Equals(state.TalkAsset, state.IdleAsset, StringComparison.OrdinalIgnoreCase))
            {
                if (index.TryGetValue(Normalize(state.IdleAsset), out var existing))
                    existing.Label = $"{state.Name} • REST / TALK";
            }
            else
            {
                AddSlot(index, state.TalkAsset, $"{state.Name} • TALK");
            }
        }
    }

    private void AddSlot(Dictionary<string, AssetSlotRow> index, string relativePath, string label)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        var key = Normalize(relativePath);
        if (index.ContainsKey(key)) return;

        var row = new AssetSlotRow
        {
            RelativePath = relativePath,
            Label = label,
            FileName = Path.GetFileName(relativePath)
        };
        index[key] = row;
        _rows.Add(row);
    }

    private async void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a FURI//CORE base skin folder",
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(_assets.OverrideFolder) && Directory.Exists(_assets.OverrideFolder))
            dialog.InitialDirectory = _assets.OverrideFolder;

        if (dialog.ShowDialog(this) != true) return;

        _assets.SetOverrideFolder(dialog.FolderName);
        _profile.Settings.CharacterSkinFolder = _assets.OverrideFolder ?? "";
        await SaveAndRefreshAsync();
    }

    private async void ClearFolderButton_Click(object sender, RoutedEventArgs e)
    {
        _assets.SetOverrideFolder(null);
        _profile.Settings.CharacterSkinFolder = "";
        await SaveAndRefreshAsync();
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        _assets.Reload();
        RefreshAll();
        _assetsChanged();
    }

    private async void ChooseAssetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string relativePath }) return;

        var current = _assets.GetAssetOverride(relativePath);
        var dialog = new OpenFileDialog
        {
            Title = $"Choose image for {FindRow(relativePath)?.Label ?? Path.GetFileName(relativePath)}",
            Filter = "Avatar images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|PNG images (*.png)|*.png|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
            dialog.FileName = Path.GetFileName(current);
        }
        else if (!string.IsNullOrWhiteSpace(_assets.OverrideFolder) && Directory.Exists(_assets.OverrideFolder))
        {
            dialog.InitialDirectory = _assets.OverrideFolder;
        }

        if (dialog.ShowDialog(this) != true) return;

        _assets.SetAssetOverride(relativePath, dialog.FileName);
        SyncOverridesToProfile();
        await SaveAndRefreshAsync();
    }

    private async void ClearAssetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string relativePath }) return;
        _assets.ClearAssetOverride(relativePath);
        SyncOverridesToProfile();
        await SaveAndRefreshAsync();
    }

    private async void ClearAllOverridesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_assets.AssetOverrides.Count == 0) return;
        var result = MessageBox.Show(this,
            "Clear every per-emotion file override? The selected skin folder will still remain active.",
            "Clear file overrides",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        _assets.ClearAllAssetOverrides();
        SyncOverridesToProfile();
        await SaveAndRefreshAsync();
    }

    private void OpenSkinFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = _assets.OverrideFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            MessageBox.Show(this, "No base skin folder is currently selected.", "FURI//CORE", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task SaveAndRefreshAsync()
    {
        SyncOverridesToProfile();
        await _profileService.SaveAsync(_profile);
        RefreshAll();
        _assetsChanged();
    }

    private void SyncOverridesToProfile()
    {
        _profile.Settings.AssetOverrides = new Dictionary<string, string>(_assets.AssetOverrides, StringComparer.OrdinalIgnoreCase);
    }

    private void RefreshAll()
    {
        FolderPathText.Text = string.IsNullOrWhiteSpace(_assets.OverrideFolder)
            ? "Built-in Chaos Critter (no base folder selected)"
            : _assets.OverrideFolder;
        FolderPathText.ToolTip = _assets.OverrideFolder;

        foreach (var row in _rows)
        {
            var resolution = _assets.Describe(row.RelativePath);
            row.SourceText = resolution.Source;
            row.EffectivePath = resolution.EffectivePath;
            row.Preview = LoadPreview(resolution.EffectivePath);
        }

        var folderCount = _rows.Count(r => _assets.Describe(r.RelativePath).Source == "SKIN FOLDER");
        var customCount = _rows.Count(r => _assets.GetAssetOverride(r.RelativePath) is not null);
        SummaryText.Text = $"{_rows.Count} asset slots • {customCount} file override{(customCount == 1 ? "" : "s")} • {folderCount} supplied by skin folder";
    }

    private AssetSlotRow? FindRow(string relativePath) =>
        _rows.FirstOrDefault(r => string.Equals(Normalize(r.RelativePath), Normalize(relativePath), StringComparison.OrdinalIgnoreCase));

    private static BitmapImage? LoadPreview(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 128;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
}

public sealed class AssetSlotRow : System.ComponentModel.INotifyPropertyChanged
{
    private string _label = "";
    private string _sourceText = "";
    private string _effectivePath = "";
    private BitmapImage? _preview;

    public string RelativePath { get; set; } = "";
    public string FileName { get; set; } = "";

    public string Label
    {
        get => _label;
        set { if (_label == value) return; _label = value; OnChanged(nameof(Label)); }
    }

    public string SourceText
    {
        get => _sourceText;
        set { if (_sourceText == value) return; _sourceText = value; OnChanged(nameof(SourceText)); }
    }

    public string EffectivePath
    {
        get => _effectivePath;
        set { if (_effectivePath == value) return; _effectivePath = value; OnChanged(nameof(EffectivePath)); }
    }

    public BitmapImage? Preview
    {
        get => _preview;
        set { if (ReferenceEquals(_preview, value)) return; _preview = value; OnChanged(nameof(Preview)); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}
