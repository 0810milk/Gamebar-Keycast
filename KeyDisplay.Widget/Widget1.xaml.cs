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
        private const double MinPadW = 40, MinPadH = 36;   // 鼠标垫等比缩放最小尺寸（沿用 ComputePadSize 的 MinW/MinH）
        private const string LayoutPrefix = "Layout_";
        // 吸附对齐（0.5.0）：拖动/缩放时按周边按键十字方向边对边贴齐；软化——接近 SnapNear 触发、偏离 SnapRelease 脱离
        private const double SnapNear = 8.0;         // 触发吸附的边距（px）
        private const double SnapRelease = 10.0;     // 脱离吸附的边距（px，大于 SnapNear 形成滞回，避免抖动）
        private const double SnapHintNear = 40.0;    // 接近提示阈值（px）：8~40 显示半透明虚线，>40 不显示
        private static readonly Color SnapLineColor = Color.FromArgb(0xFF, 0x4A, 0x9E, 0xFF);   // 浅蓝 #4A9EFF（吸中实线）
        private static readonly Color SnapHintColor = Color.FromArgb(0x80, 0x4A, 0x9E, 0xFF);   // 半透明浅蓝（接近提示虚线，约 50%）
        private bool _layoutLocked = true;           // true=锁定（不可调整），默认开
        private Border _dragKey;                     // 当前拖拽中的按键
        private string _dragMode;                    // l/r/t/b/tl/tr/bl/br
        private double _dragStartX, _dragStartY;
        private double _dragStartW, _dragStartH;
        private double _dragStartML, _dragStartMT;
        private bool _padCustomized;                 // 用户是否已自定义过鼠标垫（首次移动/缩放后置位，UpdatePadSize 据此跳过自动跟随）
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

        // 吸附对齐（0.5.0）：参考线对象池（最多 4 条复用，避免频繁分配）+ 拖动起点的视觉基准坐标（SnapCanvas 坐标系）
        private readonly Line[] _snapLines = new Line[4];   // 吸参考线池（贯穿线，实线=吸中 / 虚线=接近提示）
        private bool _snapActiveH, _snapActiveV;            // 水平/垂直轴是否正处吸附态（滞回：吸住后偏离 >SnapRelease 才脱离）
        private double _moveBaseLeft, _moveBaseTop;         // 移动起点：被拖按键的视觉左/上（SnapCanvas 坐标，用于拖动中免 TransformToVisual 反推）
        private double _dragBaseLeft, _dragBaseTop;         // 缩放起点：被调按键的视觉左/上（SnapCanvas 坐标）
        private readonly SolidColorBrush _snapSolid = new SolidColorBrush(SnapLineColor);   // 吸中实线画刷
        private readonly SolidColorBrush _snapDash = new SolidColorBrush(SnapHintColor);    // 接近提示虚线画刷

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

            // 吸附参考线池：预先创建 4 条 Line 加入 SnapCanvas（默认隐藏），吸附时复用对象而非频繁分配。
            // 实线/虚线、实色/半透明由显示时按距离分级动态切换（见 UpdateSnapLine）。
            for (int i = 0; i < _snapLines.Length; i++)
            {
                var line = new Line
                {
                    Stroke = _snapSolid,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4.0, 3.0 },
                    Visibility = Visibility.Collapsed
                };
                _snapLines[i] = line;
                SnapCanvas.Children.Add(line);
            }

            _keys["Q"] = KeyQ; _keys["W"] = KeyW; _keys["E"] = KeyE; _keys["R"] = KeyR;
            _keys["A"] = KeyA; _keys["S"] = KeyS; _keys["D"] = KeyD; _keys["F"] = KeyF;
            _keys["Shift"] = KeyShift; _keys["Ctrl"] = KeyCtrl; _keys["Alt"] = KeyAlt; _keys["Space"] = KeySpace;
            _mouse["L"] = MouseL; _mouse["M"] = MouseM; _mouse["MR"] = MouseR;   // MR：避免与键盘 R 的 Layout_R 冲突
            _mouse["X1"] = MouseX1; _mouse["X2"] = MouseX2;

            // 布局自定义：所有按键/鼠标键附加指针处理（边缘/四角拖拽缩放）；鼠标垫也参与（长按移动 + 等比缩放）
            foreach (var kv in _keys) AttachResize(kv.Value);
            foreach (var kv in _mouse) AttachResize(kv.Value);
            // 鼠标垫：让内部 Canvas/点不拦截指针，保证事件落到 MousePad Border 本身
            MousePadCanvas.IsHitTestVisible = false;
            MouseDot.IsHitTestVisible = false;
            AttachResize(MousePad);
            RestoreLayout();
            RestorePadCustom();
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
            if (_moveKey != null) { EndMoveStyle(_moveKey); _moveKey = null; }   // 主题切换时清除移动高亮（鼠标垫走专属恢复）
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

        // 移动落位/丢捕获时恢复按键样式：普通键走 SetKey(false)；鼠标垫恢复其专属半透明背景（避免被默认键样式覆盖）
        private void EndMoveStyle(Border key)
        {
            if (key == MousePad)
            {
                MousePad.Background = _dark ? _darkPad : _lightPad;
                MousePad.BorderBrush = _dark ? _darkBorder : _lightBorder;
            }
            else
            {
                SetKey(key, false);
            }
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
        // 用户自定义过鼠标垫（移动/缩放）后 _padCustomized=true，跳过自动跟随，保留用户尺寸。
        private void UpdatePadSize(int vsW, int vsH)
        {
            if (_padCustomized) return;
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

        // ===================== 鼠标垫等比缩放（0.5.0）=====================
        // 任意边/四角拖动鼠标垫都等比例变化宽高（比例 = 拖动起点 _dragStartW/_dragStartH）。
        // 主导轴决定缩放：纯水平边（l/r）由 dx 主导；纯垂直边（t/b）由 dy 主导；
        // 四角比较 |dx| 与 |dy|（换算到同一量纲）取变化更大的轴，保证比例一致不漂移。
        // 锚点：左/右/上/下"被拖动的边"移动，其"对边"保持不动（固定锚）。
        // 最小尺寸：等比同比例钳制到 MinPadW/MinPadH（沿用 ComputePadSize 的 40/36）。
        private void ComputePadEqualScale(ref double w, ref double h, ref double ml, ref double mt, double dx, double dy)
        {
            double w0 = _dragStartW, h0 = _dragStartH;
            double k = h0 / w0;   // 宽→高比例（等比约束系数）
            bool hasH = _dragMode.Contains("l") || _dragMode.Contains("r");
            bool hasV = _dragMode.Contains("t") || _dragMode.Contains("b");
            bool horizontalDominant;
            // 决定主导轴：单边直接按其轴；四角按 |dx|/w0 与 |dy|/h0 比较谁更大
            if (hasH && hasV)
            {
                horizontalDominant = (Math.Abs(dx) / w0) >= (Math.Abs(dy) / h0);
            }
            else
            {
                horizontalDominant = hasH;
            }
            if (horizontalDominant)
            {
                // 水平主导：w 由 dx 决定，h = w * k
                double wNew = _dragMode.Contains("l") ? w0 - dx : w0 + dx;
                double wMin = Math.Max(MinPadW, MinPadH / k);   // 等比同时满足 w、h 下限
                wNew = Math.Max(wMin, wNew);
                w = wNew;
                h = wNew * k;
                if (_dragMode.Contains("l")) ml = _dragStartML + (w0 - wNew);   // 右缘不动
                else ml = _dragStartML;                                        // 左缘不动
                mt = _dragStartMT;   // 顶部固定不动
            }
            else
            {
                // 垂直主导：h 由 dy 决定，w = h / k
                double hNew = _dragMode.Contains("t") ? h0 - dy : h0 + dy;
                double hMin = Math.Max(MinPadH, MinPadW * k);   // 等比同时满足 h、w 下限
                hNew = Math.Max(hMin, hNew);
                h = hNew;
                w = hNew / k;
                if (_dragMode.Contains("t")) mt = _dragStartMT + (h0 - hNew);   // 下缘不动
                else mt = _dragStartMT;                                          // 上缘不动
                ml = _dragStartML;   // 左边固定不动
            }
        }

        // 鼠标垫自定义持久化：写 PadPos_left/top、PadW/H（InvariantCulture）、PadCustom_=1，并置 _padCustomized=true
        private void SavePadCustom()
        {
            _padCustomized = true;
            var v = ApplicationData.Current.LocalSettings.Values;
            v["PadCustom_"] = 1;
            v["PadPos_left"] = MousePad.Margin.Left.ToString(CultureInfo.InvariantCulture);
            v["PadPos_top"] = MousePad.Margin.Top.ToString(CultureInfo.InvariantCulture);
            v["PadW"] = MousePad.Width.ToString(CultureInfo.InvariantCulture);
            v["PadH"] = MousePad.Height.ToString(CultureInfo.InvariantCulture);
            DiagLog("pad customized ml=" + (int)MousePad.Margin.Left + " mt=" + (int)MousePad.Margin.Top
                    + " w=" + (int)MousePad.Width + " h=" + (int)MousePad.Height);
        }

        // 启动恢复鼠标垫自定义：PadCustom_=1 时应用保存的 Margin/Width/Height 并置 _padCustomized=true；
        // 否则保持自动跟随（_padCustomized=false）。
        private void RestorePadCustom()
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                object pc = v["PadCustom_"];
                if (pc == null) { _padCustomized = false; return; }
                bool customized = (pc is bool b) ? b : (pc.ToString() == "1" || pc.ToString() == "True");
                if (!customized) { _padCustomized = false; return; }
                double ml = ReadSettingDouble(v, "PadPos_left", 0.0);
                double mt = ReadSettingDouble(v, "PadPos_top", 0.0);
                double w = ReadSettingDouble(v, "PadW", MousePad.Width);
                double h = ReadSettingDouble(v, "PadH", MousePad.Height);
                if (w < MinPadW || h < MinPadH) { _padCustomized = false; return; }
                MousePad.Margin = new Thickness(ml, mt, 0, 0);
                MousePad.Width = w;
                MousePad.Height = h;
                _padW = w;   // 同步垫面尺寸变量，保证鼠标点映射基准与实际尺寸一致
                _padH = h;
                _padCustomized = true;
                DiagLog("pad restored ml=" + (int)ml + " mt=" + (int)mt + " w=" + (int)w + " h=" + (int)h);
            }
            catch (Exception ex)
            {
                _padCustomized = false;
                DiagLog("pad restore fail: " + ex.Message);
            }
        }

        // 从 LocalSettings 读 double（值可能是 string 或 double/其他数值类型，统一安全解析）
        private static double ReadSettingDouble(Windows.Foundation.Collections.IPropertySet values, string key, double fallback)
        {
            object o = values[key];
            if (o == null) return fallback;
            if (o is double d) return d;
            if (o is float f) return f;
            if (o is int i) return i;
            double r;
            if (double.TryParse(o.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            return fallback;
        }

        // 重置鼠标垫后立即恢复自动跟随：用当前快照 vs 尺寸刷新一次（无快照则回退默认 1920×1080）
        private void RefreshPadAutoSize()
        {
            var snap = _latest;
            int vsW = snap != null ? snap.VsW : 1920;
            int vsH = snap != null ? snap.VsH : 1080;
            double w, h;
            ComputePadSize(vsW, vsH, out w, out h);
            _padW = w;
            _padH = h;
            MousePad.Width = w;
            MousePad.Height = h;
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
            // 重置鼠标垫：清除自定义持久化、_padCustomized=false、恢复自动跟随（立即用当前 vs 尺寸刷新）
            var padVals = ApplicationData.Current.LocalSettings.Values;
            padVals.Remove("PadCustom_");
            padVals.Remove("PadPos_left");
            padVals.Remove("PadPos_top");
            padVals.Remove("PadW");
            padVals.Remove("PadH");
            _padCustomized = false;
            MousePad.Margin = new Thickness(0, 0, 0, 0);
            RefreshPadAutoSize();
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
            bool isPad = name == "触摸板";
            var border = new Border
            {
                Width = isPad ? 80 : CustomKeyWidth(name),
                Height = isPad ? 80 : 48,
                CornerRadius = new CornerRadius(isPad ? 8 : 6),
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
                return -1;   // 未识别单字符 → 越界，渲染循环视为未按下
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
            return -1;   // 未识别键名（如"触摸板"）→ 越界，渲染循环视为未按下，绝不抛异常
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
            if (b == MousePad) _padCustomized = true;   // 一旦开始移动鼠标垫即视为自定义，避免操作期间被自动跟随覆盖
            _moveStartX = _pressPointerRoot.X;
            _moveStartY = _pressPointerRoot.Y;
            _moveStartML = b.Margin.Left;
            _moveStartMT = b.Margin.Top;
            // 记录起点视觉基准（SnapCanvas 坐标），供吸附计算反推被拖按键四边
            var baseRect = VisualRectOf(b);
            _moveBaseLeft = baseRect.X;
            _moveBaseTop = baseRect.Y;
            _snapActiveH = false;   // 进入移动时重置吸附滞回态
            _snapActiveV = false;
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
            if (b == MousePad) _padCustomized = true;   // 一旦开始缩放鼠标垫即视为自定义，避免操作期间被自动跟随覆盖
            // 记录起点视觉基准（SnapCanvas 坐标），供缩放吸附反推被调边
            var baseRect = VisualRectOf(b);
            _dragBaseLeft = baseRect.X;
            _dragBaseTop = baseRect.Y;
            _snapActiveH = false;   // 进入缩放时重置吸附滞回态
            _snapActiveV = false;
            HideSnapLines();
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
                // 自由位置（无吸附）作为候选，再按十字方向做边对边软吸附（水平/垂直轴独立）
                double ml = _moveStartML + dx;
                double mt = _moveStartMT + dy;
                double w = (key.ActualWidth > 0 ? key.ActualWidth : key.Width);
                double h = (key.ActualHeight > 0 ? key.ActualHeight : key.Height);
                // 被拖按键当前四边（SnapCanvas 坐标）：起点视觉基准 + Margin 位移增量（免布局刷新）
                double[] ea = new double[4];
                ea[0] = _moveBaseLeft + (ml - _moveStartML);
                ea[1] = ea[0] + w;
                ea[2] = _moveBaseTop + (mt - _moveStartMT);
                ea[3] = ea[2] + h;
                var rects = CollectOtherRects(key);
                var hitH = ComputeAxisSnap(true, ea, rects);
                var hitV = ComputeAxisSnap(false, ea, rects);
                // 滞回：未吸附 ≤8 触发、已吸附 ≤10 保持、>10 脱离；两轴独立，只修正吸附到的轴
                bool snapH = hitH.Active && ShouldSnap(hitH.Delta, ref _snapActiveH);
                bool snapV = hitV.Active && ShouldSnap(hitV.Delta, ref _snapActiveV);
                if (snapH) ml += hitH.Delta;
                if (snapV) mt += hitV.Delta;
                key.Margin = new Thickness(ml, mt, key.Margin.Right, key.Margin.Bottom);
                // 参考线分级显示（v2）：吸中=实线、8~40px=半透明虚线、>40px 或无候选=隐藏；每轴最近 1 条
                UpdateSnapLine(0, false, hitH.LinePos, hitH.Active ? Math.Abs(hitH.Delta) : double.MaxValue, snapH);
                UpdateSnapLine(1, true, hitV.LinePos, hitV.Active ? Math.Abs(hitV.Delta) : double.MaxValue, snapV);
                return;
            }
            if (_dragKey != null)
            {
                var key = _dragKey;   // 捕获期间 CaptureLost 可能已把 _dragKey 置空，用局部变量
                if (b != key) return;
                double dx = e.GetCurrentPoint(null).Position.X - _dragStartX;
                double dy = e.GetCurrentPoint(null).Position.Y - _dragStartY;
                double w = _dragStartW, h = _dragStartH, ml = _dragStartML, mt = _dragStartMT;
                if (key == MousePad)
                {
                    // 鼠标垫：任意边/四角拖动都等比例缩放（宽高比 = 起点比例），不做自由缩放、不做缩放吸附。
                    ComputePadEqualScale(ref w, ref h, ref ml, ref mt, dx, dy);
                    key.Width = w;
                    key.Height = h;
                    key.Margin = new Thickness(ml, mt, key.Margin.Right, key.Margin.Bottom);
                    return;
                }
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

                // 缩放吸附：对"被调整的边"做十字方向边对边贴齐（与移动模式同一套判定），
                // 仍受 MinKeyW/MinKeyH 约束——吸附修正不得缩破最小值。
                ApplyDragSnap(key, ref w, ref h, ref ml, ref mt);

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
                EndMoveStyle(key);   // 恢复样式（鼠标垫走专属恢复，其余 SetKey(false) 清移动高亮）
                HideSnapLines();      // 落位隐藏吸附参考线
                string nm = key.Tag as string;
                if (!string.IsNullOrEmpty(nm))
                {
                    if (nm == "Pad")
                    {
                        SavePadCustom();   // 鼠标垫移动：写 Pad 持久化（不影响 Layout_ 键）
                    }
                    else if (_customKeys.ContainsKey(nm))
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
            HideSnapLines();     // 缩放结束隐藏吸附参考线
            if (dragKey == MousePad)
            {
                // 鼠标垫缩放落位：持久化并同步垫面尺寸变量（保证点映射基准 = 实际尺寸），不走 SaveLayout
                _padW = MousePad.Width;
                _padH = MousePad.Height;
                SavePadCustom();
            }
            else
            {
                SaveLayout();
            }
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
                EndMoveStyle(key);   // 丢捕获视为落位，恢复样式（鼠标垫走专属恢复）
            }
            _dragKey = null;
            _dragMode = null;
            ApplyCursor(null);   // 异常丢捕获（失焦/窗口切换）也恢复默认光标，避免 Size 光标残留
            HideSnapLines();     // 丢捕获兜底隐藏吸附参考线，防残留
        }

        // ===================== 吸附对齐（0.5.0）=====================
        // 统一坐标系：SnapCanvas（最外层覆盖层），与被拖按键所在容器无关，跨 StackPanel 也能正确比较视觉边。
        // 关键：拖动中只用"起点视觉坐标 + 位移增量"反推被拖按键的边，避免改 Margin 后布局未刷新导致 TransformToVisual 读到旧值。

        // 吸附判定结果：单轴的贴边修正量 + 参考线位置
        private struct SnapHit
        {
            public bool Active;          // 本轴是否吸附
            public double Delta;         // 修正量（加到被拖按键的 Margin.Left 或 Margin.Top）
            public double LinePos;       // 参考线贴齐位置（水平吸附=x，垂直吸附=y）
        }

        // 读取按键在 SnapCanvas 坐标系下的视觉矩形（用于"其他按键 B"——拖动中它们静止，布局稳定，可实时读）
        private Rect VisualRectOf(Border b)
        {
            try
            {
                var t = b.TransformToVisual(SnapCanvas);
                var tl = t.TransformPoint(new Point(0, 0));
                double w = b.ActualWidth > 0 ? b.ActualWidth : b.Width;
                double h = b.ActualHeight > 0 ? b.ActualHeight : b.Height;
                return new Rect(tl.X, tl.Y, w, h);
            }
            catch
            {
                return Rect.Empty;
            }
        }

        // 对单轴做全局参考线吸附（v2）：去掉"投影重叠"限制，所有其他控件的四条边都是候选参考线。
        //   horizontal：A 左/右缘分别去贴 B 的左/右缘（左对齐/右对齐/贴边自动覆盖）
        //   vertical  ：A 上/下缘分别去贴 B 的上/下缘（上对齐/下对齐/贴边自动覆盖）
        // edgesA = 被拖按键当前四边（SnapCanvas 坐标，[0]=左 [1]=右 [2]=上 [3]=下）；rectsB = 其他按键视觉矩形列表。
        // 遍历全部边对，取 |Delta| 最小者作为该轴候选（隔空也能对齐，如 A 左缘贴远处 B 左缘 = 左对齐）。
        private SnapHit ComputeAxisSnap(bool horizontal, double[] edgesA, List<Rect> rectsB)
        {
            var hit = new SnapHit { Active = false };
            double best = double.MaxValue;
            foreach (var r in rectsB)
            {
                if (r.Width <= 0 || r.Height <= 0) continue;
                if (horizontal)
                {
                    // A 左缘 vs B 左缘（左对齐）、A 左缘 vs B 右缘（贴边）、A 右缘 vs B 左缘（贴边）、A 右缘 vs B 右缘（右对齐）
                    double bLeft = r.X, bRight = r.X + r.Width;
                    double dAL = bLeft - edgesA[0];       // A 左缘贴 B 左缘（左对齐）
                    double dALR = bRight - edgesA[0];     // A 左缘贴 B 右缘（A 在 B 右侧贴边）
                    double dARL = bLeft - edgesA[1];      // A 右缘贴 B 左缘（A 在 B 左侧贴边）
                    double dAR = bRight - edgesA[1];      // A 右缘贴 B 右缘（右对齐）
                    double d = MinAbs4(dAL, dALR, dARL, dAR);
                    if (Math.Abs(d) < Math.Abs(best))
                    {
                        best = d;
                        hit.Delta = d;
                        if (d == dAL || d == dALR) hit.LinePos = (d == dAL) ? bLeft : bRight;   // 由 A 左缘吸附
                        else hit.LinePos = (d == dARL) ? bLeft : bRight;                         // 由 A 右缘吸附
                    }
                }
                else
                {
                    double bTop = r.Y, bBot = r.Y + r.Height;
                    double dAT = bTop - edgesA[2];        // A 上缘贴 B 上缘（上对齐）
                    double dATB = bBot - edgesA[2];       // A 上缘贴 B 下缘（A 在 B 下方贴边）
                    double dABT = bTop - edgesA[3];       // A 下缘贴 B 上缘（A 在 B 上方贴边）
                    double dAB = bBot - edgesA[3];        // A 下缘贴 B 下缘（下对齐）
                    double d = MinAbs4(dAT, dATB, dABT, dAB);
                    if (Math.Abs(d) < Math.Abs(best))
                    {
                        best = d;
                        hit.Delta = d;
                        if (d == dAT || d == dATB) hit.LinePos = (d == dAT) ? bTop : bBot;   // 由 A 上缘吸附
                        else hit.LinePos = (d == dABT) ? bTop : bBot;                         // 由 A 下缘吸附
                    }
                }
            }
            if (best != double.MaxValue) hit.Active = true;
            return hit;
        }

        // 四个距离里取绝对值最小者（符号保留，用于确定修正方向与贴合的边）
        private static double MinAbs4(double a, double b, double c, double d)
        {
            double r = a;
            if (Math.Abs(b) < Math.Abs(r)) r = b;
            if (Math.Abs(c) < Math.Abs(r)) r = c;
            if (Math.Abs(d) < Math.Abs(r)) r = d;
            return r;
        }

        // 滞回判定：未吸附时 |Delta|≤SnapNear 才触发；已吸附时 |Delta|≤SnapRelease 保持（>SnapRelease 脱离）。
        // 永不锁死：一旦偏离超 SnapRelease，本轴立即回到自由位置。
        private static bool ShouldSnap(double delta, ref bool active)
        {
            double a = Math.Abs(delta);
            if (active)
            {
                if (a > SnapRelease) { active = false; return false; }
                return true;
            }
            if (a <= SnapNear) { active = true; return true; }
            return false;
        }

        // 收集"其他按键"（除 exclude 外全部参与按键）在 SnapCanvas 坐标系下的视觉矩形
        private List<Rect> CollectOtherRects(Border exclude)
        {
            var rects = new List<Rect>();
            foreach (var kv in _keys) if (kv.Value != exclude) rects.Add(VisualRectOf(kv.Value));
            foreach (var kv in _mouse) if (kv.Value != exclude) rects.Add(VisualRectOf(kv.Value));
            foreach (var kv in _customKeys) if (kv.Value != exclude) rects.Add(VisualRectOf(kv.Value));
            return rects;
        }

        // 缩放吸附：对"被调整的边"做十字方向边对边贴齐（统一 SnapCanvas 坐标）。
        // 被调边由 _dragMode 决定（含 l/r/t/b）；只吸附被调整的那条边（对应的固定边不动）。
        // 修正直接写回 w/h/ml/mt，仍受调用方的最小尺寸保护。
        private void ApplyDragSnap(Border key, ref double w, ref double h, ref double ml, ref double mt)
        {
            // 被调按键当前四边（SnapCanvas 坐标）：起点视觉基准 + 缩放增量反推
            double baseL = _dragBaseLeft, baseT = _dragBaseTop;
            double baseRight = baseL + _dragStartW, baseBot = baseT + _dragStartH;
            double left = baseL + (ml - _dragStartML);
            double top = baseT + (mt - _dragStartMT);
            double right = left + w;
            double bot = top + h;
            var rects = CollectOtherRects(key);
            bool anyH = false, anyV = false;
            SnapHit hitH = new SnapHit(), hitV = new SnapHit();

            // 水平被调边：l（左缘移动，右缘固定）或 r（右缘移动，左缘固定）
            if (_dragMode.Contains("r"))
            {
                // 右缘贴 B 左缘（A 在 B 左）或 B 右缘（A 在 B 右取更近）——只比较右缘 vs 对方左/右缘
                hitH = SnapRightEdge(left, right, top, bot, rects);
                anyH = true;
            }
            else if (_dragMode.Contains("l"))
            {
                hitH = SnapLeftEdge(left, right, top, bot, rects);
                anyH = true;
            }
            // 垂直被调边：t（上缘移动）或 b（下缘移动）
            if (_dragMode.Contains("b"))
            {
                hitV = SnapBottomEdge(left, right, top, bot, rects);
                anyV = true;
            }
            else if (_dragMode.Contains("t"))
            {
                hitV = SnapTopEdge(left, right, top, bot, rects);
                anyV = true;
            }

            // 保存原始 |Delta|（ShouldSnap 前），用于参考线分级；吸中换算进 w/h 后 hitX.Delta 会清零
            double absH = hitH.Active ? Math.Abs(hitH.Delta) : double.MaxValue;
            double absV = hitV.Active ? Math.Abs(hitV.Delta) : double.MaxValue;

            bool snappedH = anyH && hitH.Active && ShouldSnap(hitH.Delta, ref _snapActiveH);
            bool snappedV = anyV && hitV.Active && ShouldSnap(hitV.Delta, ref _snapActiveV);
            if (snappedH)
            {
                if (_dragMode.Contains("r"))
                {
                    // 右缘移动：delta 加到宽度（左缘固定）
                    w = Math.Max(MinKeyW, w + hitH.Delta);
                }
                else
                {
                    // 左缘移动：delta 加到 ml，宽度反向收缩（右缘固定）
                    ml += hitH.Delta;
                    w = Math.Max(MinKeyW, w - hitH.Delta);
                }
            }
            if (snappedV)
            {
                if (_dragMode.Contains("b"))
                {
                    h = Math.Max(MinKeyH, h + hitV.Delta);
                }
                else
                {
                    mt += hitV.Delta;
                    h = Math.Max(MinKeyH, h - hitV.Delta);
                }
            }

            // 参考线分级显示（v2）：吸中=实线、8~40px=半透明虚线、>40px 或无候选=隐藏；每轴最近 1 条
            // 池索引 0=垂直参考线、1=水平参考线（对应水平/垂直吸附）
            UpdateSnapLine(0, false, hitH.LinePos, absH, snappedH);
            UpdateSnapLine(1, true, hitV.LinePos, absV, snappedV);
        }

        // 缩放右缘（r）：全局模式——A 右缘贴 B 左缘（贴边）或 B 右缘（右对齐），取最近者
        private SnapHit SnapRightEdge(double left, double right, double top, double bot, List<Rect> rects)
        {
            var hit = new SnapHit();
            double best = double.MaxValue;
            foreach (var r in rects)
            {
                if (r.Width <= 0 || r.Height <= 0) continue;
                double d1 = r.X - right;                 // 贴对方左缘
                double d2 = (r.X + r.Width) - right;     // 贴对方右缘
                double d = Math.Abs(d1) <= Math.Abs(d2) ? d1 : d2;
                if (Math.Abs(d) < Math.Abs(best))
                {
                    best = d;
                    hit.Delta = d;
                    hit.LinePos = (d == d1) ? r.X : (r.X + r.Width);
                }
            }
            hit.Active = best != double.MaxValue;
            return hit;
        }

        // 缩放左缘（l）：全局模式——A 左缘贴 B 右缘（贴边）或 B 左缘（左对齐），取最近者
        private SnapHit SnapLeftEdge(double left, double right, double top, double bot, List<Rect> rects)
        {
            var hit = new SnapHit();
            double best = double.MaxValue;
            foreach (var r in rects)
            {
                if (r.Width <= 0 || r.Height <= 0) continue;
                double d1 = (r.X + r.Width) - left;   // 贴对方右缘
                double d2 = r.X - left;               // 贴对方左缘
                double d = Math.Abs(d1) <= Math.Abs(d2) ? d1 : d2;
                if (Math.Abs(d) < Math.Abs(best))
                {
                    best = d;
                    hit.Delta = d;
                    hit.LinePos = (d == d1) ? (r.X + r.Width) : r.X;
                }
            }
            hit.Active = best != double.MaxValue;
            return hit;
        }

        // 缩放下缘（b）：全局模式——A 下缘贴 B 上缘（贴边）或 B 下缘（下对齐），取最近者
        private SnapHit SnapBottomEdge(double left, double right, double top, double bot, List<Rect> rects)
        {
            var hit = new SnapHit();
            double best = double.MaxValue;
            foreach (var r in rects)
            {
                if (r.Width <= 0 || r.Height <= 0) continue;
                double d1 = r.Y - bot;                 // 贴对方上缘
                double d2 = (r.Y + r.Height) - bot;    // 贴对方下缘
                double d = Math.Abs(d1) <= Math.Abs(d2) ? d1 : d2;
                if (Math.Abs(d) < Math.Abs(best))
                {
                    best = d;
                    hit.Delta = d;
                    hit.LinePos = (d == d1) ? r.Y : (r.Y + r.Height);
                }
            }
            hit.Active = best != double.MaxValue;
            return hit;
        }

        // 缩放上缘（t）：全局模式——A 上缘贴 B 下缘（贴边）或 B 上缘（上对齐），取最近者
        private SnapHit SnapTopEdge(double left, double right, double top, double bot, List<Rect> rects)
        {
            var hit = new SnapHit();
            double best = double.MaxValue;
            foreach (var r in rects)
            {
                if (r.Width <= 0 || r.Height <= 0) continue;
                double d1 = (r.Y + r.Height) - top;   // 贴对方下缘
                double d2 = r.Y - top;                // 贴对方上缘
                double d = Math.Abs(d1) <= Math.Abs(d2) ? d1 : d2;
                if (Math.Abs(d) < Math.Abs(best))
                {
                    best = d;
                    hit.Delta = d;
                    hit.LinePos = (d == d1) ? (r.Y + r.Height) : r.Y;
                }
            }
            hit.Active = best != double.MaxValue;
            return hit;
        }

        // 参考线分级显示（v2）：按距离 |delta| 决定样式，并贯穿 SnapCanvas 全宽/全高（全局线效果）。
        //   snapped=true（|delta|≤SnapNear 吸中）→ 浅蓝实线；
        //   8 < |delta| ≤ SnapHintNear → 浅蓝半透明虚线（接近提示）；
        //   >SnapHintNear 或无候选 → 该轴参考线隐藏。
        // 每轴只画最近的 1 条（最近线由调用方选出），屏幕最多同时 2 条（水平+垂直）。
        private void UpdateSnapLine(int lineIndex, bool horizontal, double pos, double absDelta, bool snapped)
        {
            if (lineIndex < 0 || lineIndex >= _snapLines.Length) return;
            var line = _snapLines[lineIndex];
            bool show = snapped || absDelta <= SnapHintNear;
            if (!show)
            {
                line.Visibility = Visibility.Collapsed;
                return;
            }
            double panelW = SnapCanvas.ActualWidth > 0 ? SnapCanvas.ActualWidth : 1920;
            double panelH = SnapCanvas.ActualHeight > 0 ? SnapCanvas.ActualHeight : 1080;
            if (horizontal)
            {
                // 水平参考线：贯穿全宽
                line.X1 = 0; line.Y1 = pos;
                line.X2 = panelW; line.Y2 = pos;
            }
            else
            {
                // 垂直参考线：贯穿全高
                line.X1 = pos; line.Y1 = 0;
                line.X2 = pos; line.Y2 = panelH;
            }
            if (snapped)
            {
                // 吸中：实线 + 实色
                line.Stroke = _snapSolid;
                line.StrokeDashArray = null;
            }
            else
            {
                // 接近提示：半透明虚线
                line.Stroke = _snapDash;
                line.StrokeDashArray = new DoubleCollection { 4.0, 3.0 };
            }
            line.Visibility = Visibility.Visible;
        }

        // 隐藏全部参考线
        private void HideSnapLines()
        {
            for (int i = 0; i < _snapLines.Length; i++)
            {
                if (_snapLines[i] != null) _snapLines[i].Visibility = Visibility.Collapsed;
            }
            _snapActiveH = false;
            _snapActiveV = false;
        }

        private string NameOf(Border b)
        {
            if (b == MousePad) return "Pad";
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