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
    /// 键盘鼠标状态显示小组件。读取命名管道推送的输入快照（协议 v3，68 字节）并刷新 UI。
    /// </summary>
    public sealed partial class Widget1 : Page
    {
        private static readonly string[] KeyNames =
            { "Q", "W", "E", "R", "A", "S", "D", "F", "Shift", "Ctrl", "Alt", "Space" };

        private readonly Dictionary<string, Border> _keys = new Dictionary<string, Border>();
        private readonly Dictionary<string, Border> _mouse = new Dictionary<string, Border>();
        // 自定义按键（"自定义控件"菜单从 87 配列布局添加）：按名字去重、动态创建、LocalSettings 持久化
        private readonly Dictionary<string, Border> _customKeys = new Dictionary<string, Border>();
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
        private CoreCursor _defaultCursor;           // 加载时保存的初始默认光标（恢复用，不赋 null）

        // 长按移动 + 右键删除：200ms 长按进入移动模式（拖动改变位置）；右键自定义键弹删除确认框
        private DispatcherTimer _longPressTimer;
        private Border _longPressKey;
        private Border _moveKey;                       // 当前移动模式中的按键
        private double _moveStartX, _moveStartY;       // 移动按下时的指针位置
        private double _moveStartML, _moveStartMT;     // 移动按下时的 Margin 起点
        private Border _deleteConfirmKey;              // 待确认删除的自定义键（右键弹出）
        private Point _pressPointerRoot;               // 最近一次按下的根坐标（长按移动用）

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
            RestoreCustomKeys();
            // 保存初始默认光标：恢复时赋回它，而不是赋 null（沙箱内 null 会导致光标不显示）
            try
            {
                var cw0 = CoreWindow.GetForCurrentThread();
                if (cw0 != null)
                {
                    _defaultCursor = cw0.PointerCursor;
                    if (_defaultCursor == null) _defaultCursor = new CoreCursor(CoreCursorType.Arrow, 0);
                    DiagLog("default cursor saved type=" + _defaultCursor.Type);
                }
            }
            catch (Exception ex) { DiagLog("default cursor save fail: " + ex.Message); }
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
            if (_longPressTimer != null) _longPressTimer.Stop();
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
            foreach (var kv in _customKeys) SetKey(kv.Value, false);
            if (_moveKey != null) { SetKey(_moveKey, false); _moveKey = null; }   // 主题切换时清除移动高亮
            _deleteConfirmKey = null;
            if (DeleteConfirmPanel != null) DeleteConfirmPanel.Visibility = Visibility.Collapsed;

            if (_docked)
            {
                // Game Bar 关闭、仅固定组件叠加显示时：隐藏面板背景/边框、工具条按钮、状态字与设置，只留按键
                RootPanel.Background = _transparent;
                RootPanel.BorderBrush = _transparent;
                SettingsBtn.Visibility = Visibility.Collapsed;
                SettingsPanel.Visibility = Visibility.Collapsed;
                LockPanel.Visibility = Visibility.Collapsed;   // 固定叠加态一并收起二级锁定菜单，避免残留覆盖层
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
            // 自定义键：按下状态改从快照的 256 位 VK 位图（协议 v3 ExtraKeys）读取，不再轮询
            // 系统键态 API（UWP 沙箱内不可用）。位 = (extra[vk>>3]>>(vk&7))&1；vk 越界 0..255 或
            // 旧协议快照（ExtraKeys==null）一律视为未按下（降级为仅显示）。
            if (_customKeys.Count > 0)
            {
                foreach (var kv in _customKeys)
                {
                    if (kv.Value == _moveKey) continue;   // 移动模式高亮不被轮询覆盖
                    bool down = false;
                    if (snap != null && snap.ExtraKeys != null)
                    {
                        int vk = VkFromName(kv.Key);
                        if (vk >= 0 && vk <= 255)
                        {
                            down = ((snap.ExtraKeys[vk >> 3] >> (vk & 7)) & 1) != 0;
                        }
                    }
                    SetKey(kv.Value, down);
                }
            }
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
                    if (_keys.TryGetValue(KeyNames[i], out b))
                    {
                        if (b != _moveKey) SetKey(b, down);   // 移动高亮不被快照重绘覆盖
                    }
                }

                bool l = (snap.Mouse & 1) != 0;
                bool r = (snap.Mouse & 2) != 0;
                bool m = (snap.Mouse & 4) != 0;
                bool x1 = (snap.Mouse & 8) != 0;
                bool x2 = (snap.Mouse & 16) != 0;
                if (_mouse["L"] != _moveKey) SetKey(_mouse["L"], l);
                if (_mouse["MR"] != _moveKey) SetKey(_mouse["MR"], r);
                if (_mouse["M"] != _moveKey) SetKey(_mouse["M"], m);
                if (_mouse["X1"] != _moveKey) SetKey(_mouse["X1"], x1);
                if (_mouse["X2"] != _moveKey) SetKey(_mouse["X2"], x2);

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

        // 设置子菜单配色：菜单框、标题、主题行、"自定义控件"入口都随当前主题刷新；
        // 二级控件菜单的标题/锁定行/按钮及 87 配列布局键同步刷新
        private void ApplySettingsColors()
        {
            SettingsMenu.Background = _dark ? _darkPanel : _lightPanel;
            SettingsMenu.BorderBrush = _dark ? _darkBorder : _lightBorder;
            SettingsTitle.Foreground = _dark ? _darkDefaultFg : _lightDefaultFg;
            SettingsThemeLabel.Foreground = _dark ? _darkDefaultFg : _lightDefaultFg;

            LockMenu.Background = _dark ? _darkPanel : _lightPanel;
            LockMenu.BorderBrush = _dark ? _darkBorder : _lightBorder;
            LockMenuTitle.Foreground = _dark ? _darkDefaultFg : _lightDefaultFg;
            LockSwitchLabel.Foreground = _dark ? _darkDefaultFg : _lightDefaultFg;
            KeyPickerToggleText.Foreground = _dark ? _darkDefaultFg : _lightDefaultFg;
            KeyPickerToggleArrow.Foreground = _dark ? _darkDefaultFg : _lightDefaultFg;

            SettingsThemeText.Text = _dark ? "\u767d" : "\u9ed1";   // 白 / 黑
            LockSwitchText.Text = _layoutLocked ? "\u5f00" : "\u5173";   // 开 / 关（锁定菜单开关，与设置面板逻辑同步）
            if (_dark)
            {
                SettingsThemeBtn.Background = _lightDefaultBg;
                SettingsThemeBtn.BorderBrush = _lightBorder;
                SettingsThemeText.Foreground = _lightDefaultFg;
                LockKeyBtn.Background = _darkDefaultBg;
                LockKeyBtn.BorderBrush = _darkBorder;
                LockKeyText.Foreground = _darkDefaultFg;
                LockSwitchBtn.Background = _darkDefaultBg;
                LockSwitchBtn.BorderBrush = _darkBorder;
                LockSwitchText.Foreground = _darkDefaultFg;
                LockResetBtn.Background = _darkDefaultBg;
                LockResetBtn.BorderBrush = _darkBorder;
                LockResetText.Foreground = _darkDefaultFg;
                LockCloseBtn.Background = _darkDefaultBg;
                LockCloseBtn.BorderBrush = _darkBorder;
                LockCloseText.Foreground = _darkDefaultFg;
                DeleteConfirmBox.Background = _darkPanel;
                DeleteConfirmBox.BorderBrush = _darkBorder;
                DeleteConfirmText.Foreground = _darkDefaultFg;
                DeleteConfirmYes.Background = _darkDefaultBg;
                DeleteConfirmYes.BorderBrush = _darkBorder;
                DeleteConfirmYesText.Foreground = _darkDefaultFg;
                DeleteConfirmNo.Background = _darkDefaultBg;
                DeleteConfirmNo.BorderBrush = _darkBorder;
                DeleteConfirmNoText.Foreground = _darkDefaultFg;
                SettingsBtnIcon.Foreground = _darkDefaultFg;
            }
            else
            {
                SettingsThemeBtn.Background = _darkDefaultBg;
                SettingsThemeBtn.BorderBrush = _darkBorder;
                SettingsThemeText.Foreground = _darkDefaultFg;
                LockKeyBtn.Background = _lightDefaultBg;
                LockKeyBtn.BorderBrush = _lightBorder;
                LockKeyText.Foreground = _lightDefaultFg;
                LockSwitchBtn.Background = _lightDefaultBg;
                LockSwitchBtn.BorderBrush = _lightBorder;
                LockSwitchText.Foreground = _lightDefaultFg;
                LockResetBtn.Background = _lightDefaultBg;
                LockResetBtn.BorderBrush = _lightBorder;
                LockResetText.Foreground = _lightDefaultFg;
                LockCloseBtn.Background = _lightDefaultBg;
                LockCloseBtn.BorderBrush = _lightBorder;
                LockCloseText.Foreground = _lightDefaultFg;
                DeleteConfirmBox.Background = _lightPanel;
                DeleteConfirmBox.BorderBrush = _lightBorder;
                DeleteConfirmText.Foreground = _lightDefaultFg;
                DeleteConfirmYes.Background = _lightDefaultBg;
                DeleteConfirmYes.BorderBrush = _lightBorder;
                DeleteConfirmYesText.Foreground = _lightDefaultFg;
                DeleteConfirmNo.Background = _lightDefaultBg;
                DeleteConfirmNo.BorderBrush = _lightBorder;
                DeleteConfirmNoText.Foreground = _lightDefaultFg;
                SettingsBtnIcon.Foreground = _lightDefaultFg;
            }
            ApplyPickerColors();
        }

        // 87 配列布局键配色：遍历 KeyPickerScroll 内容里所有带 Tag 的键 Border，随主题刷新（与 LockMenu 系一致）
        private void ApplyPickerColors()
        {
            var content = KeyPickerScroll.Content as Panel;
            if (content == null) return;
            ApplyPickerColorsRecursive(content);
        }

        private void ApplyPickerColorsRecursive(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var b = child as Border;
                if (b != null && b.Tag is string)
                {
                    b.Background = _dark ? _darkDefaultBg : _lightDefaultBg;
                    b.BorderBrush = _dark ? _darkBorder : _lightBorder;
                    var tb = b.Child as TextBlock;
                    if (tb != null) tb.Foreground = _dark ? _darkDefaultFg : _lightDefaultFg;
                }
                else
                {
                    ApplyPickerColorsRecursive(child);
                }
            }
        }

        // 点击设置图标：展开设置子菜单
        private void Settings_Click(object sender, TappedRoutedEventArgs e)
        {
            ApplySettingsColors();
            SettingsPanel.Visibility = Visibility.Visible;
            DiagLog("settings opened theme=" + (_dark ? "dark" : "light"));
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

        // 重置按键布局共用逻辑（设置面板与二级锁定菜单的重置按钮都走这里）
        private void PerformLayoutReset()
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
            // 重置 = 恢复到刚安装时的样子：删除全部自定义添加的按键（字典/面板/持久化），默认键恢复初始布局。
            // 绝不触碰主题（_dark/_light 及任何配色）——重置只处理按键布局与自定义键。
            var deadNames = new List<string>();
            foreach (var kv in _customKeys) deadNames.Add(kv.Key);
            foreach (var nm in deadNames)
            {
                Border cb;
                if (_customKeys.TryGetValue(nm, out cb))
                {
                    _customKeys.Remove(nm);
                    CustomKeysPanel.Children.Remove(cb);
                    ApplicationData.Current.LocalSettings.Values.Remove("Custom_" + nm);
                    ApplicationData.Current.LocalSettings.Values.Remove("CustomPos_" + nm);
                }
            }
            if (_customKeys.Count == 0) CustomKeysPanel.Visibility = Visibility.Collapsed;
            ClearHover();
            DiagLog("layout reset (custom keys cleared: " + deadNames.Count + ")");
        }

        // 二级控件菜单："自定义控件"按键点击，展开控件菜单（覆盖层在设置面板之上，外观一致）
        private void LockKey_Click(object sender, TappedRoutedEventArgs e)
        {
            ApplySettingsColors();
            LockPanel.Visibility = Visibility.Visible;
            DiagLog("control menu opened");
        }

        // 点击 87 配列布局中的按键：在布局底部新增该键名的自定义按键
        private void AddKeyFromLayout_Click(object sender, TappedRoutedEventArgs e)
        {
            var border = sender as Border;
            if (border == null || border.Tag == null) return;
            AddCustomKey(border.Tag.ToString());
            e.Handled = true;
        }

        // 添加自定义按键：按名字去重；已存在则只提示不重复添加
        private void AddCustomKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (_customKeys.ContainsKey(name))
            {
                DiagLog("custom key duplicate: " + name);
                return;
            }
            var border = new Border
            {
                Width = CustomKeyWidth(name),
                Height = 48,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 6, 0),
                Tag = name
            };
            border.Child = new TextBlock
            {
                Text = name,
                FontSize = 18,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _customKeys[name] = border;
            CustomKeysPanel.Children.Add(border);
            CustomKeysPanel.Visibility = Visibility.Visible;
            AttachResize(border);       // 复用拖拽缩放/hover/锁定/长按移动机制
            SetKey(border, false);      // 初始主题样式
            ApplicationData.Current.LocalSettings.Values["Custom_" + name] = "1";
            // 移动位置持久化：若已存 CustomPos_<名>（left;top）则应用，否则写默认 (0,0)
            string pos = ApplicationData.Current.LocalSettings.Values["CustomPos_" + name] as string;
            if (!string.IsNullOrEmpty(pos))
            {
                try
                {
                    var pp = pos.Split(';');
                    if (pp.Length == 2)
                    {
                        double pl = double.Parse(pp[0], CultureInfo.InvariantCulture);
                        double pt = double.Parse(pp[1], CultureInfo.InvariantCulture);
                        border.Margin = new Thickness(pl, pt, 6, 0);   // 恢复时保留默认右间距 6
                    }
                }
                catch { }
            }
            else
            {
                ApplicationData.Current.LocalSettings.Values["CustomPos_" + name] = "0;0";
            }
            DiagLog("custom key added: " + name);
        }

        // 自定义键宽度：单个字符 52，两个字符 68，Space 176，更长按 22px/字符递增
        private static double CustomKeyWidth(string name)
        {
            if (name == "Space") return 176;
            int len = name.Length;
            if (len <= 1) return 52;
            if (len == 2) return 68;
            return 52 + (len - 2) * 22;
        }

        // 启动时从 LocalSettings 恢复自定义按键（"Custom_<键名>" = "1" 即存在）
        private void RestoreCustomKeys()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                foreach (var kv in values)
                {
                    if (kv.Key.StartsWith("Custom_", StringComparison.Ordinal))
                    {
                        string name = kv.Key.Substring("Custom_".Length);
                        if (!string.IsNullOrEmpty(name)) AddCustomKey(name);
                    }
                }
            }
            catch
            {
            }
        }

        // 键名 → VK 虚拟键码：字母=ASCII 大写，数字=0x30-0x39，F1-F12=0x70-0x7B，符号/编辑/方向键查表
        private static int VkFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            if (name.Length == 1)
            {
                char c = name[0];
                if (c >= 'a' && c <= 'z') return (int)char.ToUpperInvariant(c);
                if (c >= 'A' && c <= 'Z') return (int)c;
                if (c >= '0' && c <= '9') return 0x30 + (c - '0');
                switch (c)
                {
                    case '`': return 0xC0;
                    case '-': return 0xBD;
                    case '=': return 0xBB;
                    case '[': return 0xDB;
                    case ']': return 0xDD;
                    case '\\': return 0xDC;
                    case ';': return 0xBA;
                    case '\'': return 0xDE;
                    case ',': return 0xBC;
                    case '.': return 0xBE;
                    case '/': return 0xBF;
                    case '\u2191': return 0x26;   // ↑
                    case '\u2193': return 0x28;   // ↓
                    case '\u2190': return 0x25;   // ←
                    case '\u2192': return 0x27;   // →
                }
                return 0;
            }
            switch (name)
            {
                case "Esc": return 0x1B;
                case "F1": return 0x70;
                case "F2": return 0x71;
                case "F3": return 0x72;
                case "F4": return 0x73;
                case "F5": return 0x74;
                case "F6": return 0x75;
                case "F7": return 0x76;
                case "F8": return 0x77;
                case "F9": return 0x78;
                case "F10": return 0x79;
                case "F11": return 0x7A;
                case "F12": return 0x7B;
                case "PrtSc": return 0x2C;
                case "ScrLk": return 0x91;
                case "Pause": return 0x13;
                case "Backspace": return 0x08;
                case "Tab": return 0x09;
                case "Caps": return 0x14;
                case "Enter": return 0x0D;
                case "Space": return 0x20;
                case "Ins": return 0x2D;
                case "Del": return 0x2E;
                case "Home": return 0x24;
                case "End": return 0x23;
                case "PgUp": return 0x21;
                case "PgDn": return 0x22;
                case "\u5de6Shift": return 0xA0;   // 左Shift
                case "\u53f3Shift": return 0xA1;   // 右Shift
                case "\u5de6Ctrl": return 0xA2;    // 左Ctrl
                case "\u53f3Ctrl": return 0xA3;    // 右Ctrl
                case "\u5de6Win": return 0x5B;     // 左Win
                case "\u53f3Win": return 0x5C;     // 右Win
                case "\u5de6Alt": return 0xA4;     // 左Alt
                case "\u53f3Alt": return 0xA5;     // 右Alt
                case "Menu": return 0x5D;
            }
            return 0;
        }

        // 点击锁定菜单框内部：标记已处理，避免冒泡到遮罩触发关闭
        private void LockMenu_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        // 键盘折叠开关：点击展开/折叠 87 配列键盘布局（箭头随状态切换 ▼/▲）
        private void KeyPickerToggle_Click(object sender, TappedRoutedEventArgs e)
        {
            if (KeyPickerScroll.Visibility == Visibility.Collapsed)
            {
                KeyPickerScroll.Visibility = Visibility.Visible;
                KeyPickerToggleArrow.Text = "\u25B2";   // ▲ 展开态
            }
            else
            {
                KeyPickerScroll.Visibility = Visibility.Collapsed;
                KeyPickerToggleArrow.Text = "\u25BC";   // ▼ 折叠态
            }
            e.Handled = true;
        }

        // 删除确认框：确认删除
        private void DeleteConfirmYes_Click(object sender, TappedRoutedEventArgs e)
        {
            if (_deleteConfirmKey != null) ConfirmDeleteCustomKey(_deleteConfirmKey);
            _deleteConfirmKey = null;
            DeleteConfirmPanel.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        // 删除确认框：取消
        private void DeleteConfirmNo_Click(object sender, TappedRoutedEventArgs e)
        {
            _deleteConfirmKey = null;
            DeleteConfirmPanel.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        // 删除确认框：点遮罩关闭
        private void DeleteConfirmPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            _deleteConfirmKey = null;
            DeleteConfirmPanel.Visibility = Visibility.Collapsed;
        }

        // 点击遮罩（菜单框外）：收起锁定菜单
        private void LockPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            LockPanel.Visibility = Visibility.Collapsed;
            DiagLog("lock menu closed by mask");
        }

        // 点击锁定菜单的关闭按钮：收起锁定菜单
        private void LockClose_Click(object sender, TappedRoutedEventArgs e)
        {
            LockPanel.Visibility = Visibility.Collapsed;
            DiagLog("lock menu closed by btn");
        }

        // 锁定菜单里的布局锁定开关：与设置面板的锁定开关共用同一逻辑（两处"开/关"文本由 ApplySettingsColors 统一刷新）
        private void LockSwitch_Click(object sender, TappedRoutedEventArgs e)
        {
            ToggleLayoutLock();
        }

        // 锁定菜单里的重置按键布局：与设置面板的重置按钮共用同一逻辑
        private void LockReset_Click(object sender, TappedRoutedEventArgs e)
        {
            PerformLayoutReset();
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

        // 锁定布局开关共用逻辑（设置面板与二级锁定菜单的开关都走这里）：
        // 翻转 _layoutLocked、写回 LocalSettings、重置高亮/光标、刷新配色（锁定菜单"开/关"文本由 ApplySettingsColors 统一刷新）
        private void ToggleLayoutLock()
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

        // 按下即启动 200ms 长按计时（所有键统一挂，仅自定义键生效）
        private void StartLongPress(Border b)
        {
            CancelLongPress();
            if (_longPressTimer == null)
            {
                _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _longPressTimer.Tick += LongPress_Tick;
            }
            _longPressKey = b;
            _longPressTimer.Start();
        }

        // 指针移动/松开/离开/丢捕获都取消长按计时
        private void CancelLongPress()
        {
            if (_longPressTimer != null) _longPressTimer.Stop();
            _longPressKey = null;
        }

        // 长按 200ms：进入移动模式（默认键/鼠标键/自定义键均可移动）；缩放拖拽中或锁定时不触发
        private void LongPress_Tick(object sender, object e)
        {
            _longPressTimer.Stop();
            var b = _longPressKey;
            _longPressKey = null;
            if (b == null) return;
            if (_dragKey != null) return;   // 正在边缘缩放拖拽中，不进入移动模式
            if (_layoutLocked) return;      // 锁定布局时禁止移动控件（与缩放一致）
            // 进入移动模式：记录起点（按下时的指针/边距），高亮提示（琥珀色边框）
            _moveKey = b;
            _moveStartX = _pressPointerRoot.X;
            _moveStartY = _pressPointerRoot.Y;
            _moveStartML = b.Margin.Left;
            _moveStartMT = b.Margin.Top;
            b.BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD5, 0x4F));   // 琥珀 #FFD54F
            DiagLog("move mode: " + (b.Tag ?? "?"));
        }

        // 确认删除自定义键：移除字典/面板/LocalSettings/CustomPos，并清理状态
        private void ConfirmDeleteCustomKey(Border b)
        {
            string name = b.Tag as string;
            if (string.IsNullOrEmpty(name)) return;
            if (!_customKeys.Remove(name)) return;
            CustomKeysPanel.Children.Remove(b);
            ApplicationData.Current.LocalSettings.Values.Remove("Custom_" + name);
            ApplicationData.Current.LocalSettings.Values.Remove("CustomPos_" + name);
            if (_customKeys.Count == 0) CustomKeysPanel.Visibility = Visibility.Collapsed;
            _deleteConfirmKey = null;
            DeleteConfirmPanel.Visibility = Visibility.Collapsed;
            CancelLongPress();
            DiagLog("custom key deleted: " + name);
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
                if (cw == null) return;
                // 恢复默认时赋回保存的初始光标；若未保存（极端情况）则用 Arrow 兜底，绝不赋 null
                if (target == null)
                {
                    var def = _defaultCursor;
                    if (def == null) def = new CoreCursor(CoreCursorType.Arrow, 0);
                    cw.PointerCursor = def;
                }
                else
                {
                    cw.PointerCursor = new CoreCursor(target.Value, 0);
                }
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

        // 按下：右键→删除确认（仅自定义键）；非右键→启动长按计时 + 边缘缩放判定
        private void Key_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var b = sender as Border;
            if (b == null) return;
            // 右键：仅自定义键弹删除确认框；先取消待决长按并防护拖拽/移动中的状态
            if (e.GetCurrentPoint(b).Properties.IsRightButtonPressed)
            {
                CancelLongPress();
                if (_dragKey != null || _moveKey != null)
                {
                    e.Handled = true;
                    return;
                }
                string nm = b.Tag as string;
                if (nm != null && _customKeys.ContainsKey(nm))
                {
                    _deleteConfirmKey = b;
                    DeleteConfirmText.Text = "\u5220\u9664\u63a7\u4ef6 " + nm + " \uff1f";   // 删除控件 <名> ？
                    DeleteConfirmPanel.Visibility = Visibility.Visible;
                    DiagLog("delete confirm: " + nm);
                }
                e.Handled = true;
                return;
            }
            _pressPointerRoot = e.GetCurrentPoint(null).Position;
            StartLongPress(b);
            // 捕获指针：保证长按移动模式中指针移出按键仍持续收到 PointerMoved/Released
            try { b.CapturePointer(e.Pointer); } catch { }
            if (_layoutLocked) return;
            string mode = HitTestEdge(b, e.GetCurrentPoint(b).Position);
            if (mode == null) return;
            ApplyCursor(mode);
            _dragKey = b;
            _dragMode = mode;
            _dragStartX = _pressPointerRoot.X;
            _dragStartY = _pressPointerRoot.Y;
            _dragStartW = b.Width;
            _dragStartH = b.Height;
            _dragStartML = b.Margin.Left;
            _dragStartMT = b.Margin.Top;
            e.Handled = true;
        }

        // 移动：移动模式中平移位置；拖拽中实时缩放；未拖拽且解锁时更新边缘高亮。
        // 长按取消带位移阈值：微小走动（<15px）不打断长按计时，保证长按移动能稳定触发。
        private void Key_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var b = sender as Border;
            if (b == null) return;
            if (_moveKey != null)
            {
                var key = _moveKey;
                if (b != key) return;
                double dx = e.GetCurrentPoint(null).Position.X - _moveStartX;
                double dy = e.GetCurrentPoint(null).Position.Y - _moveStartY;
                key.Margin = new Thickness(_moveStartML + dx, _moveStartMT + dy, key.Margin.Right, key.Margin.Bottom);
                return;
            }
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
            // 长按计时中：位移超阈值（15px）才取消长按
            if (_longPressTimer != null && _longPressTimer.IsEnabled)
            {
                double dxe = e.GetCurrentPoint(null).Position.X - _pressPointerRoot.X;
                double dye = e.GetCurrentPoint(null).Position.Y - _pressPointerRoot.Y;
                if (dxe * dxe + dye * dye > 225.0) CancelLongPress();   // 15px 阈值
            }
            if (_layoutLocked) return;
            var mode = HitTestEdge(b, e.GetCurrentPoint(b).Position);
            if (mode != null) SetHover(b, mode);
            else ClearHover();
        }

        // 松开：移动模式落位持久化；缩放拖拽结束持久化；一律取消长按
        private void Key_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            CancelLongPress();
            if (_moveKey != null)
            {
                var key = _moveKey;
                _moveKey = null;
                try { key.ReleasePointerCapture(e.Pointer); } catch { }
                SetKey(key, false);   // 恢复普通主题样式（清移动高亮）
                string nm = key.Tag as string;
                if (!string.IsNullOrEmpty(nm))
                {
                    if (_customKeys.ContainsKey(nm))
                    {
                        ApplicationData.Current.LocalSettings.Values["CustomPos_" + nm] =
                            (int)key.Margin.Left + ";" + (int)key.Margin.Top;
                    }
                    else
                    {
                        SaveKeyLayout(nm, key);   // 默认键走现有 Layout_ 持久化
                    }
                    DiagLog("key moved " + nm + " ml=" + (int)key.Margin.Left + " mt=" + (int)key.Margin.Top);
                }
                return;
            }
            if (_dragKey == null) return;
            var dragKey = _dragKey;   // 释放捕获会同步触发 CaptureLost 置空 _dragKey，先存局部变量
            try { dragKey.ReleasePointerCapture(e.Pointer); } catch { }
            DiagLog("layout resize " + NameOf(dragKey)
                    + " w=" + (int)dragKey.Width + " h=" + (int)dragKey.Height
                    + " ml=" + (int)dragKey.Margin.Left + " mt=" + (int)dragKey.Margin.Top);
            _dragKey = null;
            _dragMode = null;
            ApplyCursor(null);   // 松开后恢复默认光标
            SaveLayout();
        }

        private void Key_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            CancelLongPress();   // 移出按键即取消长按
            if (_moveKey != null) return;
            if (_dragKey != null) return;
            ClearHover();
        }

        private void Key_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            CancelLongPress();   // 丢捕获也取消长按
            if (_moveKey != null)
            {
                var key = _moveKey;
                _moveKey = null;
                SetKey(key, false);   // 丢捕获视为落位，恢复样式
            }
            _dragKey = null;
            _dragMode = null;
            ApplyCursor(null);   // 异常丢捕获（失焦/窗口切换）也恢复默认光标，避免 Size 光标残留
        }

        private string NameOf(Border b)
        {
            foreach (var kv in _keys) if (kv.Value == b) return kv.Key;
            foreach (var kv in _mouse) if (kv.Value == b) return kv.Key;
            foreach (var kv in _customKeys) if (kv.Value == b) return kv.Key;
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