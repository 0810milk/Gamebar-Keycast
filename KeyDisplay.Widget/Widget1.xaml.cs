using System;
using System.Collections.Generic;
using System.Globalization;
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
        private double _padW = 80;               // 鼠标垫当前宽高（按屏幕纵横比动态计算）
        private double _padH = 80;
        private DateTime _lastDotLog = DateTime.MinValue;  // 点状态日志节流（每秒一条）

        // BongoCat 同款光标平滑：目标点仍按绝对屏幕坐标计算，但用帧率无关的
        // 指数插值追赶（alpha=1-0.75^(dt/16.67)），到位(<0.5px)即吸附停止。
        private const double CursorDampingDecay = 0.75;
        private readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
        private long _lastFrameTicks = -1;
        private double _smoothX = -1;   // 平滑后的垫面坐标；-1 = 尚无初始位置
        private double _smoothY = -1;
        private double _targetX;
        private double _targetY;
        private bool _hasSmoothTarget;

        private bool _dark = true;
        private bool _docked;
        private XboxGameBarWidget _widget;   // 本实例自己的 widget（由 App 导航传入），不用共享 App.Widget
        private static bool s_companionLaunched;

        // 布局自定义：边缘/四角拖拽缩放（窗口式），鼠标垫不参与；默认锁定。
        // 光标：悬停/拖拽边缘时用 CoreWindow.PointerCursor 映射成 Size 光标（拉放窗口那种），
        // 元素级 InputCursor/ProtectedCursor 在当前工程元数据不可见（编译 CS1061）；全部 try/catch 静默降级，
        // 边缘悬停仍配合边框高亮作为视觉提示。
        private const double EdgeHit = 8.0;          // 判定为"边缘"的指针距离（px）
        private const double MinKeyW = 20, MinKeyH = 20;
        private const string LayoutPrefix = "Layout_";
        private bool _layoutLocked = true;           // true=锁定（不可调整），默认开
        private Border _dragKey;                     // 当前拖拽中的按键
        private string _dragMode;                    // l/r/t/b/tl/tr/bl/br
        private double _dragStartX, _dragStartY;
        private double _dragStartW, _dragStartH;
        private double _dragStartML, _dragStartMT;
        private Border _hoverKey;                    // 当前边缘悬停高亮的按键
        private string _hoverMode;                   // 当前悬停的边缘模式（l/r/t/b/tl/tr/bl/br，null=无）
        private CoreCursorType? _curCursorType;      // 当前生效的全局光标类型（null=系统默认）

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
            _mouse["L"] = MouseL; _mouse["M"] = MouseM; _mouse["MR"] = MouseR;   // MR：避免与键盘 R 的 Layout_R 冲突
            _mouse["X1"] = MouseX1; _mouse["X2"] = MouseX2;

            // 布局自定义：所有按键/鼠标键附加指针处理（边缘/四角拖拽缩放），鼠标垫不参与
            foreach (var kv in _keys) AttachResize(kv.Value);
            foreach (var kv in _mouse) AttachResize(kv.Value);
            RestoreLayout();
            object layoutLock = ApplicationData.Current.LocalSettings.Values["LayoutLocked"];
            _layoutLocked = (layoutLock is bool lb) ? lb : true;

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

            if (_docked)
            {
                // Game Bar 关闭、仅固定组件叠加显示时：隐藏面板背景/边框、工具条按钮、状态字与设置，只留按键
                RootPanel.Background = _transparent;
                RootPanel.BorderBrush = _transparent;
                SettingsBtn.Visibility = Visibility.Collapsed;
                SettingsPanel.Visibility = Visibility.Collapsed;
                StatusText.Visibility = Visibility.Collapsed;
            }
            else
            {
                SettingsBtn.Visibility = Visibility.Visible;
                StatusText.Visibility = Visibility.Visible;
            }

            ApplySettingsColors();

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

        // 每 UI 帧触发；数据帧序号未变化时跳过按键重绘（高帧率下空闲时零开销）
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
                _hasSmoothTarget = false;
                return;
            }
            if (snap.Seq != _lastSeq)
            {
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
                SetKey(_mouse["MR"], r);
                SetKey(_mouse["M"], m);
                SetKey(_mouse["X1"], x1);
                SetKey(_mouse["X2"], x2);

                UpdatePadSize(snap.VsW, snap.VsH);
                // 目标点：绝对屏幕坐标 → 垫面位置（点 = 屏幕的真实镜像）
                double vw = snap.VsW > 0 ? snap.VsW : 1920;
                double vh = snap.VsH > 0 ? snap.VsH : 1080;
                double tx = ((snap.MouseX - snap.VsX) / vw) * _padW;
                double ty = ((snap.MouseY - snap.VsY) / vh) * _padH;
                tx = Math.Max(0.0, Math.Min(_padW - 10.0, tx));
                ty = Math.Max(0.0, Math.Min(_padH - 10.0, ty));
                _targetX = tx;
                _targetY = ty;
                // 尚无初始位置（首帧）时直接就位，避免点从角落飞过来
                if (_smoothX < 0) { _smoothX = tx; _smoothY = ty; }
                _hasSmoothTarget = true;
                _lastFrameTicks = -1;   // 静止后首个动画帧用默认帧间隔，平滑起步

                // 点状态监控（每秒一条）：目标点与平滑点
                if ((DateTime.Now - _lastDotLog).TotalSeconds >= 1.0)
                {
                    _lastDotLog = DateTime.Now;
                    DiagLog("dot mx=" + snap.MouseX + " my=" + snap.MouseY
                            + " pad=" + (int)_padW + "x" + (int)_padH
                            + " tgt=" + (int)tx + "," + (int)ty
                            + " sm=" + (int)_smoothX + "," + (int)_smoothY);
                }
            }

            // 平滑追赶（BongoCat 同款指数插值，帧率无关；静止到位后零开销）
            if (!_hasSmoothTarget) return;
            long now = _frameClock.ElapsedTicks;
            double dtMs = _lastFrameTicks < 0
                ? 16.7
                : (now - _lastFrameTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _lastFrameTicks = now;
            double alpha = 1.0 - Math.Pow(CursorDampingDecay, dtMs / (1000.0 / 60.0));
            double nx = _smoothX + (_targetX - _smoothX) * alpha;
            double ny = _smoothY + (_targetY - _smoothY) * alpha;
            double dx = _targetX - nx;
            double dy = _targetY - ny;
            if (dx * dx + dy * dy < 0.25)   // 距目标 < 0.5px：吸附到位并停止
            {
                _smoothX = _targetX;
                _smoothY = _targetY;
                _hasSmoothTarget = false;
            }
            else
            {
                _smoothX = nx;
                _smoothY = ny;
            }
            Canvas.SetLeft(MouseDot, _smoothX);
            Canvas.SetTop(MouseDot, _smoothY);
            MouseDot.Visibility = Visibility.Visible;
        }

        // 鼠标垫尺寸跟随屏幕纵横比：随帧下发的 vs_w/vs_h 就是鼠标坐标的映射基准，
        // 比例天然一致，分辨率/多显示器切换时实时跟随。
        private void UpdatePadSize(int vsW, int vsH)
        {
            double w, h;
            ComputePadSize(vsW, vsH, out w, out h);
            if (Math.Abs(w - _padW) < 0.5 && Math.Abs(h - _padH) < 0.5) return;
            _padW = w;
            _padH = h;
            MousePad.Width = w;
            MousePad.Height = h;
        }

        // 先按比例装入最大盒子（180x120），极端比例（超宽/超高）保比例缩放到最小边以上，
        // 避免把面板撑爆；vs_w/vs_h 无效时按 16:9 兜底。
        private static void ComputePadSize(int vsW, int vsH, out double w, out double h)
        {
            const double MaxW = 180, MaxH = 120, MinW = 40, MinH = 36;
            double rw = vsW > 0 ? vsW : 1920;
            double rh = vsH > 0 ? vsH : 1080;
            double scale = Math.Min(MaxW / rw, MaxH / rh);
            w = rw * scale;
            h = rh * scale;
            if (w < MinW) { double f = MinW / w; w = MinW; h *= f; }
            if (h < MinH) { double f = MinH / h; h = MinH; w *= f; }
        }

        // 设置子菜单配色：菜单框、标题、主题行、锁定行、关闭按钮都随当前主题刷新
        private void ApplySettingsColors()
        {
            SettingsMenu.Background = _dark ? _darkPanel : _lightPanel;
            SettingsMenu.BorderBrush = _dark ? _darkBorder : _lightBorder;
            SettingsTitle.Foreground = _dark ? _darkDefaultFg : _lightDefaultFg;
            SettingsThemeLabel.Foreground = _dark ? _darkDefaultFg : _lightDefaultFg;
            SettingsLockLabel.Foreground = _dark ? _darkDefaultFg : _lightDefaultFg;

            SettingsThemeText.Text = _dark ? "\u767d" : "\u9ed1";   // 白 / 黑
            SettingsLockText.Text = _layoutLocked ? "\u5f00" : "\u5173";   // 开 / 关
            if (_dark)
            {
                SettingsThemeBtn.Background = _lightDefaultBg;
                SettingsThemeBtn.BorderBrush = _lightBorder;
                SettingsThemeText.Foreground = _lightDefaultFg;
                SettingsLockBtn.Background = _darkDefaultBg;
                SettingsLockBtn.BorderBrush = _darkBorder;
                SettingsLockText.Foreground = _darkDefaultFg;
                SettingsCloseBtn.Background = _darkDefaultBg;
                SettingsCloseBtn.BorderBrush = _darkBorder;
                SettingsCloseText.Foreground = _darkDefaultFg;
                SettingsTestBtn.Background = _darkDefaultBg;
                SettingsTestBtn.BorderBrush = _darkBorder;
                SettingsTestText.Foreground = _darkDefaultFg;
                SettingsResetBtn.Background = _darkDefaultBg;
                SettingsResetBtn.BorderBrush = _darkBorder;
                SettingsResetText.Foreground = _darkDefaultFg;
                SettingsBtnIcon.Foreground = _darkDefaultFg;
            }
            else
            {
                SettingsThemeBtn.Background = _darkDefaultBg;
                SettingsThemeBtn.BorderBrush = _darkBorder;
                SettingsThemeText.Foreground = _darkDefaultFg;
                SettingsLockBtn.Background = _lightDefaultBg;
                SettingsLockBtn.BorderBrush = _lightBorder;
                SettingsLockText.Foreground = _lightDefaultFg;
                SettingsCloseBtn.Background = _lightDefaultBg;
                SettingsCloseBtn.BorderBrush = _lightBorder;
                SettingsCloseText.Foreground = _lightDefaultFg;
                SettingsTestBtn.Background = _lightDefaultBg;
                SettingsTestBtn.BorderBrush = _lightBorder;
                SettingsTestText.Foreground = _lightDefaultFg;
                SettingsResetBtn.Background = _lightDefaultBg;
                SettingsResetBtn.BorderBrush = _lightBorder;
                SettingsResetText.Foreground = _lightDefaultFg;
                SettingsBtnIcon.Foreground = _lightDefaultFg;
            }
        }

        // 点击设置图标：展开设置子菜单
        private void Settings_Click(object sender, TappedRoutedEventArgs e)
        {
            ApplySettingsColors();
            SettingsPanel.Visibility = Visibility.Visible;
            DiagLog("settings opened theme=" + (_dark ? "dark" : "light"));
        }

        // 点击关闭按钮：收起设置子菜单
        private void SettingsClose_Click(object sender, TappedRoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            DiagLog("settings closed by close btn");
        }

        // 点击菜单框内部：标记已处理，避免冒泡到遮罩触发关闭
        private void SettingsMenu_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        // 点击遮罩（菜单框外）：收起设置子菜单
        private void SettingsPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            DiagLog("settings closed by mask");
        }

        // 设置面板里的主题切换：与底部胶囊按钮同逻辑
        private void SettingsTheme_Click(object sender, TappedRoutedEventArgs e)
        {
            _dark = !_dark;
            ApplyTheme();
        }

        // 测试按钮：按下反色（点击反馈），松开恢复主题色
        private void SettingsTest_Pressed(object sender, PointerRoutedEventArgs e)
        {
            SettingsTestBtn.Background = _dark ? _darkPressedBg : _lightPressedBg;
            SettingsTestBtn.BorderBrush = _dark ? _darkPressedBg : _lightPressedBg;
            SettingsTestText.Foreground = _dark ? _darkPressedFg : _lightPressedFg;
        }

        private void SettingsTest_Released(object sender, PointerRoutedEventArgs e)
        {
            ApplySettingsColors();
        }

        private void SettingsTest_Exited(object sender, PointerRoutedEventArgs e)
        {
            ApplySettingsColors();
        }

        // 测试按钮点击：记日志，验证点击链路
        private void SettingsTest_Click(object sender, TappedRoutedEventArgs e)
        {
            DiagLog("test button clicked theme=" + (_dark ? "dark" : "light"));
        }

        // 重置按键布局按钮：按下反色（点击反馈），松开恢复主题色
        private void SettingsReset_Pressed(object sender, PointerRoutedEventArgs e)
        {
            SettingsResetBtn.Background = _dark ? _darkPressedBg : _lightPressedBg;
            SettingsResetBtn.BorderBrush = _dark ? _darkPressedBg : _lightPressedBg;
            SettingsResetText.Foreground = _dark ? _darkPressedFg : _lightPressedFg;
        }

        private void SettingsReset_Released(object sender, PointerRoutedEventArgs e)
        {
            ApplySettingsColors();
        }

        private void SettingsReset_Exited(object sender, PointerRoutedEventArgs e)
        {
            ApplySettingsColors();
        }

        // 重置按键布局：全部按键/鼠标键恢复默认尺寸位置，并清除已保存的自定义布局
        private void SettingsReset_Click(object sender, TappedRoutedEventArgs e)
        {
            ResetKeyLayout("Q", KeyQ, 52, 48, new Thickness(0, 0, 6, 0));
            ResetKeyLayout("W", KeyW, 52, 48, new Thickness(0, 0, 6, 0));
            ResetKeyLayout("E", KeyE, 52, 48, new Thickness(0, 0, 6, 0));
            ResetKeyLayout("R", KeyR, 52, 48, new Thickness(0, 0, 0, 0));
            ResetKeyLayout("A", KeyA, 52, 48, new Thickness(0, 0, 6, 0));
            ResetKeyLayout("S", KeyS, 52, 48, new Thickness(0, 0, 6, 0));
            ResetKeyLayout("D", KeyD, 52, 48, new Thickness(0, 0, 6, 0));
            ResetKeyLayout("F", KeyF, 52, 48, new Thickness(0, 0, 0, 0));
            ResetKeyLayout("Shift", KeyShift, 68, 48, new Thickness(0, 0, 6, 0));
            ResetKeyLayout("Ctrl", KeyCtrl, 68, 48, new Thickness(0, 0, 6, 0));
            ResetKeyLayout("Alt", KeyAlt, 68, 48, new Thickness(0, 0, 0, 0));
            ResetKeyLayout("Space", KeySpace, 176, 48, new Thickness(0, 0, 0, 0));
            ResetKeyLayout("L", MouseL, 36, 36, new Thickness(0, 0, 6, 0));
            ResetKeyLayout("M", MouseM, 36, 36, new Thickness(0, 0, 6, 0));
            ResetKeyLayout("MR", MouseR, 36, 36, new Thickness(0, 0, 0, 0));
            ResetKeyLayout("X1", MouseX1, 36, 36, new Thickness(0, 0, 6, 0));
            ResetKeyLayout("X2", MouseX2, 36, 36, new Thickness(0, 0, 0, 0));
            ClearHover();
            DiagLog("layout reset");
        }

        // 恢复单个按键默认尺寸并写入默认布局（写值而非删除：LocalSettings 的 Remove 不一定立即落盘，写默认值保证重启后也是默认）
        private void ResetKeyLayout(string name, Border b, double w, double h, Thickness m)
        {
            b.Width = w;
            b.Height = h;
            b.Margin = m;
            ApplicationData.Current.LocalSettings.Values[LayoutPrefix + name] =
                ((int)w) + ";" + ((int)h) + ";" + ((int)m.Left) + ";" + ((int)m.Top);
        }

        // 锁定布局开关：开=按键不可调整（默认），关=可拖动边缘/四角缩放
        private void SettingsLock_Click(object sender, TappedRoutedEventArgs e)
        {
            _layoutLocked = !_layoutLocked;
            ApplicationData.Current.LocalSettings.Values["LayoutLocked"] = _layoutLocked;
            ClearHover();   // 锁定/解锁都重置高亮与光标，避免残留 Size 光标
            ApplySettingsColors();
            DiagLog("layout lock=" + (_layoutLocked ? "on" : "off"));
        }

        // 给按键附加指针处理并让内层文字不拦截指针（Border 直接收事件）
        private void AttachResize(Border b)
        {
            var tb = b.Child as TextBlock;
            if (tb != null) tb.IsHitTestVisible = false;
            b.PointerPressed += Key_PointerPressed;
            b.PointerMoved += Key_PointerMoved;
            b.PointerReleased += Key_PointerReleased;
            b.PointerExited += Key_PointerExited;
            b.PointerCaptureLost += Key_PointerCaptureLost;
        }

        // 判定指针是否在按键边缘/四角（8px 阈值），返回模式 l/r/t/b/tl/tr/bl/br
        private string HitTestEdge(Border b, Point pt)
        {
            double w = b.Width;
            if (double.IsNaN(w)) w = b.ActualWidth;
            double h = b.Height;
            if (double.IsNaN(h)) h = b.ActualHeight;
            bool left = pt.X <= EdgeHit, right = pt.X >= w - EdgeHit;
            bool top = pt.Y <= EdgeHit, bottom = pt.Y >= h - EdgeHit;
            if (left && top) return "tl";
            if (right && top) return "tr";
            if (left && bottom) return "bl";
            if (right && bottom) return "br";
            if (left) return "l";
            if (right) return "r";
            if (top) return "t";
            if (bottom) return "b";
            return null;
        }

        // 按边缘模式映射系统光标（拉放窗口样式）；null/锁定=恢复默认光标。
        // 用 CoreWindow.PointerCursor（稳定 UWP API；元素级 InputCursor/ProtectedCursor 在当前工程元数据不可见）。
        private void ApplyCursor(string mode)
        {
            try
            {
                CoreCursorType? target = null;
                if (!_layoutLocked && mode != null)
                {
                    switch (mode)
                    {
                        case "l":
                        case "r":
                            target = CoreCursorType.SizeWestEast;
                            break;
                        case "t":
                        case "b":
                            target = CoreCursorType.SizeNorthSouth;
                            break;
                        case "tl":
                        case "br":
                            target = CoreCursorType.SizeNorthwestSoutheast;
                            break;
                        case "tr":
                        case "bl":
                            target = CoreCursorType.SizeNortheastSouthwest;
                            break;
                        default:
                            target = CoreCursorType.Hand;
                            break;
                    }
                }
                if (target == _curCursorType) return;
                _curCursorType = target;
                var cw = CoreWindow.GetForCurrentThread();
                if (cw != null) cw.PointerCursor = target == null ? null : new CoreCursor(target.Value, 0);
            }
            catch { /* 静默降级 */ }
        }

        // 边缘悬停提示：边框高亮（纯 XAML 属性，沙箱安全；不碰 CoreWindow 光标）
        private void SetHover(Border b, string mode)
        {
            if (_hoverKey == b && _hoverMode == mode) return;   // 同键同模式才去重；边缘→角落需更新光标
            ClearHover();
            _hoverKey = b;
            _hoverMode = mode;
            b.BorderBrush = _dark ? _darkDefaultFg : _darkDefaultBg;
            ApplyCursor(mode);
        }

        private void ClearHover()
        {
            if (_hoverKey == null) return;
            _hoverKey.BorderBrush = _dark ? _darkBorder : _lightBorder;
            ApplyCursor(null);
            _hoverKey = null;
            _hoverMode = null;
        }

        // 按下：落在边缘/四角则开始拖拽并捕获指针
        private void Key_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_layoutLocked) return;
            var b = sender as Border;
            if (b == null) return;
            string mode = HitTestEdge(b, e.GetCurrentPoint(b).Position);
            if (mode == null) return;
            ApplyCursor(mode);
            _dragKey = b;
            _dragMode = mode;
            _dragStartX = e.GetCurrentPoint(null).Position.X;
            _dragStartY = e.GetCurrentPoint(null).Position.Y;
            _dragStartW = b.Width;
            _dragStartH = b.Height;
            _dragStartML = b.Margin.Left;
            _dragStartMT = b.Margin.Top;
            try { b.CapturePointer(e.Pointer); } catch { }
            e.Handled = true;
        }

        // 移动：拖拽中实时缩放；未拖拽且解锁时更新边缘高亮
        private void Key_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var b = sender as Border;
            if (b == null) return;
            if (_dragKey != null)
            {
                var key = _dragKey;   // 捕获期间 CaptureLost 可能已把 _dragKey 置空，用局部变量
                if (b != key) return;
                double dx = e.GetCurrentPoint(null).Position.X - _dragStartX;
                double dy = e.GetCurrentPoint(null).Position.Y - _dragStartY;
                double w = _dragStartW, h = _dragStartH, ml = _dragStartML, mt = _dragStartMT;
                if (_dragMode.Contains("l"))
                {
                    w = Math.Max(MinKeyW, _dragStartW - dx);
                    ml = (_dragStartML + _dragStartW) - w;   // 保持右缘不动
                }
                if (_dragMode.Contains("r")) w = Math.Max(MinKeyW, _dragStartW + dx);
                if (_dragMode.Contains("t"))
                {
                    h = Math.Max(MinKeyH, _dragStartH - dy);
                    mt = (_dragStartMT + _dragStartH) - h;   // 保持下缘不动
                }
                if (_dragMode.Contains("b")) h = Math.Max(MinKeyH, _dragStartH + dy);
                key.Width = w;
                key.Height = h;
                key.Margin = new Thickness(ml, mt, key.Margin.Right, key.Margin.Bottom);
                return;
            }
            if (_layoutLocked) return;
            var mode = HitTestEdge(b, e.GetCurrentPoint(b).Position);
            if (mode != null) SetHover(b, mode);
            else ClearHover();
        }

        // 松开：结束拖拽并持久化布局
        private void Key_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_dragKey == null) return;
            var key = _dragKey;   // 释放捕获会同步触发 CaptureLost 置空 _dragKey，先存局部变量
            try { key.ReleasePointerCapture(e.Pointer); } catch { }
            DiagLog("layout resize " + NameOf(key)
                    + " w=" + (int)key.Width + " h=" + (int)key.Height
                    + " ml=" + (int)key.Margin.Left + " mt=" + (int)key.Margin.Top);
            _dragKey = null;
            _dragMode = null;
            ApplyCursor(null);   // 松开后恢复默认光标
            SaveLayout();
        }

        private void Key_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_dragKey != null) return;
            ClearHover();
        }

        private void Key_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _dragKey = null;
            _dragMode = null;
            ApplyCursor(null);   // 异常丢捕获（失焦/窗口切换）也恢复默认光标，避免 Size 光标残留
        }

        private string NameOf(Border b)
        {
            foreach (var kv in _keys) if (kv.Value == b) return kv.Key;
            foreach (var kv in _mouse) if (kv.Value == b) return kv.Key;
            return "?";
        }

        // 布局持久化：每个按键存 "宽;高;左边距;上边距"（边距用于左/上缘拖动后的定位）
        private void SaveLayout()
        {
            foreach (var kv in _keys) SaveKeyLayout(kv.Key, kv.Value);
            foreach (var kv in _mouse) SaveKeyLayout(kv.Key, kv.Value);
        }

        private void SaveKeyLayout(string name, Border b)
        {
            ApplicationData.Current.LocalSettings.Values[LayoutPrefix + name] =
                ((int)b.Width) + ";" + ((int)b.Height) + ";"
                + ((int)b.Margin.Left) + ";" + ((int)b.Margin.Top);
        }

        private void RestoreLayout()
        {
            foreach (var kv in _keys) RestoreKeyLayout(kv.Key, kv.Value);
            foreach (var kv in _mouse) RestoreKeyLayout(kv.Key, kv.Value);
        }

        private void RestoreKeyLayout(string name, Border b)
        {
            try
            {
                var s = ApplicationData.Current.LocalSettings.Values[LayoutPrefix + name] as string;
                if (s == null) return;
                var parts = s.Split(';');
                if (parts.Length != 4) return;
                double w = double.Parse(parts[0], CultureInfo.InvariantCulture);
                double h = double.Parse(parts[1], CultureInfo.InvariantCulture);
                double ml = double.Parse(parts[2], CultureInfo.InvariantCulture);
                double mt = double.Parse(parts[3], CultureInfo.InvariantCulture);
                if (w < MinKeyW || h < MinKeyH) return;
                b.Width = w;
                b.Height = h;
                b.Margin = new Thickness(ml, mt, b.Margin.Right, b.Margin.Bottom);
            }
            catch
            {
            }
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