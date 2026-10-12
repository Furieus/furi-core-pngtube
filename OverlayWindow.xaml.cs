using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace FuriXeus.Tuber.Native;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            OverlayHint.Opacity = 1;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) => { OverlayHint.Opacity = 0; timer.Stop(); };
            timer.Start();
        };
    }

    public void SetImage(BitmapImage image)
    {
        OutputImage.Source = image;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
