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
        private double _smoothX = -1;   // 平滑后的垫面坐标：-1 = 尚无初始位置
        private double _smoothY = -1;
        private double _targetX;
        private double _targetY;
        private bool _hasSmoothTarget;

        private string _theme = "dark";   // 五态主题："dark"/"gray"/"light"/"pink"/"blue"（黑/灰/白/粉/蓝）+ custom，持久化到 Theme
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
        private bool _padVisible = true;             // 鼠标垫显示/隐藏状态（true=显示；仅切 Visibility，不影响位置/尺寸/transform）
        private Border _hoverKey;                    // 当前边缘悬停高亮的按键
        private string _hoverMode;                   // 当前悬停的边缘模式（l/r/t/b/tl/tr/bl/br，null=无）
        private CoreCursorType? _curCursorType;      // 当前生效的全局光标类型（null=系统默认）
        private CoreCursor _defaultCursor;           // 加载时保存的初始默认光标（恢复用，不赋 null）

        // 长按移动 + 右键删除：200ms 长按进入移动模式（拖动改变位置）；右键自定义键弹删除确认框
        private DispatcherTimer _longPressTimer;
        private Border _longPressKey;
        private Border _moveKey;                       // 当前移动模式中的按键
        private double _moveStartX, _moveStartY;       // 移动按下时的指针位置
        private double _moveStartTX, _moveStartTY;     // 移动按下时的 TranslateTransform 偏移起点
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

        // 粉色主题画刷（用户拍板：字体白色，按键底加深一档保证白字可读）
        private readonly SolidColorBrush _pinkPanel = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xB3, 0xC6));   // 面板 #B3FFB3C6
        private readonly SolidColorBrush _pinkBorder = new SolidColorBrush(Color.FromArgb(0xCC, 0xB0, 0x57, 0x7E));  // 边框 #CCB0577E
        private readonly SolidColorBrush _pinkKeyBg = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xB3, 0xC6));   // 按键默认背景 #FFFFB3C6（原 #FFCDD8 太浅，白字看不清）
        private readonly SolidColorBrush _pinkKeyFg = new SolidColorBrush(Colors.White);       // 默认文字白色
        private readonly SolidColorBrush _pinkPressedBg = new SolidColorBrush(Colors.White);  // 按下白底
        private readonly SolidColorBrush _pinkPressedFg = new SolidColorBrush(Color.FromArgb(0xFF, 0xB0, 0x57, 0x7E));  // 按下深粉字
        private readonly SolidColorBrush _pinkPad = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xB3, 0xC6));      // 鼠标垫 #4DFFB3C6
        private readonly SolidColorBrush _pinkDot = new SolidColorBrush(Color.FromArgb(0xFF, 0xB0, 0x57, 0x7E));     // 鼠标点深粉

        // 灰色主题画刷（浅灰底 + 黑字）
        private readonly SolidColorBrush _grayPanel = new SolidColorBrush(Color.FromArgb(0xB3, 0xD6, 0xD6, 0xD6));   // 面板 #B3D6D6D6
        private readonly SolidColorBrush _grayBorder = new SolidColorBrush(Color.FromArgb(0x66, 0x66, 0x66, 0x66));  // 边框 #66666666
        private readonly SolidColorBrush _grayKeyBg = new SolidColorBrush(Color.FromArgb(0xFF, 0xE4, 0xE4, 0xE4));   // 按键默认背景 #FFE4E4E4
        private readonly SolidColorBrush _grayKeyFg = new SolidColorBrush(Colors.Black);       // 默认文字黑色
        private readonly SolidColorBrush _grayPressedBg = new SolidColorBrush(Color.FromArgb(0xFF, 0x8C, 0x8C, 0x8C));  // 按下深灰底
        private readonly SolidColorBrush _grayPressedFg = new SolidColorBrush(Colors.White);  // 按下白字
        private readonly SolidColorBrush _grayPad = new SolidColorBrush(Color.FromArgb(0x59, 0xD6, 0xD6, 0xD6));      // 鼠标垫 #59D6D6D6
        private readonly SolidColorBrush _grayDot = new SolidColorBrush(Colors.Black);        // 鼠标点黑色

        // 蓝色主题画刷（浅蓝底 + 深蓝字）
        private readonly SolidColorBrush _bluePanel = new SolidColorBrush(Color.FromArgb(0xB3, 0xBF, 0xD9, 0xEE));   // 面板 #B3BFD9EE
        private readonly SolidColorBrush _blueBorder = new SolidColorBrush(Color.FromArgb(0x66, 0x3A, 0x6E, 0xA5));  // 边框 #663A6EA5
        private readonly SolidColorBrush _blueKeyBg = new SolidColorBrush(Color.FromArgb(0xFF, 0xD2, 0xE5, 0xF7));   // 按键默认背景 #FFD2E5F7
        private readonly SolidColorBrush _blueKeyFg = new SolidColorBrush(Color.FromArgb(0xFF, 0x1F, 0x4E, 0x79));   // 默认文字深蓝
        private readonly SolidColorBrush _bluePressedBg = new SolidColorBrush(Colors.White);  // 按下白底
        private readonly SolidColorBrush _bluePressedFg = new SolidColorBrush(Color.FromArgb(0xFF, 0x1F, 0x4E, 0x79));  // 按下深蓝字
        private readonly SolidColorBrush _bluePad = new SolidColorBrush(Color.FromArgb(0x59, 0xBF, 0xD9, 0xEE));      // 鼠标垫 #59BFD9EE
        private readonly SolidColorBrush _blueDot = new SolidColorBrush(Color.FromArgb(0xFF, 0x1F, 0x4E, 0x79));     // 鼠标点深蓝

        // 按键透明度滑条设定值（0~100，默认 100）；锁定开=按此值，锁定关=临时强制 100%
        private double _keyOpacity = 100.0;

        // ===================== 自定义主题色（8 槽位，custom 态）=====================
        // 持久化键（Custom_ 前缀，存 "#RRGGBB"）；缺省回落 dark 预设对应值
        private static readonly string[] CustomKeys = { "CustomPanel_", "CustomBorder_", "CustomKeyBg_", "CustomKeyFg_",
            "CustomPressedBg_", "CustomPressedFg_", "CustomPad_", "CustomDot_" };
        private static readonly string[] SlotNames = { "面板", "边框", "按键底", "文字", "按下底", "按下字", "鼠标垫", "鼠标点" };
        // 动态画刷：custom 态下 8 个语义方法返回它们；启动/修改时用 Custom_ 键刷新
        private readonly SolidColorBrush[] _customBrushes = new SolidColorBrush[8];
        private int _activeSlot = -1;          // 调色区当前作用行（-1=未展开）
        private double _hue = 0.0;             // 当前色相（0~360）
        private double _alpha = 255.0;         // 当前透明度（0~255，调色盘取色套用）
        private bool _syncing = false;         // 程序性文本更新标志（防 TextChanged 递归）
        private Color? _lastPickColor;         // 拖动中最后取色（释放时固化用，避免依赖拖动中不更新的 hex 框）
        private int _lastPickMs;               // 拖动节流时间戳（Environment.TickCount，ms）

        // ===================== 主题配色查询（数据驱动，扩展性）=====================
        // 未来加第六种颜色：新增一个 _xxxXxx 画刷字段 + 在 P()/各语义方法的 blue 参数后追加，或改写成按主题名查字典表即可

        // 五态取画刷：dark/gray/light/pink/blue（黑/灰/白/粉/蓝）
        private Brush P(Brush dark, Brush gray, Brush light, Brush pink, Brush blue) =>
            _theme == "dark" ? dark : _theme == "gray" ? gray : _theme == "light" ? light : _theme == "pink" ? pink : blue;

        // 语义分组查询（每组一语义，避免散落三元）；custom 态返回自定义动态画刷
        private Brush PanelB() => _theme == "custom" ? _customBrushes[0] : P(_darkPanel, _grayPanel, _lightPanel, _pinkPanel, _bluePanel);     // 面板背景
        private Brush BorderB() => _theme == "custom" ? _customBrushes[1] : P(_darkBorder, _grayBorder, _lightBorder, _pinkBorder, _blueBorder); // 边框
        private Brush KeyBgB() => _theme == "custom" ? _customBrushes[2] : P(_darkDefaultBg, _grayKeyBg, _lightDefaultBg, _pinkKeyBg, _blueKeyBg);     // 按键默认背景
        private Brush KeyFgB() => _theme == "custom" ? _customBrushes[3] : P(_darkDefaultFg, _grayKeyFg, _lightDefaultFg, _pinkKeyFg, _blueKeyFg);     // 默认文字
        private Brush PressBgB() => _theme == "custom" ? _customBrushes[4] : P(_darkPressedBg, _grayPressedBg, _lightPressedBg, _pinkPressedBg, _bluePressedBg); // 按下背景
        private Brush PressFgB() => _theme == "custom" ? _customBrushes[5] : P(_darkPressedFg, _grayPressedFg, _lightPressedFg, _pinkPressedFg, _bluePressedFg); // 按下文字
        private Brush PadB() => _theme == "custom" ? _customBrushes[6] : P(_darkPad, _grayPad, _lightPad, _pinkPad, _bluePad);             // 鼠标垫背景
        private Brush DotB() => _theme == "custom" ? _customBrushes[7] : P(_darkDefaultFg, _grayDot, _darkDefaultBg, _pinkDot, _blueDot);  // 鼠标点（dark=白、light=黑、gray=黑、pink=深粉、blue=深蓝）

        // 反色按钮（主题切换/开关类胶囊）：深色主题（dark）=亮胶囊，浅色主题（gray/light/pink/blue）=暗胶囊；custom=文字色底+背景色字（互换反色）
        private Brush InvertKeyBgB() => _theme == "custom" ? _customBrushes[3] : (_theme == "dark" ? _lightDefaultBg : _darkDefaultBg);
        private Brush InvertKeyFgB() => _theme == "custom" ? _customBrushes[2] : (_theme == "dark" ? _lightDefaultFg : _darkDefaultFg);

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

            RegisterDefaultKeys();   // 登记全部默认键（键盘 12 键 + 鼠标 5 键）到字典

            // 布局自定义：所有按键/鼠标键附加指针处理（边缘/四角拖拽缩放）；鼠标垫也参与（长按移动 + 等比缩放）
            foreach (var kv in _keys) AttachResize(kv.Value);
            foreach (var kv in _mouse) AttachResize(kv.Value);
            // 鼠标垫：让内部 Canvas/点不拦截指针，保证事件落到 MousePad Border 本身
            MousePadCanvas.IsHitTestVisible = false;
            MouseDot.IsHitTestVisible = false;
            AttachResize(MousePad);
            RestoreLayout();
            RestoreDeletions();   // 应用"已删默认键"状态（Collapsed + 移除字典）
            RestorePadCustom();
            RestorePadVisibility();
            object layoutLock = ApplicationData.Current.LocalSettings.Values["LayoutLocked"];
            _layoutLocked = (layoutLock is bool lb) ? lb : true;

            object theme = ApplicationData.Current.LocalSettings.Values["Theme"];
            _theme = (theme is string ts && (ts == "light" || ts == "pink" || ts == "gray" || ts == "blue" || ts == "custom")) ? ts : "dark";   // 老数据只有 dark/light，缺失默认 dark

            // 自定义主题色：构建调色盘控件 + 恢复 8 槽自定义值（custom 态生效，预设态忽略）
            InitPickerControls();
            for (int k = 0; k < 8; k++) _customBrushes[k] = new SolidColorBrush(Colors.Black);
            RefreshCustomBrushes();
            if (_theme == "custom") ApplyTheme();   // 语义方法的 custom 分支需要 _theme 已定后应用一次

            // 恢复按键透明度设定值（KeyOpacity_ 存 0~100；缺失默认 100），并同步滑条位置 + 应用（按当前锁定状态）
            object op = ApplicationData.Current.LocalSettings.Values["KeyOpacity_"];
            _keyOpacity = (op is int oi && oi >= 10 && oi <= 100) ? oi : 100.0;
            if (OpacitySlider != null) OpacitySlider.Value = _keyOpacity;
            ApplyKeyOpacity();

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
            RootPanel.Background = PanelB();
            RootPanel.BorderBrush = BorderB();
            MousePad.Background = PadB();
            MousePad.BorderBrush = BorderB();
            MouseDot.Fill = DotB();
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

            ApplicationData.Current.LocalSettings.Values["Theme"] = _theme;
        }

        private void SetKey(Border border, bool down)
        {
            border.Background = down ? PressBgB() : KeyBgB();
            border.BorderBrush = BorderB();
            var tb = border.Child as TextBlock;
            if (tb != null) tb.Foreground = down ? PressFgB() : KeyFgB();
        }

        // 移动落位/丢捕获时恢复按键样式：普通键走 SetKey(false)；鼠标垫恢复其专属半透明背景（避免被默认键样式覆盖）
        private void EndMoveStyle(Border key)
        {
            if (key == MousePad)
            {
                MousePad.Background = PadB();
                MousePad.BorderBrush = BorderB();
            }
            else
            {
                SetKey(key, false);
            }
        }

        // 读取按键当前 TranslateTransform 偏移（无则视为 0）；out 参数返回 XY
        private static void GetTransformXY(Border b, out double tx, out double ty)
        {
            var tt = b.RenderTransform as TranslateTransform;
            tx = tt != null ? tt.X : 0;
            ty = tt != null ? tt.Y : 0;
        }

        // 应用 TranslateTransform 偏移作为渲染变换（不影响布局流，用于移动位置表达）
        private static void SetTransformXY(Border b, double tx, double ty)
        {
            b.RenderTransform = new TranslateTransform { X = tx, Y = ty };
        }

        // 缩放落位归一：把 Margin.Left/Top（缩放 l/t 边补偿）并入 transform（tx/ty += margin），并把 Margin.Left/Top 归零。
        // 视觉位置不变（布局 margin + transform 等价归一到 transform），保证"位置"只有 transform 一个来源，避免与移动冲突。
        private static void NormalizeTransformMargin(Border b)
        {
            double tx, ty;
            GetTransformXY(b, out tx, out ty);
            double ml = b.Margin.Left;
            double mt = b.Margin.Top;
            if (ml == 0 && mt == 0) { if (tx == 0 && ty == 0) return; SetTransformXY(b, tx, ty); return; }
            tx += ml;
            ty += mt;
            b.Margin = new Thickness(0, 0, b.Margin.Right, b.Margin.Bottom);
            SetTransformXY(b, tx, ty);
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
                // 被删的内置鼠标键不在字典里，用 TryGetValue 容忍缺失（不抛 KeyNotFound）
                Border mL;
                if (_mouse.TryGetValue("L", out mL)) { if (mL != _moveKey) SetKey(mL, l); }
                if (_mouse.TryGetValue("MR", out mL)) { if (mL != _moveKey) SetKey(mL, r); }
                if (_mouse.TryGetValue("M", out mL)) { if (mL != _moveKey) SetKey(mL, m); }
                if (_mouse.TryGetValue("X1", out mL)) { if (mL != _moveKey) SetKey(mL, x1); }
                if (_mouse.TryGetValue("X2", out mL)) { if (mL != _moveKey) SetKey(mL, x2); }

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

        // ===================== 鼠标垫等比缩放（0.5.0）====================
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

        // 鼠标垫自定义持久化：写 PadPos_left/top（=transform tx/ty）、PadW/H（InvariantCulture）、PadCustom_=1，并置 _padCustomized=true
        private void SavePadCustom()
        {
            _padCustomized = true;
            double tx, ty;
            GetTransformXY(MousePad, out tx, out ty);
            var v = ApplicationData.Current.LocalSettings.Values;
            v["PadCustom_"] = 1;
            v["PadPos_left"] = tx.ToString(CultureInfo.InvariantCulture);
            v["PadPos_top"] = ty.ToString(CultureInfo.InvariantCulture);
            v["PadW"] = MousePad.Width.ToString(CultureInfo.InvariantCulture);
            v["PadH"] = MousePad.Height.ToString(CultureInfo.InvariantCulture);
            DiagLog("pad customized tx=" + (int)tx + " ty=" + (int)ty
                    + " w=" + (int)MousePad.Width + " h=" + (int)MousePad.Height);
        }

        // 启动恢复鼠标垫自定义：PadCustom_=1 时应用保存的 transform/Width/Height 并置 _padCustomized=true；
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
                double tx = ReadSettingDouble(v, "PadPos_left", 0.0);
                double ty = ReadSettingDouble(v, "PadPos_top", 0.0);
                double w = ReadSettingDouble(v, "PadW", MousePad.Width);
                double h = ReadSettingDouble(v, "PadH", MousePad.Height);
                if (w < MinPadW || h < MinPadH) { _padCustomized = false; return; }
                SetTransformXY(MousePad, tx, ty);
                MousePad.Margin = new Thickness(0, 0, 0, 0);
                MousePad.Width = w;
                MousePad.Height = h;
                _padW = w;   // 同步垫面尺寸变量，保证鼠标点映射基准与实际尺寸一致
                _padH = h;
                _padCustomized = true;
                DiagLog("pad restored tx=" + (int)tx + " ty=" + (int)ty + " w=" + (int)w + " h=" + (int)h);
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
            // 面板背景/边框
            SettingsMenu.Background = PanelB();
            SettingsMenu.BorderBrush = BorderB();
            LockMenu.Background = PanelB();
            LockMenu.BorderBrush = BorderB();
            AboutMenu.Background = PanelB();
            AboutMenu.BorderBrush = BorderB();
            DeleteConfirmBox.Background = PanelB();
            DeleteConfirmBox.BorderBrush = BorderB();

            // 标题/标签文字（默认文字，FollowKeyFg）
            SettingsTitle.Foreground = KeyFgB();
            SettingsThemeLabel.Foreground = KeyFgB();
            SettingsPadLabel.Foreground = KeyFgB();
            SettingsOpacityLabel.Foreground = KeyFgB();
            AboutTitle.Foreground = KeyFgB();
            AboutAuthor.Foreground = KeyFgB();
            SettingsInfoBtn.BorderBrush = BorderB();
            SettingsInfoText.Foreground = KeyFgB();
            LockMenuTitle.Foreground = KeyFgB();
            LockSwitchLabel.Foreground = KeyFgB();
            KeyPickerToggleText.Foreground = KeyFgB();
            KeyPickerToggleArrow.Foreground = KeyFgB();
            DeleteConfirmText.Foreground = KeyFgB();
            // GitHub/QQ 行文字用固定浅蓝 #4A9EFF 作为可点击提示，下划线已在 XAML 设置

            // 状态文本：主题切换按钮显示当前主题名（黑/灰/白/粉/蓝），点击切到下一个
            SettingsThemeText.Text = _theme == "dark" ? "黑" : _theme == "gray" ? "灰" : _theme == "light" ? "白" : _theme == "pink" ? "粉" : "蓝";
            SettingsPadText.Text = _padVisible ? "\u663e\u793a" : "\u9690\u85cf";   // 显示 / 隐藏
            LockSwitchText.Text = _layoutLocked ? "\u5f00" : "\u5173";   // 开 / 关（锁定菜单开关，与设置面板逻辑同步）

            // 反色按钮（主题切换 + 鼠标垫开关）：深色主题=亮胶囊，浅色主题=暗胶囊
            SettingsThemeBtn.Background = InvertKeyBgB();
            SettingsThemeBtn.BorderBrush = BorderB();
            SettingsThemeText.Foreground = InvertKeyFgB();
            SettingsPadBtn.Background = InvertKeyBgB();
            SettingsPadBtn.BorderBrush = BorderB();
            SettingsPadText.Foreground = InvertKeyFgB();
            // 透明度滑条：轨道/滑块用主题文字色与边框色
            OpacitySlider.Foreground = KeyFgB();
            OpacitySlider.Background = BorderB();

            // 主题颜色子菜单：「自定义」按钮（反色胶囊）、菜单配色 + 8 行目标输入框/颜色盘按钮
            SettingsCustomBtn.Background = InvertKeyBgB();
            SettingsCustomBtn.BorderBrush = BorderB();
            (SettingsCustomBtn.Child as TextBlock).Foreground = InvertKeyFgB();
            ThemeColorMenu.Background = PanelB();
            ThemeColorMenu.BorderBrush = BorderB();
            PickerMenu.Background = PanelB();
            PickerMenu.BorderBrush = BorderB();
            ThemeColorTitle.Foreground = KeyFgB();
            PickerTitle.Foreground = KeyFgB();
            foreach (var tb in new TextBlock[] { SlotLabel0, SlotLabel1, SlotLabel2, SlotLabel3, SlotLabel4, SlotLabel5, SlotLabel6, SlotLabel7 })
                tb.Foreground = KeyFgB();
            foreach (var b in new Border[] { SlotPick0, SlotPick1, SlotPick2, SlotPick3, SlotPick4, SlotPick5, SlotPick6, SlotPick7 })
            {
                b.Background = KeyBgB();
                b.BorderBrush = BorderB();
                (b.Child as TextBlock).Foreground = KeyFgB();
            }
            foreach (var tb in new TextBox[] { SlotInput0, SlotInput1, SlotInput2, SlotInput3, SlotInput4, SlotInput5, SlotInput6, SlotInput7 })
            {
                tb.Foreground = KeyFgB();
                tb.BorderBrush = BorderB();
            }
            SvMarker.Stroke = KeyFgB();
            AlphaMarker.Stroke = KeyFgB();
            foreach (var row in ColorGrid.Children)
                if (row is Grid rg)
                    foreach (var ch in rg.Children)
                        if (ch is Border sw) sw.BorderBrush = BorderB();

            // 顺色按钮（普通按钮随按键默认配色）
            LockKeyBtn.Background = KeyBgB();
            LockKeyBtn.BorderBrush = BorderB();
            LockKeyText.Foreground = KeyFgB();
            LockSwitchBtn.Background = KeyBgB();
            LockSwitchBtn.BorderBrush = BorderB();
            LockSwitchText.Foreground = KeyFgB();
            LockResetBtn.Background = KeyBgB();
            LockResetBtn.BorderBrush = BorderB();
            LockResetText.Foreground = KeyFgB();
            LockCloseBtn.Background = KeyBgB();
            LockCloseBtn.BorderBrush = BorderB();
            LockCloseText.Foreground = KeyFgB();
            DeleteConfirmYes.Background = KeyBgB();
            DeleteConfirmYes.BorderBrush = BorderB();
            DeleteConfirmYesText.Foreground = KeyFgB();
            DeleteConfirmNo.Background = KeyBgB();
            DeleteConfirmNo.BorderBrush = BorderB();
            DeleteConfirmNoText.Foreground = KeyFgB();
            SettingsBtn.Background = KeyBgB();
            SettingsBtn.BorderBrush = BorderB();
            SettingsBtnText.Foreground = KeyFgB();

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
                    b.Background = KeyBgB();
                    b.BorderBrush = BorderB();
                    var tb = b.Child as TextBlock;
                    if (tb != null) tb.Foreground = KeyFgB();
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
            DiagLog("settings opened theme=" + _theme);
        }

        // 点击菜单框内部：标记已处理，避免冒泡到遮罩触发关闭
        private void SettingsMenu_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        // 点击遮罩（菜单框外）：收起设置子菜单，并一并收起关于面板与主题颜色面板
        private void SettingsPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            AboutPanel.Visibility = Visibility.Collapsed;
            ThemeColorPanel.Visibility = Visibility.Collapsed;
            DiagLog("settings closed by mask");
        }

        // 设置菜单 信息按钮：弹出「关于」面板（覆盖在设置菜单之上）
        private void SettingsInfo_Click(object sender, TappedRoutedEventArgs e)
        {
            ApplySettingsColors();
            AboutPanel.Visibility = Visibility.Visible;
            DiagLog("about opened");
        }

        // 点击关于面板框内部：标记已处理，避免冒泡到遮罩触发关闭
        private void AboutMenu_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        // 点击关于面板遮罩（面板框外）：收起关于面板
        private void AboutPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (e.OriginalSource == AboutPanel)
            {
                AboutPanel.Visibility = Visibility.Collapsed;
                DiagLog("about closed by mask");
            }
        }

        // GitHub 行点击：调用系统浏览器打开仓库页；Game Bar 沙箱可能拦截，失败静默降级为仅显示
        private async void GitHubRow_Tapped(object sender, TappedRoutedEventArgs e)
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/0810milk/Gamebar-Keycast"));
            }
            catch
            {
                // 静默降级：仅显示地址文本，不崩溃
            }
        }

        // QQ 群行点击：调用系统打开 QQ 群快捷链接（链接不显示在界面，仅群号文本），失败静默
        private async void QqRow_Tapped(object sender, TappedRoutedEventArgs e)
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri("https://qun.qq.com/universal-share/share?ac=1&authKey=O"));
            }
            catch
            {
                // 静默降级：仅显示群号文本，不崩溃
            }
        }

        // 设置面板里的主题切换：三色轮换 dark→light→pink→dark（与底部胶囊按钮同逻辑）
        private void SettingsTheme_Click(object sender, TappedRoutedEventArgs e)
        {
            // 五态循环：黑 → 灰 → 白 → 粉 → 蓝 → 黑
            if (_theme == "dark") _theme = "gray";
            else if (_theme == "gray") _theme = "light";
            else if (_theme == "light") _theme = "pink";
            else if (_theme == "pink") _theme = "blue";
            else _theme = "dark";
            // 切主题时收起调色盘/主题颜色菜单，避免残留覆盖层
            PickerMenu.Visibility = Visibility.Collapsed;
            ThemeColorPanel.Visibility = Visibility.Collapsed;
            _activeSlot = -1;
            ApplyTheme();
        }

        // 鼠标垫显示/隐藏开关：仅切 Visibility（不是删除，位置/尺寸/transform 全部保留），写 PadVisible_ 持久化并刷新配色
        private void PadToggle_Click(object sender, TappedRoutedEventArgs e)
        {
            _padVisible = !_padVisible;
            MousePad.Visibility = _padVisible ? Visibility.Visible : Visibility.Collapsed;
            ApplicationData.Current.LocalSettings.Values["PadVisible_"] = _padVisible ? 1 : 0;
            ApplySettingsColors();
            DiagLog("pad visible=" + (_padVisible ? "on" : "off"));
        }

        // 重置按键布局共用逻辑（设置面板与二级锁定菜单的重置按钮都走这里）
        private void PerformLayoutReset()
        {
            // 重新登记全部默认键（含被删的），恢复其可见性由下方 ResetKeyLayout 统一处理
            RegisterDefaultKeys();
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
            // 绝不触碰主题（_theme/_light 及任何配色）——重置只处理按键布局与自定义键。
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
            MousePad.RenderTransform = null;   // 清 transform（恢复默认位置）
            RefreshPadAutoSize();
            // 清除"已删默认键"标记（重置后全部默认键恢复到刚安装状态）
            var delNames = new List<string>();
            foreach (var kv in ApplicationData.Current.LocalSettings.Values)
            {
                if (kv.Key.StartsWith("Deleted_", StringComparison.Ordinal)) delNames.Add(kv.Key);
            }
            foreach (var k in delNames) ApplicationData.Current.LocalSettings.Values.Remove(k);
            ClearHover();
            DiagLog("layout reset (custom keys cleared: " + deadNames.Count + ")");
        }

        // 二级控件菜单的"自定义控件"按键点击，展开控件菜单（覆盖层在设置面板之上，外观一致）
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
            // 移动位置持久化：若已存 CustomPos_<名>（tx;ty）则应用 transform，否则写默认 (0,0)
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
                        SetTransformXY(border, pl, pt);   // 位置=transform，Margin 保持 (0,0,6,0) 布局间距
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
            if (_deleteConfirmKey != null) ConfirmDeleteKey(_deleteConfirmKey);
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
            b.RenderTransform = null;   // 清 transform（恢复默认位置）
            b.Visibility = Visibility.Visible;   // 恢复可见（重置 = 恢复被删内置键）
            ApplicationData.Current.LocalSettings.Values[LayoutPrefix + name] =
                ((int)w) + ";" + ((int)h) + ";0;0";   // 位置=transform，归零
        }

        // 锁定布局开关共用逻辑（设置面板与二级锁定菜单的开关都走这里）：
        // 翻转 _layoutLocked、写回 LocalSettings、重置高亮/光标、刷新配色（锁定菜单"开/关"文本由 ApplySettingsColors 统一刷新）
        private void ToggleLayoutLock()
        {
            _layoutLocked = !_layoutLocked;
            ApplicationData.Current.LocalSettings.Values["LayoutLocked"] = _layoutLocked;
            ClearHover();   // 锁定/解锁都重置高亮与光标，避免残留 Size 光标
            ApplyKeyOpacity();   // 锁定切换：解锁→按键临时 100%；重新锁定→恢复滑条设定值（不残留 100%）
            ApplySettingsColors();
            DiagLog("layout lock=" + (_layoutLocked ? "on" : "off"));
        }

        // 应用按键透明度：锁定开启（游玩中）= 滑条设定值；锁定关闭（编辑布局）= 临时强制 100%
        // 关键：始终从 _keyOpacity 设定值计算，绝不从当前 Opacity 推导——退出编辑（重新开启锁定）自动回到设定值，
        // 不会残留编辑期的 100%。被删内置键不在字典，foreach 天然跳过；菜单/关于面板/参考线不设 Opacity
        private void ApplyKeyOpacity()
        {
            double target = _layoutLocked ? _keyOpacity / 100.0 : 1.0;
            foreach (var kv in _keys) kv.Value.Opacity = target;
            foreach (var kv in _mouse) kv.Value.Opacity = target;
            foreach (var kv in _customKeys) kv.Value.Opacity = target;
            if (MousePad != null) MousePad.Opacity = target;
        }

        // 透明度滑条：更新设定值、写持久化（KeyOpacity_ 存 0~100），立即按当前锁定状态应用
        private void OpacitySlider_Changed(object sender, RangeBaseValueChangedEventArgs e)
        {
            _keyOpacity = e.NewValue;
            ApplicationData.Current.LocalSettings.Values["KeyOpacity_"] = (int)e.NewValue;
            ApplyKeyOpacity();
        }

        // ===================== 自定义主题色：工具与核心逻辑 =====================

        // #RRGGBB 或 #AARRGGBB 转 Color（大小写均可，alpha 在前，8 位默认 alpha=FF）；非法返回 null
        private static Color? ParseHex(string s)
        {
            if (s == null) return null;
            s = s.Trim();   // 容忍粘贴带空格
            if (s.Length < 7 || s.Length > 9 || s[0] != '#') return null;
            byte A = 0xFF, R, G, B;
            int off = 0;
            if (s.Length == 9)
            {
                if (!byte.TryParse(s.Substring(1, 2), System.Globalization.NumberStyles.HexNumber, null, out A)) return null;
                off = 2;
            }
            if (!byte.TryParse(s.Substring(1 + off, 2), System.Globalization.NumberStyles.HexNumber, null, out R)) return null;
            if (!byte.TryParse(s.Substring(3 + off, 2), System.Globalization.NumberStyles.HexNumber, null, out G)) return null;
            if (!byte.TryParse(s.Substring(5 + off, 2), System.Globalization.NumberStyles.HexNumber, null, out B)) return null;
            return Color.FromArgb(A, R, G, B);
        }

        // Color 转 #RRGGBB（alpha=FF 时）或 #AARRGGBB（带透明度时）
        private static string ToHex(Color c)
        {
            if (c.A == 0xFF)
                return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
            return "#" + c.A.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        // HSV 转 RGB（h 0~360，s/v 0~1），标准算法
        private static Color HsvToRgb(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromArgb(0xFF, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        // 当前主题预设的第 k 槽颜色（dark/gray/light/pink/blue 从现有画刷字段取，与五态主题一致）
        private Color PresetColor(int k)
        {
            Brush[] d = { _darkPanel, _darkBorder, _darkDefaultBg, _darkDefaultFg, _darkPressedBg, _darkPressedFg, _darkPad, _darkDefaultFg };
            Brush[] g = { _grayPanel, _grayBorder, _grayKeyBg, _grayKeyFg, _grayPressedBg, _grayPressedFg, _grayPad, _grayDot };
            Brush[] l = { _lightPanel, _lightBorder, _lightDefaultBg, _lightDefaultFg, _lightPressedBg, _lightPressedFg, _lightPad, _darkDefaultBg };
            Brush[] p = { _pinkPanel, _pinkBorder, _pinkKeyBg, _pinkKeyFg, _pinkPressedBg, _pinkPressedFg, _pinkPad, _pinkDot };
            Brush[] b = { _bluePanel, _blueBorder, _blueKeyBg, _blueKeyFg, _bluePressedBg, _bluePressedFg, _bluePad, _blueDot };
            var pick = _theme == "dark" ? d : _theme == "gray" ? g : _theme == "light" ? l : _theme == "pink" ? p : b;
            return ((SolidColorBrush)pick[k]).Color;
        }

        private Color? GetCustomKey(int k)
        {
            var v = ApplicationData.Current.LocalSettings.Values[CustomKeys[k]] as string;
            return v != null ? ParseHex(v) : null;
        }

        private void SetCustomKey(int k, Color c)
        {
            ApplicationData.Current.LocalSettings.Values[CustomKeys[k]] = ToHex(c);
        }

        // 槽位显示色：custom 态读自定义键（缺省回落 dark 预设）；否则当前预设色
        private Color GetSlotDisplayColor(int k)
        {
            if (_theme == "custom")
            {
                var c = GetCustomKey(k);
                if (c.HasValue) return c.Value;
                // 回落：dark 预设
                var save = _theme; _theme = "dark";
                var col = PresetColor(k);
                _theme = save;
                return col;
            }
            return PresetColor(k);
        }

        // 固化保存（用户拍板的预设联动）：先把当前显示中的 8 个值全部写入，再写被改槽位新值，切主题为 custom 并刷新应用
        private void CommitSlotColor(int i, Color c)
        {
            for (int k = 0; k < 8; k++) SetCustomKey(k, GetSlotDisplayColor(k));
            SetCustomKey(i, c);
            _theme = "custom";
            RefreshCustomBrushes();
            ApplyTheme();
        }

        // 用 8 个 Custom_ 键刷新动态画刷（缺省回落 dark 预设，GetSlotDisplayColor 已处理回落逻辑）
        private void RefreshCustomBrushes()
        {
            for (int k = 0; k < 8; k++)
            {
                _customBrushes[k].Color = GetSlotDisplayColor(k);
            }
        }

        // 取色统一入口（低频：色块点击/hex 输入后同步）：回填 hex 框（防递归）→ 全量固化 → 盘同步。
        // 拖动中（高频）不走这里，由三个 Update*Pick 直连轻量路径（只改画刷色 + marker，不落盘不刷文本）
        private void ApplyPickedColor(Color c)
        {
            if (_activeSlot < 0 || PickerMenu.Visibility != Visibility.Visible) return;
            _lastPickColor = c;
            var tb = SlotInput(_activeSlot);
            _syncing = true;
            tb.Text = ToHex(c);
            _syncing = false;
            tb.BorderBrush = BorderB();   // 恢复可能存在的红框
            CommitSlotColor(_activeSlot, c);
            SyncPickerToColor(c);
        }

        // 拖动节流：两次取色间隔 <10ms 直接跳过（高刷屏 120Hz+ 事件合并，避免每帧全量工作）
        private bool ThrottlePick()
        {
            int now = Environment.TickCount;
            if (now - _lastPickMs < 10) return false;
            _lastPickMs = now;
            return true;
        }

        // 拖动结束：用拖动中保存的最后颜色全量固化（8 键落盘 + 切 custom + ApplyTheme），并回填 hex 框
        private void FinalizePick()
        {
            if (_activeSlot < 0 || PickerMenu.Visibility != Visibility.Visible) return;
            Color? c = _lastPickColor;
            if (!c.HasValue) c = ParseHex(SlotInput(_activeSlot).Text);   // 兜底：未拖动直接点开再收起
            if (c.HasValue)
            {
                CommitSlotColor(_activeSlot, c.Value);
                var tb = SlotInput(_activeSlot);
                _syncing = true;
                tb.Text = ToHex(c.Value);   // 拖动中不刷文本，释放时一次回填
                _syncing = false;
            }
        }

        // 拖动开始：确保处于 custom 主题（否则轻量路径改的 brush 不被组件引用）
        private void EnsureCustomTheme()
        {
            if (_theme != "custom")
            {
                _theme = "custom";
                RefreshCustomBrushes();
                ApplyTheme();
            }
        }

        private TextBox SlotInput(int i)
        {
            switch (i)
            {
                case 0: return SlotInput0;
                case 1: return SlotInput1;
                case 2: return SlotInput2;
                case 3: return SlotInput3;
                case 4: return SlotInput4;
                case 5: return SlotInput5;
                case 6: return SlotInput6;
                default: return SlotInput7;
            }
        }

        // 让调色盘显示某颜色：更新色相、方块渐变、marker 位置、透明度条
        private void SyncPickerToColor(Color c)
        {
            _alpha = c.A;
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;
            double h = 0, s = 0, v = max;
            if (delta > 0.0001)
            {
                s = delta / max;
                if (max == r) h = 60 * (((g - b) / delta) % 6);
                else if (max == g) h = 60 * ((b - r) / delta + 2);
                else h = 60 * ((r - g) / delta + 4);
                if (h < 0) h += 360;
            }
            _hue = h;
            UpdateSvBase();
            double sx = s, sy = 1 - v;
            if (SvBox.ActualWidth > 0)
            {
                // 父级是 Grid，Canvas.SetLeft/Top 无效，必须用 Margin 定位
                SvMarker.Margin = new Thickness(sx * SvBox.ActualWidth - SvMarker.Width / 2,
                                                sy * SvBox.ActualHeight - SvMarker.Height / 2, 0, 0);
            }
            UpdateAlphaBar();
            UpdateHueMarker();
        }

        // 更新方块底层渐变：白 → 当前色相纯色
        private void UpdateSvBase()
        {
            var grad = SvBase.Fill as LinearGradientBrush;
            if (grad == null) return;
            grad.GradientStops[1].Color = HsvToRgb(_hue, 1.0, 1.0);
        }

        // ===================== 自定义主题色：事件 =====================

        // 设置菜单"自定义"按钮：切到自定义主题并打开「主题颜色」子菜单
        private void CustomTheme_Click(object sender, TappedRoutedEventArgs e)
        {
            _theme = "custom";
            RefreshCustomBrushes();
            ApplyTheme();
            ApplySettingsColors();
            // 同步 8 个 hex 输入框为当前显示值（重启后 custom 值也能正确显示，而非初始 #FF000000）
            for (int k = 0; k < 8; k++)
            {
                _syncing = true;
                SlotInput(k).Text = ToHex(GetSlotDisplayColor(k));
                _syncing = false;
            }
            // 重置调色区，避免上次残留的展开状态
            _activeSlot = -1;
            PickerMenu.Visibility = Visibility.Collapsed;
            ThemeColorPanel.Visibility = Visibility.Visible;
            DiagLog("theme color menu opened");
        }

        // 点遮罩收起；点面板内不冒泡
        private void ThemeColorPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (e.OriginalSource == ThemeColorPanel)
            {
                ThemeColorPanel.Visibility = Visibility.Collapsed;
                DiagLog("theme color closed by mask");
            }
        }

        private void ThemeColorMenu_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        // 点某行"颜色盘"：展开调色区并定位到该行，盘显示该行当前色；再点同一行则收起
        private void SlotPick_Tapped(object sender, TappedRoutedEventArgs e)
        {
            int i = Convert.ToInt32(((Border)sender).Tag);   // XAML Tag 是字符串，必须 Convert 不能强转
            if (_activeSlot == i && PickerMenu.Visibility == Visibility.Visible)
            {
                PickerMenu.Visibility = Visibility.Collapsed;
                _activeSlot = -1;
                return;
            }
            _activeSlot = i;
            PickerTitle.Text = SlotNames[_activeSlot];
            PickerMenu.Visibility = Visibility.Visible;
            SyncPickerToColor(GetSlotDisplayColor(_activeSlot));
        }

        // hex 输入校验：合法则固化应用 + 盘同步；非法（非空）标红框不应用
        private void SlotInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncing) return;
            var tb = (TextBox)sender;
            int i = Convert.ToInt32(tb.Tag);   // XAML Tag 是字符串，必须 Convert 不能强转
            var c = ParseHex(tb.Text);
            if (c.HasValue)
            {
                tb.BorderBrush = BorderB();
                _activeSlot = i;
                CommitSlotColor(i, c.Value);
                SyncPickerToColor(c.Value);
            }
            else if (!string.IsNullOrEmpty(tb.Text))
            {
                tb.BorderBrush = new SolidColorBrush(Colors.Red);
            }
        }

        // 方形盘：按下捕获 → 移动取色 → 释放（拖动中轻量更新，释放时固化）
        private void SvBox_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            EnsureCustomTheme();
            if (SvBox.CapturePointer(e.Pointer)) UpdateSvPick(e);
        }

        private void SvBox_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (SvBox.PointerCaptures != null && SvBox.PointerCaptures.Count > 0) UpdateSvPick(e);
        }

        private void SvBox_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (SvBox.PointerCaptures != null && SvBox.PointerCaptures.Count > 0)
            {
                SvBox.ReleasePointerCapture(e.Pointer);
                FinalizePick();
            }
        }

        private void UpdateSvPick(PointerRoutedEventArgs e)
        {
            if (SvBox.ActualWidth <= 0 || SvBox.ActualHeight <= 0) return;
            if (!ThrottlePick()) return;
            var pos = e.GetCurrentPoint(SvBox).Position;
            double s = Math.Max(0.0, Math.Min(1.0, pos.X / SvBox.ActualWidth));
            double v = Math.Max(0.0, Math.Min(1.0, 1.0 - pos.Y / SvBox.ActualHeight));
            var c = HsvToRgb(_hue, s, v);
            c.A = (byte)Math.Round(_alpha);
            _lastPickColor = c;
            _customBrushes[_activeSlot].Color = c;   // 引用即改，组件实时变色
            SvMarker.Margin = new Thickness(s * SvBox.ActualWidth - SvMarker.Width / 2,
                                            (1 - v) * SvBox.ActualHeight - SvMarker.Height / 2, 0, 0);
            UpdateAlphaBar();   // RGB 变了，透明度条渐变同步
        }

        // 色相条：按下捕获 → 取色 → 释放（拖动中轻量更新，释放时固化）
        private void HueBar_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            EnsureCustomTheme();
            if (HueBar.CapturePointer(e.Pointer)) UpdateHuePick(e);
        }

        private void HueBar_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (HueBar.PointerCaptures != null && HueBar.PointerCaptures.Count > 0) UpdateHuePick(e);
        }

        private void HueBar_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (HueBar.PointerCaptures != null && HueBar.PointerCaptures.Count > 0)
            {
                HueBar.ReleasePointerCapture(e.Pointer);
                FinalizePick();
            }
        }

        // 透明度条：取 X → alpha（0~255）→ 用当前 S/V/H 重新取色
        private void AlphaBar_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            EnsureCustomTheme();
            if (AlphaBar.CapturePointer(e.Pointer)) UpdateAlphaPick(e);
        }

        private void AlphaBar_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (AlphaBar.PointerCaptures != null && AlphaBar.PointerCaptures.Count > 0) UpdateAlphaPick(e);
        }

        private void AlphaBar_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (AlphaBar.PointerCaptures != null && AlphaBar.PointerCaptures.Count > 0)
            {
                AlphaBar.ReleasePointerCapture(e.Pointer);
                FinalizePick();
            }
        }

        private void UpdateAlphaPick(PointerRoutedEventArgs e)
        {
            if (AlphaBar.ActualWidth <= 0) return;
            if (!ThrottlePick()) return;
            var pos = e.GetCurrentPoint(AlphaBar).Position;
            _alpha = Math.Max(0.0, Math.Min(255.0, pos.X / AlphaBar.ActualWidth * 255.0));
            // 用当前行的 S/V/H 与新区块重新取色
            if (_activeSlot >= 0 && PickerMenu.Visibility == Visibility.Visible)
            {
                var cur = ParseHex(SlotInput(_activeSlot).Text);
                if (cur.HasValue)
                {
                    var c = cur.Value;
                    c.A = (byte)Math.Round(_alpha);
                    _lastPickColor = c;
                    _customBrushes[_activeSlot].Color = c;   // 引用即改，组件实时变色
                }
            }
            UpdateAlphaBar();   // marker 随 alpha 移动（渐变 RGB 不变，无需重建）
        }

        // 更新透明度条：Fill = 透明 → 当前 RGB 色（垫灰底显示透明效果），marker 随 alpha 移动
        private void UpdateAlphaBar()
        {
            var grad = AlphaFill.Fill as LinearGradientBrush;
            if (grad == null)
            {
                grad = new LinearGradientBrush();
                grad.StartPoint = new Point(0, 0); grad.EndPoint = new Point(1, 0);
                grad.GradientStops.Add(new GradientStop { Color = Colors.Transparent, Offset = 0 });
                grad.GradientStops.Add(new GradientStop { Color = Colors.Transparent, Offset = 1 });
                AlphaFill.Fill = grad;
            }
            Color rgb = Colors.Black;
            if (_activeSlot >= 0 && PickerMenu.Visibility == Visibility.Visible)
            {
                var cur = ParseHex(SlotInput(_activeSlot).Text);
                if (cur.HasValue) { rgb = cur.Value; rgb.A = 0xFF; }
            }
            grad.GradientStops[0].Color = Colors.Transparent;
            grad.GradientStops[1].Color = rgb;
            if (AlphaBar.ActualWidth > 0)
            {
                // 父级是 Grid，Canvas.SetLeft/Top 无效，必须用 Margin 定位
                AlphaMarker.Margin = new Thickness(_alpha / 255.0 * AlphaBar.ActualWidth - AlphaMarker.Width / 2,
                                                   (AlphaBar.ActualHeight - AlphaMarker.Height) / 2, 0, 0);
            }
        }

        // 色相条指示器：随 _hue 移动
        private void UpdateHueMarker()
        {
            if (HueMarker == null || HueBar.ActualWidth <= 0) return;
            HueMarker.Margin = new Thickness(_hue / 360.0 * HueBar.ActualWidth - HueMarker.Width / 2, 0, 0, 0);
        }

        private void UpdateHuePick(PointerRoutedEventArgs e)
        {
            if (HueBar.ActualWidth <= 0) return;
            if (!ThrottlePick()) return;
            var pos = e.GetCurrentPoint(HueBar).Position;
            _hue = Math.Max(0.0, Math.Min(360.0, pos.X / HueBar.ActualWidth * 360.0));
            UpdateSvBase();
            UpdateHueMarker();
            // 用新色相 + 当前 S/V（方块 marker 位置反推）实时取色——旧实现反推旧色导致拖色相条无效）
            if (_activeSlot >= 0 && PickerMenu.Visibility == Visibility.Visible)
            {
                double sx = 0.5, sy = 0.5;
                if (SvBox.ActualWidth > 0)
                {
                    sx = Math.Max(0.0, Math.Min(1.0, (SvMarker.Margin.Left + SvMarker.Width / 2) / SvBox.ActualWidth));
                    sy = Math.Max(0.0, Math.Min(1.0, (SvMarker.Margin.Top + SvMarker.Height / 2) / SvBox.ActualHeight));
                }
                var c = HsvToRgb(_hue, sx, sy);
                c.A = (byte)Math.Round(_alpha);
                _lastPickColor = c;
                _customBrushes[_activeSlot].Color = c;   // 引用即改，组件实时变色
                UpdateAlphaBar();   // RGB 变了，透明度条渐变同步
            }
        }

        // 16 常用色块：点击设为当前行颜色（低频操作，直接全量固化）
        private void Swatch_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var c = ParseHex((string)((Border)sender).Tag);
            if (c.HasValue) ApplyPickedColor(c.Value);
        }

        // 构建 16 常用色块（4 × 4 列）与调色盘渐变（初始化时调用一次）
        private void InitPickerControls()
        {
            string[] hexes = { "#000000", "#FFFFFF", "#808080", "#C0C0C0",
                               "#FF0000", "#FF8000", "#FFFF00", "#80FF00",
                               "#00FF00", "#00FF80", "#00FFFF", "#0080FF",
                               "#0000FF", "#8000FF", "#FF00FF", "#FF0080" };
            for (int r = 0; r < 4; r++)
            {
                ColorGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
                var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                for (int c = 0; c < 4; c++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int c = 0; c < 4; c++)
                {
                    string hex = hexes[r * 4 + c];
                    var col = ParseHex(hex);
                    var sw = new Border
                    {
                        Background = col.HasValue ? new SolidColorBrush(col.Value) : _darkDefaultBg,
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Margin = new Thickness(2),
                        Tag = hex
                    };
                    sw.Tapped += Swatch_Tapped;
                    Grid.SetColumn(sw, c);
                    row.Children.Add(sw);
                }
                Grid.SetRow(row, r);
                ColorGrid.Children.Add(row);
            }

            // 方块底层渐变：白 → 当前色相纯色（初始红色相）
            var baseGrad = new LinearGradientBrush();
            baseGrad.StartPoint = new Point(0, 0); baseGrad.EndPoint = new Point(1, 0);
            baseGrad.GradientStops.Add(new GradientStop { Color = Colors.White, Offset = 0 });
            baseGrad.GradientStops.Add(new GradientStop { Color = HsvToRgb(0, 1, 1), Offset = 1 });
            SvBase.Fill = baseGrad;
            // 方块上层渐变：透明 → 黑（垂直，明度）
            var overGrad = new LinearGradientBrush();
            overGrad.StartPoint = new Point(0, 0); overGrad.EndPoint = new Point(0, 1);
            overGrad.GradientStops.Add(new GradientStop { Color = Colors.Transparent, Offset = 0 });
            overGrad.GradientStops.Add(new GradientStop { Color = Colors.Black, Offset = 1 });
            SvOver.Fill = overGrad;
            // 色相条渐变：红→黄→绿→青→蓝→品红→红
            var hueGrad = new LinearGradientBrush();
            hueGrad.StartPoint = new Point(0, 0); hueGrad.EndPoint = new Point(1, 0);
            double[] stops = { 0, 60, 120, 180, 240, 300, 360 };
            foreach (var deg in stops)
            {
                hueGrad.GradientStops.Add(new GradientStop { Color = HsvToRgb(deg, 1, 1), Offset = deg / 360.0 });
            }
            (HueBar.Children[0] as Rectangle).Fill = hueGrad;

            // 调色区刚展开时 SvBox 可能尚未布局（ActualWidth=0），marker 定位会被跳过——布局完成后补一次
            SvBox.SizeChanged += (s2, e2) =>
            {
                if (PickerMenu.Visibility == Visibility.Visible && _activeSlot >= 0)
                {
                    var cur = ParseHex(SlotInput(_activeSlot).Text);
                    if (cur.HasValue) SyncPickerToColor(cur.Value);
                }
            };
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
            // 进入移动模式：记录起点（按下时的指针/transform 偏移），高亮提示（琥珀色边框）
            _moveKey = b;
            if (b == MousePad) _padCustomized = true;   // 一旦开始移动鼠标垫即视为自定义，避免操作期间被自动跟随覆盖
            _moveStartX = _pressPointerRoot.X;
            _moveStartY = _pressPointerRoot.Y;
            var tt0 = b.RenderTransform as TranslateTransform;
            _moveStartTX = tt0 != null ? tt0.X : 0;
            _moveStartTY = tt0 != null ? tt0.Y : 0;
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
        // 确认删除键：自定义键（移除字典/面板/持久化）或内置键（字典移除/Collapsed/清 Layout_ 并记录 Deleted_），并清理状态
        private void ConfirmDeleteKey(Border b)
        {
            string name = NameOf(b);
            if (string.IsNullOrEmpty(name) || name == "?" || name == "Pad") return;
            if (_customKeys.ContainsKey(name))
            {
                // 自定义键：移除面板 + 清 Custom_/CustomPos_ 持久化
                _customKeys.Remove(name);
                CustomKeysPanel.Children.Remove(b);
                ApplicationData.Current.LocalSettings.Values.Remove("Custom_" + name);
                ApplicationData.Current.LocalSettings.Values.Remove("CustomPos_" + name);
                if (_customKeys.Count == 0) CustomKeysPanel.Visibility = Visibility.Collapsed;
                DiagLog("custom key deleted: " + name);
            }
            else if (_keys.ContainsKey(name) || _mouse.ContainsKey(name))
            {
                // 内置键：字典移除 + 清 Layout_ 持久化 + 记录 Deleted_ + Collapsed（不销毁，便于重置恢复）
                DeleteDefaultKey(name, b);
                DiagLog("default key deleted: " + name);
            }
            else
            {
                return;
            }
            _deleteConfirmKey = null;
            DeleteConfirmPanel.Visibility = Visibility.Collapsed;
            CancelLongPress();
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
            b.BorderBrush = _theme == "dark" ? _darkDefaultFg : _darkDefaultBg;   // 深色主题白高亮，浅色主题黑高亮
            ApplyCursor(mode);
        }

        private void ClearHover()
        {
            if (_hoverKey == null) return;
            _hoverKey.BorderBrush = BorderB();
            ApplyCursor(null);
            _hoverKey = null;
            _hoverMode = null;
        }

        // 按下：右键→删除确认（仅自定义键）；非右键→启动长按计时 + 边缘缩放判定
        private void Key_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var b = sender as Border;
            if (b == null) return;
            // 右键：自定义键与内置键（_keys/_mouse）均可删除确认；鼠标垫（Pad）不可删
            if (e.GetCurrentPoint(b).Properties.IsRightButtonPressed)
            {
                CancelLongPress();
                if (_dragKey != null || _moveKey != null)
                {
                    e.Handled = true;
                    return;
                }
                string nm = NameOf(b);
                if (nm != "?" && nm != "Pad")
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
                // 位置用 TranslateTransform 渲染变换表达（不写 Margin，避免 StackPanel 流式布局挤压兄弟元素）
                double tx = _moveStartTX + dx;
                double ty = _moveStartTY + dy;
                double w = (key.ActualWidth > 0 ? key.ActualWidth : key.Width);
                double h = (key.ActualHeight > 0 ? key.ActualHeight : key.Height);
                // 被拖按键当前四边（SnapCanvas 坐标）：起点视觉基准 + transform 位移增量（免布局刷新）
                double[] ea = new double[4];
                ea[0] = _moveBaseLeft + (tx - _moveStartTX);
                ea[1] = ea[0] + w;
                ea[2] = _moveBaseTop + (ty - _moveStartTY);
                ea[3] = ea[2] + h;
                var rects = CollectOtherRects(key);
                var hitH = ComputeAxisSnap(true, ea, rects);
                var hitV = ComputeAxisSnap(false, ea, rects);
                // 滞回：未吸附 ≤8 触发、已吸附 ≤10 保持、>10 脱离；两轴独立，只修正吸附到的轴
                bool snapH = hitH.Active && ShouldSnap(hitH.Delta, ref _snapActiveH);
                bool snapV = hitV.Active && ShouldSnap(hitV.Delta, ref _snapActiveV);
                if (snapH) tx += hitH.Delta;
                if (snapV) ty += hitV.Delta;
                key.RenderTransform = new TranslateTransform { X = tx, Y = ty };
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
                        var tt = key.RenderTransform as TranslateTransform;
                        double tx = tt != null ? tt.X : 0;
                        double ty = tt != null ? tt.Y : 0;
                        ApplicationData.Current.LocalSettings.Values["CustomPos_" + nm] =
                            tx.ToString(CultureInfo.InvariantCulture) + ";" + ty.ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        SaveKeyLayout(nm, key);   // 默认键走现有 Layout_ 持久化（位置=transform）
                    }
                    var tt2 = key.RenderTransform as TranslateTransform;
                    DiagLog("key moved " + nm + " tx=" + (int)(tt2 != null ? tt2.X : 0) + " ty=" + (int)(tt2 != null ? tt2.Y : 0));
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
                // 鼠标垫缩放落位：归一 margin→transform 后持久化，并同步垫面尺寸变量（保证点映射基准 = 实际尺寸）
                NormalizeTransformMargin(MousePad);
                _padW = MousePad.Width;
                _padH = MousePad.Height;
                SavePadCustom();
            }
            else
            {
                // 普通键缩放落位：归一 margin→transform（位置唯一来源 = transform），再持久化
                NormalizeTransformMargin(dragKey);
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

        // ===================== 吸附对齐（0.5.0）====================
        // 统一坐标系：SnapCanvas（最外层覆盖层），与被拖按键所在容器无关，跨 StackPanel 也能正确比较视觉边。
        // 关键：拖动中只用"起点视觉坐标 + 位移增量"反推被拖按键的边，避免改 Margin 后布局未刷新导致 TransformToVisual 读到旧值。

        // 吸附判定结果：单轴的贴边修正量 + 参考线位置
        private struct SnapHit
        {
            public bool Active;          // 本轴是否吸附
            public double Delta;         // 修正量（加到被拖按键的 transform.tx/ty 或 Margin.Left/Top）
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

        // 登记全部默认键到字典（键盘 12 键 + 鼠标 5 键；覆盖式，重置/恢复被删键时复用）
        private void RegisterDefaultKeys()
        {
            _keys["Q"] = KeyQ; _keys["W"] = KeyW; _keys["E"] = KeyE; _keys["R"] = KeyR;
            _keys["A"] = KeyA; _keys["S"] = KeyS; _keys["D"] = KeyD; _keys["F"] = KeyF;
            _keys["Shift"] = KeyShift; _keys["Ctrl"] = KeyCtrl; _keys["Alt"] = KeyAlt; _keys["Space"] = KeySpace;
            _mouse["L"] = MouseL; _mouse["M"] = MouseM; _mouse["MR"] = MouseR;   // MR：避免与键盘 R 的 Layout_R 冲突
            _mouse["X1"] = MouseX1; _mouse["X2"] = MouseX2;
        }

        // 删除一个默认键（内置 _keys/_mouse）：字典移除 + 清 Layout_ 持久化 + 记录 Deleted_ + Collapsed（不销毁，便于重置恢复）
        private void DeleteDefaultKey(string name, Border b)
        {
            if (_keys.ContainsKey(name)) _keys.Remove(name);
            else if (_mouse.ContainsKey(name)) _mouse.Remove(name);
            ApplicationData.Current.LocalSettings.Values.Remove("Layout_" + name);
            ApplicationData.Current.LocalSettings.Values["Deleted_" + name] = 1;
            b.Visibility = Visibility.Collapsed;
        }

        // 启动/布局加载后应用"已删默认键"状态：遍历 Deleted_ 前缀，把对应键 Collapsed + 移除字典
        private void RestoreDeletions()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                var deadNames = new List<string>();
                foreach (var kv in values)
                {
                    if (kv.Key.StartsWith("Deleted_", StringComparison.Ordinal))
                    {
                        deadNames.Add(kv.Key.Substring("Deleted_".Length));
                    }
                }
                foreach (var nm in deadNames)
                {
                    Border b;
                    if (_keys.TryGetValue(nm, out b))
                    {
                        _keys.Remove(nm);
                        b.Visibility = Visibility.Collapsed;
                    }
                    else if (_mouse.TryGetValue(nm, out b))
                    {
                        _mouse.Remove(nm);
                        b.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch
            {
            }
        }

        // 布局持久化：每个按键存 "宽;高;tx;ty"（位置=TranslateTransform 偏移，Margin 不再存位置）。
        // 老数据 4 段格式 w;h;ml;mt 的 ml/mt 数值等价于 tx/ty，可直接兼容解读（无需迁移）。
        private void SaveLayout()
        {
            foreach (var kv in _keys) SaveKeyLayout(kv.Key, kv.Value);
            foreach (var kv in _mouse) SaveKeyLayout(kv.Key, kv.Value);
        }

        private void SaveKeyLayout(string name, Border b)
        {
            double tx, ty;
            GetTransformXY(b, out tx, out ty);
            ApplicationData.Current.LocalSettings.Values[LayoutPrefix + name] =
                ((int)b.Width) + ";" + ((int)b.Height) + ";"
                + tx.ToString(CultureInfo.InvariantCulture) + ";" + ty.ToString(CultureInfo.InvariantCulture);
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
                double tx = double.Parse(parts[2], CultureInfo.InvariantCulture);
                double ty = double.Parse(parts[3], CultureInfo.InvariantCulture);
                if (w < MinKeyW || h < MinKeyH) return;
                b.Width = w;
                b.Height = h;
                SetTransformXY(b, tx, ty);
                b.Margin = new Thickness(0, 0, b.Margin.Right, b.Margin.Bottom);   // 左/上归零（位置=transform），保留右/下布局间距
            }
            catch
            {
            }
        }

        // 启动恢复鼠标垫显示状态：PadVisible_=0 → 隐藏（Collapsed），否则显示（默认显示）
        private void RestorePadVisibility()
        {
            try
            {
                object pv = ApplicationData.Current.LocalSettings.Values["PadVisible_"];
                bool visible = true;
                if (pv != null)
                {
                    if (pv is bool b) visible = b;
                    else visible = !(pv.ToString() == "0");
                }
                _padVisible = visible;
                MousePad.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
            catch
            {
                _padVisible = true;
                MousePad.Visibility = Visibility.Visible;
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