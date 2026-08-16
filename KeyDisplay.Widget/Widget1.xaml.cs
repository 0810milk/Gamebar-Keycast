using System;
using System.Collections.Generic;
using Microsoft.Gaming.XboxGameBar;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
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
        private readonly DispatcherTimer _modeTimer;
        private readonly InputStateReader _reader;
        private InputSnapshot _latest;
        private uint _lastSeq = uint.MaxValue;   // 已渲染的帧序号；uint.MaxValue 强制首帧渲染
        private bool _dark = true;
        private bool _docked;
        private XboxGameBarWidget _widget;   // 本实例自己的 widget（由 App 导航传入），不用共享 App.Widget
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
        private readonly SolidColorBrush _transparent = new SolidColorBrush(Colors.Transparent);

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
            _reader.Snapshot += (_, snap) => _latest = snap;

            // 周期轮询 GameBarDisplayMode，确保无论实例如何创建/激活，都能收敛到正确的固定状态
            _modeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _modeTimer.Tick += OnModePoll;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            _widget = e.Parameter as XboxGameBarWidget;
        }

        // GameBarDisplayMode 在激活瞬间会误报 PinnedOnly（此时 Pinned=false），
        // 按微软文档"固定态 = PinnedOnly 且 Pinned=true"判定，避免 Game Bar 内一打开就只剩按键。
        // 退出/销毁瞬间 COM 属性可能抛错，任何异常都按"未固定"处理，绝不向外抛出。
        private static bool IsDocked(XboxGameBarWidget w)
        {
            try
            {
                return w.GameBarDisplayMode == XboxGameBarDisplayMode.PinnedOnly && w.Pinned;
            }
            catch
            {
                return false;
            }
        }

        private void OnModePoll(object sender, object e)
        {
            var widget = _widget;
            if (widget == null) return;
            bool docked = IsDocked(widget);
            if (docked != _docked)
            {
                try { DiagLog("poll docked=" + docked + " mode=" + widget.GameBarDisplayMode + " pinned=" + widget.Pinned); } catch { }
                _docked = docked;
                ApplyDocked();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            DiagLog("onloaded enter");
            var widget = _widget;
            if (widget != null)
            {
                _docked = IsDocked(widget);
                widget.GameBarDisplayModeChanged += OnGameBarDisplayModeChanged;
                widget.PinnedChanged += OnPinnedChanged;
                try { DiagLog("widget present, initial docked=" + _docked + " mode=" + widget.GameBarDisplayMode + " pinned=" + widget.Pinned); } catch { }
            }
            else
            {
                DiagLog("widget null");
            }
            ApplyTheme();
            // 渲染跟随显示器刷新率（CompositionTarget.Rendering 每 UI 帧触发一次，
            // 60/120/144/240Hz 显示器就是多少帧），不再被固定 30fps 限制；
            // 数据序号未变化时跳过重绘，空闲时几乎零开销。
            CompositionTarget.Rendering += OnRendering;
            _modeTimer.Start();
            _reader.Start();
            TryStartCompanion();
        }

        private void OnGameBarDisplayModeChanged(object sender, object e)
        {
            var w = sender as XboxGameBarWidget;
            if (w == null) w = _widget;
            if (w == null) return;
            bool docked = IsDocked(w);
            if (docked != _docked)
            {
                _docked = docked;
                try { DiagLog("mode changed => docked=" + docked + " mode=" + w.GameBarDisplayMode + " pinned=" + w.Pinned); } catch { }
                ApplyDockedOnUiThread();
            }
        }

        private void OnPinnedChanged(object sender, object e)
        {
            var w = sender as XboxGameBarWidget;
            if (w == null) w = _widget;
            if (w == null) return;
            bool docked = IsDocked(w);
            if (docked != _docked)
            {
                _docked = docked;
                try { DiagLog("pinned changed => docked=" + docked + " mode=" + w.GameBarDisplayMode + " pinned=" + w.Pinned); } catch { }
                ApplyDockedOnUiThread();
            }
        }

        // GameBarDisplayModeChanged/PinnedChanged 会在非 UI 线程回调（实测 0x8001010E），
        // 界面更新必须投递到 UI 线程执行，否则直接触碰 UI 元素会抛"已为另一线程整理的接口"。
        private void ApplyDockedOnUiThread()
        {
            try
            {
                Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ApplyDocked);
            }
            catch
            {
            }
        }

        // 状态变化：重绘界面并记录窗口尺寸（用于确认按钮是否被窗口裁剪）
        private void ApplyDocked()
        {
            try
            {
                ApplyTheme();
            }
            catch (Exception ex)
            {
                DiagLog("applytheme failed: " + ex.GetType().Name + " " + ex.Message);
            }
            var widget = _widget;
            if (widget == null) return;
            try
            {
                var b = widget.WindowBounds;
                DiagLog("bounds " + (int)b.Width + "x" + (int)b.Height + " docked=" + _docked);
            }
            catch (Exception ex)
            {
                DiagLog("bounds read failed: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            var widget = _widget;
            if (widget != null)
            {
                widget.GameBarDisplayModeChanged -= OnGameBarDisplayModeChanged;
                widget.PinnedChanged -= OnPinnedChanged;
            }
            CompositionTarget.Rendering -= OnRendering;
            _modeTimer.Stop();
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

            // 主题切换按钮：标签显示可切换到的主题，自身配色即该主题的预览
            ThemeToggleBtnText.Text = _dark ? "\u767d" : "\u9ed1";   // 白 / 黑
            if (_dark)
            {
                ThemeToggleBtn.Background = _lightDefaultBg;
                ThemeToggleBtn.BorderBrush = _lightBorder;
                ThemeToggleBtnText.Foreground = _lightDefaultFg;
            }
            else
            {
                ThemeToggleBtn.Background = _darkDefaultBg;
                ThemeToggleBtn.BorderBrush = _darkBorder;
                ThemeToggleBtnText.Foreground = _darkDefaultFg;
            }

            if (_docked)
            {
                // Game Bar 关闭、仅固定组件叠加显示时：隐藏面板背景/边框、主题按钮与状态字，只留按键
                RootPanel.Background = _transparent;
                RootPanel.BorderBrush = _transparent;
                ThemeToggleBtn.Visibility = Visibility.Collapsed;
                StatusText.Visibility = Visibility.Collapsed;
            }
            else
            {
                ThemeToggleBtn.Visibility = Visibility.Visible;
                StatusText.Visibility = Visibility.Visible;
            }

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

        // 每 UI 帧触发；数据帧序号未变化时跳过重绘（高帧率下空闲时零开销）
        private void OnRendering(object sender, object e)
        {
            var snap = _latest;
            if (snap == null)
            {
                if (_lastSeq != uint.MaxValue)
                {
                    _lastSeq = uint.MaxValue;
                    StatusText.Text = "\u672a\u8fde\u63a5"; // 未连接
                }
                return;
            }
            if (snap.Seq == _lastSeq) return;
            _lastSeq = snap.Seq;
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

            double vw = snap.VsW > 0 ? snap.VsW : 1920;
            double vh = snap.VsH > 0 ? snap.VsH : 1080;
            double px = ((snap.MouseX - snap.VsX) / vw) * 80.0;
            double py = ((snap.MouseY - snap.VsY) / vh) * 80.0;
            px = Math.Max(0.0, Math.Min(70.0, px));
            py = Math.Max(0.0, Math.Min(70.0, py));
            Canvas.SetLeft(MouseDot, px);
            Canvas.SetTop(MouseDot, py);
            MouseDot.Visibility = Visibility.Visible;
        }

        private void ThemeToggle_Click(object sender, TappedRoutedEventArgs e)
        {
            _dark = !_dark;
            ApplyTheme();
        }

        private static void DiagLog(string msg)
        {
            try
            {
                var dir = ApplicationData.Current.LocalFolder.Path;
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "diag.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n");
            }
            catch
            {
            }
        }
    }
}