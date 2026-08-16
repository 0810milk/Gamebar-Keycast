using System;
using System.Collections.Generic;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace KeyDisplay
{
    /// <summary>
    /// 键盘鼠标状态显示小组件。按 30fps 读取命名管道中的 20 字节快照并刷新 UI。
    /// </summary>
    public sealed partial class Widget1 : Page
    {
        private static readonly string[] KeyNames =
            { "Q", "W", "E", "R", "A", "S", "D", "F", "Shift", "Ctrl", "Alt", "Space" };

        private readonly Dictionary<string, Border> _keys = new Dictionary<string, Border>();
        private readonly Dictionary<string, Border> _mouse = new Dictionary<string, Border>();
        private readonly DispatcherTimer _timer;
        private readonly InputStateReader _reader;
        private InputSnapshot _latest;
        private bool _dark = true;
        private static bool s_companionLaunched;

        // 暗色主题画刷
        private readonly SolidColorBrush _darkDefaultBg = new SolidColorBrush(Colors.Black);
        private readonly SolidColorBrush _darkDefaultFg = new SolidColorBrush(Colors.White);
        private readonly SolidColorBrush _darkBorder = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        private readonly SolidColorBrush _darkPressedBg = new SolidColorBrush(Colors.White);
        private readonly SolidColorBrush _darkPressedFg = new SolidColorBrush(Colors.Black);
        private readonly SolidColorBrush _darkPanel = new SolidColorBrush(Color.FromArgb(0xB3, 0x00, 0x00, 0x00));
        private readonly SolidColorBrush _darkPad = new SolidColorBrush(Color.FromArgb(0x4D, 0x00, 0x00, 0x00));

        // 亮色主题画刷
        private readonly SolidColorBrush _lightDefaultBg = new SolidColorBrush(Colors.White);
        private readonly SolidColorBrush _lightDefaultFg = new SolidColorBrush(Colors.Black);
        private readonly SolidColorBrush _lightBorder = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        private readonly SolidColorBrush _lightPressedBg = new SolidColorBrush(Colors.Black);
        private readonly SolidColorBrush _lightPressedFg = new SolidColorBrush(Colors.White);
        private readonly SolidColorBrush _lightPanel = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        private readonly SolidColorBrush _lightPad = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));

        // 虚拟屏幕边界（用于把光标坐标映射到鼠标垫）
        private static readonly int VxLeft;
        private static readonly int VyTop;
        private static readonly int VxWidth;
        private static readonly int VyHeight;

        static Widget1()
        {
            VxLeft = NativeMethods.GetSystemMetrics(76);   // SM_XVIRTUALSCREEN
            VyTop = NativeMethods.GetSystemMetrics(77);    // SM_YVIRTUALSCREEN
            VxWidth = NativeMethods.GetSystemMetrics(78);  // SM_CXVIRTUALSCREEN
            VyHeight = NativeMethods.GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
            if (VxWidth <= 0) VxWidth = 1920;
            if (VyHeight <= 0) VyHeight = 1080;
        }

        public Widget1()
        {
            this.InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            _keys["Q"] = KeyQ; _keys["W"] = KeyW; _keys["E"] = KeyE; _keys["R"] = KeyR;
            _keys["A"] = KeyA; _keys["S"] = KeyS; _keys["D"] = KeyD; _keys["F"] = KeyF;
            _keys["Shift"] = KeyShift; _keys["Ctrl"] = KeyCtrl; _keys["Alt"] = KeyAlt; _keys["Space"] = KeySpace;
            _mouse["L"] = MouseL; _mouse["M"] = MouseM; _mouse["R"] = MouseR;
            _mouse["X1"] = MouseX1; _mouse["X2"] = MouseX2;

            object theme = ApplicationData.Current.LocalSettings.Values["Theme"];
            _dark = (theme is string s && s == "light") ? false : true;

            _reader = new InputStateReader();
            _reader.Snapshot += (s, snap) => _latest = snap;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (s, e) => ApplySnapshot();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyTheme();
            _reader.Start();
            _timer.Start();
            TryStartCompanion();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            _reader.Dispose();
            _latest = null;
        }

        private async void TryStartCompanion()
        {
            if (s_companionLaunched) return;
            s_companionLaunched = true;
            try
            {
                await Launcher.LaunchUriAsync(new Uri("keydisplay://start"));
            }
            catch
            {
            }
        }

        private void ApplyTheme()
        {
            RootPanel.Background = _dark ? _darkPanel : _lightPanel;
            RootPanel.BorderBrush = _dark ? _darkBorder : _lightBorder;
            MousePad.Background = _dark ? _darkPad : _lightPad;
            MousePad.BorderBrush = _dark ? _darkBorder : _lightBorder;
            MouseDot.Fill = _dark ? _darkDefaultFg : _darkDefaultBg;
            MouseDot.Visibility = Visibility.Collapsed;

            foreach (var kv in _keys) SetKey(kv.Value, false);
            foreach (var kv in _mouse) SetKey(kv.Value, false);

            ApplicationData.Current.LocalSettings.Values["Theme"] = _dark ? "dark" : "light";
        }

        private void SetKey(Border border, bool down)
        {
            SolidColorBrush bg, fg, borderBrush;
            if (_dark)
            {
                bg = down ? _darkPressedBg : _darkDefaultBg;
                fg = down ? _darkPressedFg : _darkDefaultFg;
                borderBrush = _darkBorder;
            }
            else
            {
                bg = down ? _lightPressedBg : _lightDefaultBg;
                fg = down ? _lightPressedFg : _lightDefaultFg;
                borderBrush = _lightBorder;
            }
            border.Background = bg;
            border.BorderBrush = borderBrush;
            var tb = border.Child as TextBlock;
            if (tb != null) tb.Foreground = fg;
        }

        private void ApplySnapshot()
        {
            var snap = _latest;
            if (snap == null)
            {
                StatusText.Text = "\u672a\u8fde\u63a5"; // 未连接
                return;
            }
            StatusText.Text = "";

            for (int i = 0; i < KeyNames.Length; i++)
            {
                bool down = (snap.Keys & (1 << i)) != 0;
                Border b;
                if (_keys.TryGetValue(KeyNames[i], out b)) SetKey(b, down);
            }

            bool l = (snap.Mouse & 1) != 0;
            bool r = (snap.Mouse & 2) != 0;
            bool m = (snap.Mouse & 4) != 0;
            bool x1 = (snap.Mouse & 8) != 0;
            bool x2 = (snap.Mouse & 16) != 0;
            SetKey(_mouse["L"], l);
            SetKey(_mouse["R"], r);
            SetKey(_mouse["M"], m);
            SetKey(_mouse["X1"], x1);
            SetKey(_mouse["X2"], x2);

            double px = ((snap.MouseX - VxLeft) / (double)VxWidth) * 80.0;
            double py = ((snap.MouseY - VyTop) / (double)VyHeight) * 80.0;
            px = Math.Max(0.0, Math.Min(70.0, px));
            py = Math.Max(0.0, Math.Min(70.0, py));
            Canvas.SetLeft(MouseDot, px);
            Canvas.SetTop(MouseDot, py);
            MouseDot.Visibility = Visibility.Visible;
        }

        private void Root_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ThemeItem.IsChecked = _dark;
            ThemeItem.Text = _dark
                ? "\u5207\u6362\u4e3a\u4eae\u8272\u6a21\u5f0f"   // 切换为亮色模式
                : "\u5207\u6362\u4e3a\u6697\u8272\u6a21\u5f0f";  // 切换为暗色模式
            var flyout = (MenuFlyout)Resources["WidgetMenu"];
            flyout.ShowAt(RootPanel, e.GetPosition(RootPanel));
        }

        private void ThemeItem_Click(object sender, RoutedEventArgs e)
        {
            _dark = !_dark;
            ApplyTheme();
        }

        private async void StartCompanion_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await Launcher.LaunchUriAsync(new Uri("keydisplay://start"));
            }
            catch
            {
            }
        }

        private void CloseWidget_Click(object sender, RoutedEventArgs e)
        {
            var app = Application.Current as App;
            if (app != null) app.CloseWidget();
        }
    }
}