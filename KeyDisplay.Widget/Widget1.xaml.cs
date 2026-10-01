using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
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
        // 默认键 XAML 初始文本缓存（0.8.2）：RegisterDefaultKeys 首次登记时捕获，供重置按键布局还原显示名
        private readonly Dictionary<string, string> _defaultKeyTexts = new Dictionary<string, string>();
        // 自定义按键（"自定义控件"菜单从 87 配列布局添加）：按名字去重、动态创建、LocalSettings 持久化
        private readonly Dictionary<string, Border> _customKeys = new Dictionary<string, Border>();
        private readonly DispatcherTimer _modeTimer;
        private readonly InputStateReader _reader;
        private volatile InputSnapshot _latest;   // 后台读线程写 / UI 渲染线程读；volatile 保证可见性（0.8.3）
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
        // 0.8.3：companion 可能被 Game Bar 宿主回收/系统清理。原"单次拉起"flag 导致会话中途
        // companion 退出后 widget 永久断连（按键映射无效+圆点消失+预设空）。改记最近拉起时间戳，
        // 允许断线时重新拉起（30s 窗口内最多一次，防风暴）。
        // 0.8.4 修复：初始化必须为 0 而非 long.MinValue——后者与 DateTime.UtcNow.Ticks 相减会
        // 溢出环绕成负数，使「距上次拉起 <30s」条件恒成立，TryStartCompanion 永远直接 return
        // （0.9.1 起断线自愈与 fulltrust 拉起全部失效的根因）
        private static long s_lastCompanionLaunchTicks = 0;
        private DispatcherTimer _companionWatch;   // 断线监视：持续连不上管道时重拉 companion

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
        private const double SnapGap = 10.0;         // 间隔吸附（0.7.1）：每个候选键四边外扩一圈参考线，吸附"相邻但不接触"的 10px 间距，美观排布
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
        private CoreCursor _cursorRef;               // 0.8.2：持有当前赋给 CoreWindow 的光标强引用，防止 GC 回收导致光标消失

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
        private double _dragStartTx, _dragStartTy;          // 缩放按下时的 TranslateTransform 偏移起点（缩放 l/t 边补偿走 transform，不写 Margin 避免推挤兄弟）
        private readonly SolidColorBrush _snapSolid = new SolidColorBrush(SnapLineColor);   // 吸中实线画刷
        private readonly SolidColorBrush _snapDash = new SolidColorBrush(SnapHintColor);    // 接近提示虚线画刷

        // 暗色主题画刷（0.9.4 修正：恢复"按下反白"的既有语义与中性深灰基调——
        // 0.9.3 曾试过 Xbox 绿按下 + 蓝黑底，实际观感"颜色乱"，已回退）
        private readonly SolidColorBrush _darkDefaultBg = new SolidColorBrush(Color.FromArgb(0xF2, 0x1A, 0x1A, 0x1A));   // 键帽近黑（微透）
        private readonly SolidColorBrush _darkDefaultFg = new SolidColorBrush(Colors.White);
        private readonly SolidColorBrush _darkBorder = new SolidColorBrush(Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF));     // 淡白细边
        private readonly SolidColorBrush _darkPressedBg = new SolidColorBrush(Colors.White);                            // 按下反白
        private readonly SolidColorBrush _darkPressedFg = new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x10, 0x10));
        private readonly SolidColorBrush _darkPanel = new SolidColorBrush(Color.FromArgb(0xE8, 0x12, 0x12, 0x12));       // 深灰黑面板（半透明玻璃感，不带蓝调）
        private readonly SolidColorBrush _darkPad = new SolidColorBrush(Color.FromArgb(0x4D, 0x00, 0x00, 0x00));        // 鼠标垫半透明黑
        private readonly SolidColorBrush _darkDot = new SolidColorBrush(Colors.White);                                   // 鼠标点白色
        private readonly SolidColorBrush _darkDotPressed = new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));  // 0.9.5：鼠标点按下（亮蓝）
        private readonly SolidColorBrush _darkAccent = new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));      // 强调色亮蓝 #FF4CC2FF

        // 亮色主题画刷（0.9.4：中性白玻璃——去掉 0.9.3 引入的冷蓝调）
        private readonly SolidColorBrush _lightDefaultBg = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        private readonly SolidColorBrush _lightDefaultFg = new SolidColorBrush(Colors.Black);
        private readonly SolidColorBrush _lightBorder = new SolidColorBrush(Color.FromArgb(0x59, 0x33, 0x33, 0x33));
        private readonly SolidColorBrush _lightPressedBg = new SolidColorBrush(Colors.Black);
        private readonly SolidColorBrush _lightPressedFg = new SolidColorBrush(Colors.White);
        private readonly SolidColorBrush _lightPanel = new SolidColorBrush(Color.FromArgb(0xE0, 0xF5, 0xF5, 0xF5));
        private readonly SolidColorBrush _lightPad = new SolidColorBrush(Color.FromArgb(0x42, 0x00, 0x00, 0x00));
        private readonly SolidColorBrush _lightAccent = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x67, 0xC0));     // 强调色深蓝 #FF0067C0
        private readonly SolidColorBrush _transparent = new SolidColorBrush(Colors.Transparent);

        // 粉色主题画刷（用户拍板：字体白色，按键底加深一档保证白字可读；0.8.3 面板通透化）
        private readonly SolidColorBrush _pinkPanel = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xB3, 0xC6));   // 面板 #E0FFB3C6
        private readonly SolidColorBrush _pinkBorder = new SolidColorBrush(Color.FromArgb(0xCC, 0xB0, 0x57, 0x7E));  // 边框 #CCB0577E
        private readonly SolidColorBrush _pinkKeyBg = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xB3, 0xC6));   // 按键默认背景 #FFFFB3C6（原 #FFCDD8 太浅，白字看不清）
        private readonly SolidColorBrush _pinkKeyFg = new SolidColorBrush(Colors.White);       // 默认文字白色
        private readonly SolidColorBrush _pinkPressedBg = new SolidColorBrush(Colors.White);  // 按下白底
        private readonly SolidColorBrush _pinkPressedFg = new SolidColorBrush(Color.FromArgb(0xFF, 0xB0, 0x57, 0x7E));  // 按下深粉字
        private readonly SolidColorBrush _pinkPad = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xB3, 0xC6));      // 鼠标垫 #4DFFB3C6
        private readonly SolidColorBrush _pinkDot = new SolidColorBrush(Color.FromArgb(0xFF, 0xB0, 0x57, 0x7E));     // 鼠标点深粉
        private readonly SolidColorBrush _pinkDotPressed = new SolidColorBrush(Color.FromArgb(0xFF, 0xC2, 0x18, 0x5B)); // 0.9.5：鼠标点按下（玫红）
        private readonly SolidColorBrush _pinkAccent = new SolidColorBrush(Color.FromArgb(0xFF, 0xC2, 0x18, 0x5B));     // 强调色玫红 #FFC2185B

        // 灰色主题画刷（0.9.4：中性浅灰玻璃 + 黑字 + 深灰按下——去掉 0.9.3 引入的冷蓝调）
        private readonly SolidColorBrush _grayPanel = new SolidColorBrush(Color.FromArgb(0xE0, 0xCF, 0xCF, 0xCF));   // 面板中性中灰（0.9.5 修复：原 #EEEEEE 与「白」#F5F5F5 几乎同色，现拉开明显差距）
        private readonly SolidColorBrush _grayBorder = new SolidColorBrush(Color.FromArgb(0x5C, 0x5A, 0x5A, 0x5A));  // 边框
        private readonly SolidColorBrush _grayKeyBg = new SolidColorBrush(Color.FromArgb(0xFF, 0xEA, 0xEA, 0xEA));   // 按键浅灰（比面板亮一档，保证可辨）
        private readonly SolidColorBrush _grayKeyFg = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));   // 默认文字近黑
        private readonly SolidColorBrush _grayPressedBg = new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x4A, 0x4A));  // 按下深灰底
        private readonly SolidColorBrush _grayPressedFg = new SolidColorBrush(Colors.White);  // 按下白字
        private readonly SolidColorBrush _grayPad = new SolidColorBrush(Color.FromArgb(0x47, 0x00, 0x00, 0x00));      // 鼠标垫半透明黑
        private readonly SolidColorBrush _grayDot = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));      // 鼠标点近黑
        private readonly SolidColorBrush _grayDotPressed = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x67, 0xC0)); // 0.9.5：鼠标点按下（深蓝）
        private readonly SolidColorBrush _grayAccent = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x67, 0xC0));     // 强调色深蓝 #FF0067C0

        // 蓝色主题画刷（浅蓝玻璃 + 深蓝字；0.8.3 面板通透化）
        private readonly SolidColorBrush _bluePanel = new SolidColorBrush(Color.FromArgb(0xE0, 0xC3, 0xDC, 0xF0));   // 面板 #E0C3DCF0
        private readonly SolidColorBrush _blueBorder = new SolidColorBrush(Color.FromArgb(0x66, 0x3A, 0x6E, 0xA5));  // 边框 #663A6EA5
        private readonly SolidColorBrush _blueKeyBg = new SolidColorBrush(Color.FromArgb(0xFF, 0xD2, 0xE5, 0xF7));   // 按键默认背景 #FFD2E5F7
        private readonly SolidColorBrush _blueKeyFg = new SolidColorBrush(Color.FromArgb(0xFF, 0x1F, 0x4E, 0x79));   // 默认文字深蓝
        private readonly SolidColorBrush _bluePressedBg = new SolidColorBrush(Colors.White);  // 按下白底
        private readonly SolidColorBrush _bluePressedFg = new SolidColorBrush(Color.FromArgb(0xFF, 0x1F, 0x4E, 0x79));  // 按下深蓝字
        private readonly SolidColorBrush _bluePad = new SolidColorBrush(Color.FromArgb(0x59, 0xBF, 0xD9, 0xEE));      // 鼠标垫 #59BFD9EE
        private readonly SolidColorBrush _blueDot = new SolidColorBrush(Color.FromArgb(0xFF, 0x1F, 0x4E, 0x79));     // 鼠标点深蓝
        private readonly SolidColorBrush _blueDotPressed = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x64, 0xB4)); // 0.9.5：鼠标点按下（深蓝）
        private readonly SolidColorBrush _blueAccent = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x64, 0xB4));     // 强调色深蓝 #FF0A64B4

        // 按键透明度滑条设定值（0~100，默认 100）；锁定开=按此值，锁定关=临时强制 100%
        private double _keyOpacity = 100.0;
        // 0.9.5：鼠标速度（鼠标点移动倍率）：1.0 = 屏幕与垫面 1:1；调大后更少位移就碰到垫面边缘
        private double _mouseSpeed = 1.0;
        // 0.9.5：鼠标光标按键（0=关闭；1/2/4/5/6=左右中/侧下/侧上；7/8=滚轮上/下）——该键按下时光标用"按下色"
        private int _dotKeyVk;
        private double _dotSize = 10;   // 0.9.5：光标点大小（px）
        private double _keyScaleUser;   // 0.9.5：整体按键大小（-20..+20 %，0=原尺寸）
        private bool _dotKeyOn;   // 0.9.5：鼠标光标按键开关（关闭时忽略映射）
        // 0.9.5：按键区背景是否全透明（用户要求默认透明：键位/鼠标垫直接浮在游戏画面上）
        private bool _panelTransparent = true;

        // ===== 0.9.4：按键显示名字号 / 字重（设置面板两个滑条统一控制）=====
        // 背景：自定义键与粘贴副本原先硬编码 18 号，而默认键各不相同（左Shift/Ctrl/Alt/空格 13、
        // 鼠标键 12、滚轮键 10），导致"复制粘贴出来的键字体大小跟默认键不一样"。改为统一由这两个
        // 设置控制（默认 18 / SemiBold = 键盘键原值），新建与粘贴的键自动继承。
        private double _keyFontSize = 11.0;    // 0.9.5：默认字号（界面上显示为「10」，见设置窗口的号数偏移）
        private int _keyFontWeightLevel = 5;   // 0.9.5：1..10 —— 1=Thin(100) … 6=SemiBold(600) … 9/10=Black(900)，默认 5=Medium(500)
        private Windows.UI.Text.FontWeight _keyFontWeight = FontWeightFromLevel(5);
        // 字体族："system" 标记跟随系统（Win11 官方 UI 字体链）；其余为具体字体名
        private const string SystemFontTag = "system";
        private const string SystemFontChain = "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI";
        // 0.9.5：字重刻度标记。旧版本是 1..5（1=Light…5=ExtraBold），新版本 1..10。
        // 没有这个标记时说明是旧刻度存的，读出来 +2 即可精确对应到新刻度（3=SemiBold → 6=SemiBold）。
        private const string FontWeightScaleKey = "KeyFontWeightScale2_";
        private string _keyFontTag = SystemFontTag;

        // 0.9.5：字重阶 1..10（数字越大越粗，10 为最粗）。Windows 只有 9 级真实字重，
        // 这里用 100 的整数倍映射：1=Thin(100) 2=ExtraLight 3=Light 4=Normal 5=Medium
        // 6=SemiBold 7=Bold 8=ExtraBold 9/10=Black(900，到顶)
        private static Windows.UI.Text.FontWeight FontWeightFromLevel(int lv)
        {
            if (lv < 1) lv = 1;
            if (lv > 10) lv = 10;
            int w = lv * 100;
            if (w > 900) w = 900;
            return new Windows.UI.Text.FontWeight { Weight = (ushort)w };
        }

        // 旧刻度(1..5) → 新刻度(1..10)：+2 可保持视觉一致（旧 3=SemiBold → 新 6=SemiBold，旧 5=ExtraBold → 新 8=ExtraBold）
        private static int MigrateFontWeightLevel(int oldLevel, bool alreadyNewScale)
        {
            int lv = alreadyNewScale ? oldLevel : oldLevel + 2;
            if (lv < 1) lv = 1;
            if (lv > 10) lv = 10;
            return lv;
        }

        // 解析当前字体设置 → FontFamily 对象（跟随系统时用官方字体链）
        private FontFamily CurrentFontFamily()
        {
            try
            {
                string fam = _keyFontTag == SystemFontTag ? SystemFontChain : _keyFontTag;
                return new FontFamily(fam);
            }
            catch { return new FontFamily(SystemFontChain); }
        }


        // 把当前字号/字重应用到全部按键（默认键 + 鼠标键 + 自定义键）
        private void ApplyKeyFont()
        {
            foreach (var kv in _keys) ApplyKeyFontTo(kv.Value);
            foreach (var kv in _mouse) ApplyKeyFontTo(kv.Value);
            foreach (var kv in _customKeys) ApplyKeyFontTo(kv.Value);
        }

        private void ApplyKeyFontTo(Border b)
        {
            if (b == null) return;
            // 1.3：手柄部件（Gp_*）的标签字号/字重由部件自身按尺寸计算（见 GpRenderPart），不套用按键字号设置
            if (IsGpPartName(b.Tag as string)) return;
            var tb = b.Child as TextBlock;
            if (tb == null) return;
            try
            {
                tb.FontSize = _keyFontSize;
                tb.FontWeight = _keyFontWeight;
                tb.FontFamily = CurrentFontFamily();   // 0.9.4：字体族也随设置
            }
            catch { }
        }

        // ===================== 自定义主题色（10 槽位，custom 态）=====================
        // 持久化键（Custom_ 前缀，存 "#RRGGBB"）；缺省回落 dark 预设对应值
        private static readonly string[] CustomKeys = { "CustomPanel_", "CustomBorder_", "CustomKeyBg_", "CustomKeyFg_",
            "CustomPressedBg_", "CustomPressedFg_", "CustomPad_", "CustomDot_", "CustomAccent_",
            "CustomDotPressed_" };   // 0.9.5：第 10 槽「鼠标点·按下色」
        // 动态画刷：custom 态下各语义方法返回它们；启动/修改时用 Custom_ 键刷新
        private readonly SolidColorBrush[] _customBrushes = new SolidColorBrush[10];   // 0.9.5：9→10（新增鼠标点按下色）
        private bool _defaultPadPending = false;   // 内置默认预设的垫尺寸待首帧快照按本机屏幕比例重算（宽度沿用发布者，高度=宽×本机屏高/宽）


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
        // 0.9.5：鼠标点按下色（当「鼠标光标按键」被按下时使用）
        private Brush DotPressedB() => _theme == "custom" ? _customBrushes[9] : P(_darkDotPressed, _grayDotPressed, _grayDotPressed, _pinkDotPressed, _blueDotPressed);
        private Brush AccentB() => _theme == "custom" ? _customBrushes[8] : P(_darkAccent, _grayAccent, _lightAccent, _pinkAccent, _blueAccent);   // 强调色（面板工具按钮/高亮）
        // 强调色前景对比色：亮度感知加权判亮（>0.55）返回近黑字，否则白字；取不到 SolidColorBrush 时回落白色
        private Brush AccentFgB()
        {
            var scb = AccentB() as SolidColorBrush;
            if (scb == null) return new SolidColorBrush(Colors.White);
            Color c = scb.Color;
            double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            return lum > 0.55 ? new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x10, 0x10)) : new SolidColorBrush(Colors.White);
        }

        // ===================== 浮层派生色（0.8.2）：右键菜单/改名面板等浮层 =====================
        // 浮层直接复用面板色会与主面板融为一体，层次不清；这里按基准色亮度自适应偏移
        // （深底提亮 ~18%、浅底压暗 ~18%，边框再外扩一档），让浮层立体、美观且不抢主界面。

        // RGB 亮度缩放：factor>1 提亮、<1 变暗（保持色相/饱和度比例，A 原样保留）
        private static Color ShiftLuminance(Color c, double factor)
        {
            double r = c.R * factor, g = c.G * factor, b = c.B * factor;
            return Color.FromArgb(c.A, (byte)Math.Min(255.0, r), (byte)Math.Min(255.0, g), (byte)Math.Min(255.0, b));
        }

        // 基准色亮度（0~1，感知加权）
        private static double Luma(Color c)
        {
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        }

        // 浮层背景：由面板色派生的对比色（Bright solid brush，失败回落灰）
        private Brush FloatPanelB()
        {
            try
            {
                var c = ((SolidColorBrush)PanelB()).Color;
                return new SolidColorBrush(ShiftLuminance(c, Luma(c) > 0.5 ? 0.8 : 1.2));
            }
            catch { return new SolidColorBrush(Colors.Gray); }
        }

        // 浮层边框：由边框色派生（偏移比背景更大一档，增强描边立体感）
        private Brush FloatBorderB()
        {
            try
            {
                var c = ((SolidColorBrush)BorderB()).Color;
                return new SolidColorBrush(ShiftLuminance(c, Luma(c) > 0.5 ? 0.7 : 1.35));
            }
            catch { return new SolidColorBrush(Colors.DarkGray); }
        }

        // 内置默认布局预设（0.9.5 更新）：用户 2026-09-26 导出的当前布局（Tab 0,-224 / CapsLock 0,-170，隐藏全部鼠标键）。
// 含鼠标垫位置 padPos(94,0) 与 Tab 键尺寸 56;48（用户当前实际配置，以此为准）。
// 启动时若用户从未自定义过布局（无 Layout_* 持久化）自动套用；「重置布局」也回到这套。
        private const string BuiltInDefaultLayoutJson =
            @"{""formatVersion"":1,""type"":""layout"",""name"":""默认001"",""savedAt"":""2026-09-26T15:31:30"",""data"":{""keyOpacity"":98,""padVisible"":true,""keys"":{""Layout_D"":""52;48;73.9999923706055;-1.99999046325684"",""Layout_S"":""52;48;75.9999923706055;-1.9999885559082"",""Layout_Alt"":""68;48;6.00000762939453;-3.99974822998047"",""Layout_A"":""52;48;75.9999923706055;-1.9999885559082"",""Layout_Shift"":""66;48;0;-3.99972534179688"",""Layout_Ctrl"":""68;48;2;-3.99974822998047"",""Layout_F"":""52;48;75.9999923706055;-1.99999809265137"",""Layout_R"":""64;48;63.9999923706055;2.00027847290039"",""Layout_Space"":""76;48;228;-59.9997482299805"",""Layout_E"":""52;48;63.9999923706055;2.00027847290039"",""Layout_Q"":""52;48;63.9999923706055;2.00027847290039"",""Layout_W"":""52;48;63.9999923706055;2.00027847290039""},""customKeys"":{""Tab"":{""pos"":""0;-221.999725341797"",""size"":""56;48""},""CapsLock"":{""pos"":""0;-170"",""size"":""68;48""}},""deletedKeys"":[""X1"",""MR"",""L"",""M"",""X2"",""WheelUp"",""WheelDown""]}}";

        // 首次启动初始化默认布局：仅当用户从未自定义过布局（无 Layout_* 键）时，把内置默认预设写入持久化。
        // 只写持久化不重建 UI——构造函数场景由后续 Restore* 恢复链应用；重置场景由调用方补重建。
        // 0.9.5：内置默认鼠标垫 = 发布默认（宽 223.59、位置 (94,0)，高度按本机屏幕比例在首帧重算）。
        // 注意：键位不再套用上面那份内置预设（它的偏移基于 0.7.x 的另一套基准，叠到当前 XAML 基准会重叠），
        // 但鼠标垫的尺寸与位置必须沿用发布值——否则会掉回 XAML 的 80×80(=1:1) 与 (242,0)，用户看到的就是
        // "鼠标垫变成 1:1 了 / 位置不对"。
        private const double DefaultPadWidth = 246.371;   // 0.9.5：按用户导出实测同步
        private const double DefaultPadHeight = 154.0;    // 首帧会用本机屏幕比例重算（宽度 × 屏高/屏宽）
        private const double DefaultPadLeft = 72;      // 0.9.5：按用户导出实测同步（原 94 为发布旧值）
        private const double DefaultPadTop = 2.0;      // 0.9.5：同上

        private void ApplyDefaultPadOnly()
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                v["PadCustom_"] = 1;
                v["PadW"] = DefaultPadWidth.ToString(CultureInfo.InvariantCulture);
                v["PadH"] = DefaultPadHeight.ToString(CultureInfo.InvariantCulture);
                v["PadPos_left"] = DefaultPadLeft.ToString(CultureInfo.InvariantCulture);
                v["PadPos_top"] = DefaultPadTop.ToString(CultureInfo.InvariantCulture);
                _defaultPadPending = true;   // 首帧快照到达后按本机屏幕比例重算高度（宽度沿用发布值）
                // 立即同步到 UI，不依赖首帧（重置后马上归位）
                MousePad.Width = DefaultPadWidth;
                MousePad.Height = DefaultPadHeight;
                SetTransformXY(MousePad, DefaultPadLeft, DefaultPadTop);
                DiagLog("default pad applied: " + (int)DefaultPadWidth + "x" + (int)DefaultPadHeight
                        + " @" + (int)DefaultPadLeft + "," + (int)DefaultPadTop);
            }
            catch (Exception ex) { DiagLog("default pad fail: " + ex.Message); }
        }
        private void ApplyBuiltInDefaultLayoutIfNeeded()
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                foreach (var kv in v)
                    if (kv.Key.StartsWith(LayoutPrefix, StringComparison.Ordinal)) return;   // 用户已有布局自定义，尊重用户
                string err;
                var p = PresetIO.ParseExport(BuiltInDefaultLayoutJson, out err);
                if (p == null) { DiagLog("builtin default layout parse fail: " + err); return; }
                if (p.Keys != null)
                    foreach (var kv in p.Keys)
                        if (kv.Value != null) v[kv.Key] = kv.Value;
                if (p.CustomKeys != null)
                    foreach (var kv in p.CustomKeys)
                    {
                        v["Custom_" + kv.Key] = "1";
                        v["CustomPos_" + kv.Key] = string.IsNullOrEmpty(kv.Value.Pos) ? "0;0" : kv.Value.Pos;
                        v["CustomSize_" + kv.Key] = string.IsNullOrEmpty(kv.Value.Size) ? "" : kv.Value.Size;
                    }
                if (p.DeletedKeys != null)
                    foreach (var nm in p.DeletedKeys)
                        if (!string.IsNullOrEmpty(nm)) v["Deleted_" + nm] = 1;
                v["KeyOpacity_"] = Math.Max(10, Math.Min(100, p.KeyOpacity));
                v["PadVisible_"] = p.PadVisible ? 1 : 0;
                // 鼠标垫：宽度沿用发布者，高度待首帧快照按本机虚拟屏幕比例重算（与预设导入语义一致：同步尺寸、比例跟随本机）
                if (p.PadW > 0 && p.PadH > 0)
                {
                    v["PadCustom_"] = 1;
                    v["PadW"] = p.PadW.ToString(CultureInfo.InvariantCulture);
                    v["PadH"] = p.PadH.ToString(CultureInfo.InvariantCulture);
                    // 鼠标垫位置（0.7.1）：预设带 padPos 时同步发布者位置
                    if (p.PadPosX.HasValue)
                        v["PadPos_left"] = p.PadPosX.Value.ToString(CultureInfo.InvariantCulture);
                    if (p.PadPosY.HasValue)
                        v["PadPos_top"] = p.PadPosY.Value.ToString(CultureInfo.InvariantCulture);
                    _defaultPadPending = true;
                }
                DiagLog("builtin default layout applied: keys=" + (p.Keys != null ? p.Keys.Count : 0)
                        + " custom=" + (p.CustomKeys != null ? p.CustomKeys.Count : 0)
                        + " padW=" + (int)p.PadW);
            }
            catch (Exception ex)
            {
                DiagLog("builtin default layout fail: " + ex.Message);
            }
        }

        // 首帧快照到达后按本机虚拟屏幕比例修正默认垫高度（宽度不变，比例跟随本机）
        private void ApplyDefaultPadRatio(int vsW, int vsH)
        {
            try
            {
                _defaultPadPending = false;
                var v = ApplicationData.Current.LocalSettings.Values;
                double pw = ReadSettingDouble(v, "PadW", MousePad.Width);
                double ph = pw * vsH / vsW;
                if (ph < MinPadH) { double f = MinPadH / ph; ph = MinPadH; pw *= f; }
                if (pw < MinPadW) { double f = MinPadW / pw; pw = MinPadW; ph *= f; }
                v["PadW"] = pw.ToString(CultureInfo.InvariantCulture);
                v["PadH"] = ph.ToString(CultureInfo.InvariantCulture);
                MousePad.Width = pw;
                MousePad.Height = ph;
                _padW = pw;
                _padH = ph;
                DiagLog("default pad ratio applied: " + (int)pw + "x" + (int)ph + " (vs " + vsW + "x" + vsH + ")");
            }
            catch (Exception ex)
            {
                DiagLog("default pad ratio fail: " + ex.Message);
            }
        }

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
            CaptureDefaultBoxes();   // 0.9.5：记录 XAML 初始尺寸/位置（重置按键布局时精确还原用）
            // 0.9.5：用户从未自定义过鼠标垫时，套用发布默认尺寸/位置（223.59 宽 @(94,0)，高度按屏幕比例）
            try
            {
                var v0 = ApplicationData.Current.LocalSettings.Values;
                if (v0["PadCustom_"] == null) ApplyDefaultPadOnly();
            }
            catch { }

            // 0.9.5：无用户布局自定义时套用内置默认布局（用户 2026-09-26 导出的当前布局：
            // Tab(0,-224) / CapsLock(0,-170)、隐藏全部鼠标键）。已有布局的用户不受影响（函数内部会提前返回）。
            ApplyBuiltInDefaultLayoutIfNeeded();

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

            // 恢复 9 槽自定义值（custom 态生效，预设态忽略）
            for (int k = 0; k < CustomKeys.Length; k++) _customBrushes[k] = new SolidColorBrush(Colors.Black);
            RefreshCustomBrushes();
            if (_theme == "custom") ApplyTheme();   // 语义方法的 custom 分支需要 _theme 已定后应用一次

            // 恢复按键透明度设定值（KeyOpacity_ 存 0~100；缺失默认 100），并应用
            object op = ApplicationData.Current.LocalSettings.Values["KeyOpacity_"];
            _keyOpacity = (op is int oi && oi >= 10 && oi <= 100) ? oi : 100.0;
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
                // 0.8.3：幂等订阅——Game Bar 展开/收起可能造成 Unloaded/Loaded 不成对，
                // 重复 += 会让 OnRendering 每帧跑两遍（自定义键逐帧重绘翻倍）
                widget.GameBarDisplayModeChanged -= OnGameBarDisplayModeChanged;
                widget.GameBarDisplayModeChanged += OnGameBarDisplayModeChanged;
                widget.PinnedChanged -= OnPinnedChanged;
                widget.PinnedChanged += OnPinnedChanged;
                try { DiagLog("widget present, initial docked=" + _docked + " mode=" + widget.GameBarDisplayMode + " pinned=" + widget.Pinned); } catch { }
                // 0.9.5：Game Bar 标题栏设置按钮 → 打开设置子窗口
                try
                {
                    widget.SettingsClicked -= OnSettingsClicked;
                    widget.SettingsClicked += OnSettingsClicked;
                }
                catch (Exception ex) { DiagLog("hook SettingsClicked fail: " + ex.Message); }
                // 0.9.5：设置子窗口改设置后实时重载（SignalDataChanged → DataChanged）
                try
                {
                    ApplicationData.Current.DataChanged -= OnAppDataChanged;
                    ApplicationData.Current.DataChanged += OnAppDataChanged;
                }
                catch (Exception ex) { DiagLog("hook DataChanged fail: " + ex.Message); }
            }
            else
            {
                DiagLog("widget null");
            }
            ApplyTheme();
            MigrateTabSize();          // 0.8.2：修复 1.6.0.0 版改名宽度自适应对 Tab 尺寸的污染（须在 RestoreCustomKeys 之前）
            RestoreCustomKeys();       // 内部调用 OffsetKeyLayerForNegativeKeys（0.8.3 负坐标键左缘补偿）
            RestoreKeyFontSettings();  // 0.9.4：恢复字号/字重设置并同步两个滑条
            HookWindowAdapt();         // 0.9.4 窗口自适应（键区等比缩放）
            HookKeyLayerPaste();          // 0.8.1：键区空白右键 = 粘贴已复制的按键
            ApplyDisplayNamesToDefaults();   // 0.8.1：恢复默认键的自定义显示名
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
            CompositionTarget.Rendering -= OnRendering;   // 0.8.3：幂等订阅（防 Unloaded/Loaded 不成对时重复注册）
            CompositionTarget.Rendering += OnRendering;
            _modeTimer.Start();
            _reader.Start();
            TryStartCompanion();          // 先确保伴生进程在跑（协议拉起，含系统重启后首次启动），再拉取预设
            StartCompanionWatch();        // 0.8.3：断线自动重拉（companion 被外部回收后按键/圆点自动恢复）
            StartupFadeIn(RootPanel);     // 0.8.2 整体启动淡入（320ms，透明度动画无残留）
            // 0.9.5：记录初始键区结构指纹，供设置子窗口改布局时比对触发重建
            try { _customKeysFingerprint = CustomKeysFingerprint(); } catch { }
            try { _lastLayoutResetReq = ParseLongOr(ApplicationData.Current.LocalSettings.Values[LayoutResetRequestKey], 0); } catch { }
            try { _lastLayoutSnapshotReq = ParseLongOr(ApplicationData.Current.LocalSettings.Values["LayoutSnapshotRequest_"], 0); } catch { }   // 0.9.5：启动时登记已有标记，避免把历史重置请求当成新的重复执行
            // 0.9.6 修复：冷启动也要从存储恢复全部设置。
            // 原来 ReloadSettingsFromStore() 只挂在 AppDataChanged（设置子窗口改动时的实时重载）上，
            // 冷启动只走了 RestoreCustomKeys/RestoreKeyFontSettings/HookWindowAdapt，
            // 于是「整体按键大小 KeyScale_」以及光标大小 DotSize_、鼠标速度 MouseSpeed_、
            // 鼠标光标按键 MouseDotKeyOn_/Vk_、面板透明 PanelTransparent_ 等在关闭小组件
            // 重新进入后全部回到默认值。此处放在标记键登记之后（不会被当成新重置请求误触发），
            // 也在 HookWindowAdapt() 之后（_keyScale 已建好，整体缩放立即生效）。
            try { ReloadSettingsFromStore(); DiagLog("cold start: settings applied from store"); }
            catch (Exception ex) { DiagLog("cold start settings fail: " + ex.Message); }
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
            if (_companionWatch != null) _companionWatch.Stop();   // 0.8.3：页面卸载停断线监视
            _reader.Dispose();
            _latest = null;
        }

        private async void TryStartCompanion()
        {
            // 0.8.3：可重复拉起。30s 窗口内最多触发一次协议拉起（companion 冷启动 2~4s，间隔足够），
            // 防在高频断连监视下反复弹起进程。
            long now = DateTime.UtcNow.Ticks;   // UWP 环境无 Environment.TickCount64，用 UtcNow.Ticks
            // 0（从未拉起过）时必须放行；否则按 30s 窗口限频
            if (s_lastCompanionLaunchTicks != 0 &&
                now - s_lastCompanionLaunchTicks < TimeSpan.FromSeconds(30).Ticks) return;
            s_lastCompanionLaunchTicks = now;
            // 0.8.4 回退说明：曾尝试"包内完整信任进程"（FullTrustProcessLauncher）实现零设置启动，
            // 但带包身份的进程受系统作业对象/策略限速，实测性能严重下降（光标延迟、按键反馈滞后），
            // 因此回到独立进程（Program Files\KeyDisplay）方案：由安装器负责常驻
            // （计划任务登录自启 + 安装即启动 + 失败重启），性能与旧版一致。
            // ② 协议拉起（独立进程路径；沙箱内可能被宿主拦截，作为辅助手段）
            try
            {
                await Launcher.LaunchUriAsync(new Uri("keydisplay://start"));
            }
            catch
            {
            }
        }

        // 0.8.3 断线监视：companion 被 Game Bar 回收/系统清理后，widget 侧持续连不上管道
        // （CreateFileW err=2）→ 定时重拉 companion，自动恢复按键映射/圆点/预设，无需重开 Game Bar。
        private void StartCompanionWatch()
        {
            if (_companionWatch != null) { _companionWatch.Start(); return; }
            _companionWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _companionWatch.Tick += (s, e) =>
            {
                try
                {
                    if (!_reader.Connected) TryStartCompanion();
                    // 1.4：auto 风格下定期重试品牌检测（内部 10s 限频；非 auto 时立即返回，零开销）
                    TryStartGamepadBrandDetect();
                }
                catch
                {
                }
            };
            _companionWatch.Start();
        }

        // 0.8.3：构造官方 Game Bar 风 Acrylic 玻璃面板（磨砂纹理 + 主题色 tint，tint 不透明化保证颜色纯正）；
        // 环境不支持（无 HostBackdrop / 异常）时回落半透明面板色，不影响主题系统
        private Brush TryAcrylicPanel(SolidColorBrush panel)
        {
            try
            {
                var c = panel.Color;
                var ab = new Windows.UI.Xaml.Media.AcrylicBrush
                {
                    TintColor = Windows.UI.Color.FromArgb(0xFF, c.R, c.G, c.B),
                    TintOpacity = 0.85,
                    FallbackColor = Windows.UI.Color.FromArgb(0xE8, c.R, c.G, c.B),
                    BackgroundSource = Windows.UI.Xaml.Media.AcrylicBackgroundSource.HostBackdrop
                };
                return ab;
            }
            catch { return panel; }
        }

// 0.9.4 窗口自适应（用户反馈：窗口拉小后"设置"按钮/底部内容看不到）：
        // Row0 已改为可伸缩，但键区是 Canvas 绝对布局（固有 380×272），高度不足时按键会被裁。
        // 这里按可用可视区等比缩放整个键区（左上为锚点），保证按键完整可见；
        // "设置"按钮在键区之外、锚定 RootPanel 右下角，缩放不影响它，任何窗口尺寸下都能点到。
        private ScaleTransform _keyScale;

        private void HookWindowAdapt()
        {
            try
            {
                _keyScale = new ScaleTransform { ScaleX = 1, ScaleY = 1, CenterX = 0, CenterY = 0 };
                KeyLayer.RenderTransform = _keyScale;
                RootPanel.SizeChanged += (s, e) => FitLayoutToWindow();
                FitLayoutToWindow();
            }
            catch (Exception ex) { DiagLog("window adapt hook fail: " + ex.Message); }
        }

        private void FitLayoutToWindow()
        {
            if (_keyScale == null || RootPanel == null) return;
            double availW = RootPanel.ActualWidth - 32;              // RootPanel Padding 16×2
            double availH = RootPanel.ActualHeight - 32;              // RootPanel Padding 16×2
            if (availW <= 0 || availH <= 0) return;
            const double needW = 380, needH = 272;                   // 键区固有设计尺寸
            double scale = Math.Min(1.0, Math.Min(availW / needW, availH / needH));
            if (scale < 0.4) scale = 0.4;                            // 下限：再小就看不清了
            // 0.9.5：叠加"整体按键大小"（用户可在设置窗口按 -20%..+20% 调整，0 = 原尺寸）
            scale *= (1.0 + _keyScaleUser / 100.0);
            if (scale < 0.15) scale = 0.15;
            if (scale > 3.0) scale = 3.0;
            _keyScale.ScaleX = scale;
            _keyScale.ScaleY = scale;
            // 0.9.5：键区缩放/窗口尺寸变化时，若右键菜单开着则重算位置（否则菜单不跟随窗口）
            if (KeyMenuPanel != null && KeyMenuPanel.Visibility == Visibility.Visible) PositionMenu();
        }

        private void ApplyTheme()
        {
            // 0.9.4：主面板使用主题色（半透明玻璃感）。曾试过 Acrylic 磨砂（HostBackdrop），
            // 但底色会随背后游戏画面变化、观感"颜色乱"且不可控，故回退为纯主题色。
            RootPanel.Background = PanelB();
            RootPanel.BorderBrush = BorderB();
            if (_panelTransparent)
            {
                // 0.9.5：背景全透明（可在设置窗口「布局」页切回不透明）——键位与鼠标垫浮在画面上，
                // 不再有面板底色/描边；固定叠加态（_docked）本来就透明，行为一致
                RootPanel.Background = _transparent;
                RootPanel.BorderBrush = _transparent;
            }
            MousePad.Background = PadB();
            MousePad.BorderBrush = BorderB();
            MouseDot.Fill = DotB();
            MouseDot.Visibility = Visibility.Collapsed;

            foreach (var kv in _keys) SetKey(kv.Value, false);
            foreach (var kv in _mouse) SetKey(kv.Value, false);
            // 1.3：手柄部件（Gp_*）的配色由 ApplyGamepadCustomParts 按部件类型维护（渲染循环每帧刷新），
            // 此处跳过——否则圆环/扳机条会被刷成填充键帽底色
            foreach (var kv in _customKeys) { if (IsGpPartName(kv.Key)) continue; SetKey(kv.Value, false); }
            if (_moveKey != null) { EndMoveStyle(_moveKey); _moveKey = null; }   // 主题切换时清除移动高亮（鼠标垫走专属恢复）
            _deleteConfirmKey = null;
            if (DeleteConfirmPanel != null) DeleteConfirmPanel.Visibility = Visibility.Collapsed;

            if (_docked)
            {
                // Game Bar 关闭、仅固定组件叠加显示时：隐藏面板背景/边框与状态字，只留按键
                RootPanel.Background = _transparent;
                RootPanel.BorderBrush = _transparent;
                StatusText.Visibility = Visibility.Collapsed;
            }
            else
            {
                StatusText.Visibility = Visibility.Visible;
            }

            ApplySettingsColors();
            // 1.2：手柄组跟随主题/风格重刷（字形 + 静息态配色 + 部件显隐；按下态由每帧 ApplyGamepadState 覆盖）
            ApplyGamepadSettings();

            ApplicationData.Current.LocalSettings.Values["Theme"] = _theme;
        }

        // 0.9.6 性能：多选框描边用的固定厚度（原来每帧 new Thickness(2)，改为复用同一实例）
        private static readonly Thickness SelectedBorderThickness = new Thickness(2);

        private void SetKey(Border border, bool down)
        {
            // 0.9.6 性能：本方法在渲染循环里对每个键每帧调用（60~480Hz），而依赖属性写入
            // 会触发失效/重绘。这里改成"幂等写入"——只在当前值确实不等于目标值时才写，
            // 外观结果与原实现完全一致（被别处改过样式的键依然会被纠正回来）。
            var bg = down ? PressBgB() : KeyBgB();
            if (!ReferenceEquals(border.Background, bg)) border.Background = bg;
            // 0.9.4：多选模式下选中的键保持固定红框——渲染循环每帧都会调 SetKey，
            // 若不判断就会把选中描边覆盖回主题边框色（用户反馈"多选框还是以前的灰色"）
            if (IsKeySelected(border))
            {
                if (!ReferenceEquals(border.BorderBrush, MultiSelectBrush)) border.BorderBrush = MultiSelectBrush;
                var bt = border.BorderThickness;
                if (bt.Left != 2.0 || bt.Top != 2.0 || bt.Right != 2.0 || bt.Bottom != 2.0)
                    border.BorderThickness = SelectedBorderThickness;
            }
            else
            {
                var bb = BorderB();
                if (!ReferenceEquals(border.BorderBrush, bb)) border.BorderBrush = bb;
            }
            var tb = border.Child as TextBlock;
            if (tb != null)
            {
                var fg = down ? PressFgB() : KeyFgB();
                if (!ReferenceEquals(tb.Foreground, fg)) tb.Foreground = fg;
            }
        }

        // 移动落位/丢捕获时恢复按键样式：普通键走 SetKey(false)；鼠标垫恢复其专属半透明背景（避免被默认键样式覆盖）
        private void EndMoveStyle(Border key)
        {
            if (key == MousePad)
            {
                MousePad.Background = PadB();
                MousePad.BorderBrush = BorderB();
            }
            else if (IsGpPartName(NameOf(key)))
            {
                // 1.3：手柄部件落位后按部件类型恢复（圆环/扳机条的底与边色与键帽不同）
                try { ApplyGamepadCustomParts(_latest); } catch { }
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
                    // 1.3：手柄部件（Gp_*）不是虚拟键，绝不查 VK 位图——改由 ApplyGamepadCustomParts 按手柄快照更新
                    if (IsGpPartName(kv.Key)) continue;
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
            // 1.3：手柄部件（Gp_*）逐帧按手柄快照更新其视觉（未添加任何部件时立即返回，零开销；
            // 内部自带 try/catch，手柄侧异常绝不影响键鼠渲染）
            ApplyGamepadCustomParts(snap);
            if (snap == null)
            {
                if (_lastSeq != uint.MaxValue)
                {
                    _lastSeq = uint.MaxValue;
                    StatusText.Text = "\u672a\u8fde\u63a5"; // 未连接
                }
                _hasSmoothTarget = false;
                // 1.2：数据断流（伴生进程未运行/管道断开）时手柄组按"未连接"处理：自动模式下折叠，
                // 避免整组停在最后一帧的假状态；始终显示模式保留静息态。键鼠原有逻辑一字未改。
                if (_gpMode != 2)
                {
                    GpSetVisible(GamepadPanel, false);
                    _gpPanelShown = false;
                    _gpStickInit = false;
                    _gpStickAnim = false;
                }
                return;
            }
            if (snap.Seq != _lastSeq)
            {
                _lastSeq = snap.Seq;
                if (!string.IsNullOrEmpty(StatusText.Text)) StatusText.Text = "";   // 0.9.6 性能：幂等写入

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
                // 滚轮键（0.7.0）：非 Mouse 掩码，从 VK 位图读（0x07=滚轮上 0x08=滚轮下，companion 滚动后点亮 150ms）
                if (snap.ExtraKeys != null)
                {
                    bool wUp = ((snap.ExtraKeys[0] >> 7) & 1) != 0;      // VK 0x07
                    bool wDown = ((snap.ExtraKeys[1] >> 0) & 1) != 0;    // VK 0x08
                    if (_mouse.TryGetValue("WheelDown", out mL)) { if (mL != _moveKey) SetKey(mL, wDown); }
                    if (_mouse.TryGetValue("WheelUp", out mL)) { if (mL != _moveKey) SetKey(mL, wUp); }
                }

                UpdatePadSize(snap.VsW, snap.VsH);
                _lastMouseBits = snap.Mouse;          // 0.9.5：缓存鼠标位（光标按键映射用）
                _lastExtraKeys = snap.ExtraKeys;      // 0.9.5：缓存 VK 位图（同上）
                // 0.7.1：内置默认预设的垫尺寸在首帧快照到达后按本机虚拟屏幕比例重算（宽度沿用发布者，比例跟随本机）
                if (_defaultPadPending && snap.VsW > 0 && snap.VsH > 0) ApplyDefaultPadRatio(snap.VsW, snap.VsH);
                // 目标点：绝对屏幕坐标 → 垫面位置（点 = 屏幕的真实镜像）
                double vw = snap.VsW > 0 ? snap.VsW : 1920;
                double vh = snap.VsH > 0 ? snap.VsH : 1080;
                double tx = ((snap.MouseX - snap.VsX) / vw) * _padW;
                double ty = ((snap.MouseY - snap.VsY) / vh) * _padH;
                // 0.9.5：鼠标速度倍率 —— 以垫面中心为基准放大/缩小相对位移（1.0 时与原来完全一致）
                if (Math.Abs(_mouseSpeed - 1.0) > 0.001)
                {
                    tx = _padW / 2.0 + (tx - _padW / 2.0) * _mouseSpeed;
                    ty = _padH / 2.0 + (ty - _padH / 2.0) * _mouseSpeed;
                }
                // 0.9.5：边界钳制跟随光标尺寸（原来写死 10，光标变大后会溢出垫面）
                tx = Math.Max(0.0, Math.Min(_padW - _dotSize, tx));
                ty = Math.Max(0.0, Math.Min(_padH - _dotSize, ty));
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

                // 1.2：手柄组状态映射（与键鼠共用同一份快照；未连接/未握手时整组不显示）
                ApplyGamepadState(snap);
            }

            // 1.2：手柄摇杆点平滑追赶（独立于鼠标点；静止到位后零开销）
            UpdateGamepadSticks();

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
            if (MouseDot.Visibility != Visibility.Visible) MouseDot.Visibility = Visibility.Visible;
            // 0.9.5：鼠标光标按键映射 —— 命中的键按下时，光标改用"鼠标点按下"色
            // 0.9.6 性能：颜色只在真正变化时写入（幂等写入），未按光标按键时不再每帧写 DP
            try
            {
                var fill = IsDotKeyDown(_dotKeyOn ? _dotKeyVk : 0) ? DotPressedB() : DotB();
                if (!ReferenceEquals(MouseDot.Fill, fill)) MouseDot.Fill = fill;
            }
            catch { }
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
            _defaultPadPending = false;   // 用户手动调整过垫：放弃默认预设的比例修正
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

        // 浮层配色：删除确认框 / 改名面板随当前主题刷新。
        private void ApplySettingsColors()
        {
            // 删除确认框与改名面板：浮层派生色（0.8.2，与右键菜单链路统一）
            DeleteConfirmBox.Background = FloatPanelB();
            DeleteConfirmBox.BorderBrush = FloatBorderB();
            DeleteConfirmText.Foreground = KeyFgB();
            RenameBox.Background = FloatPanelB();
            RenameBox.BorderBrush = FloatBorderB();
            RenameTitle.Foreground = KeyFgB();
            RenameInput.Background = KeyBgB();
            RenameInput.Foreground = KeyFgB();
            RenameInput.BorderBrush = BorderB();

            DeleteConfirmYes.Background = KeyBgB();
            DeleteConfirmYes.BorderBrush = BorderB();
            DeleteConfirmYesText.Foreground = KeyFgB();
            DeleteConfirmNo.Background = KeyBgB();
            DeleteConfirmNo.BorderBrush = BorderB();
            DeleteConfirmNoText.Foreground = KeyFgB();
            RefreshKeyMenuColors();   // 0.9.5：配色变化立即刷新已打开的右键菜单
        }


        // 0.9.5：Game Bar 标题栏的「设置」按钮 → 打开设置子窗口
        private async void OnSettingsClicked(Microsoft.Gaming.XboxGameBar.XboxGameBarWidget sender, object args)
        {
            try { await sender.ActivateSettingsAsync(); DiagLog("settings widget activated (titlebar)"); }
            catch (Exception ex) { DiagLog("titlebar settings fail: " + ex.Message); }
        }

        // 0.9.5：设置子窗口写入设置后 SignalDataChanged() → 这里实时重载，无需重开小组件
        private async void OnAppDataChanged(Windows.Storage.ApplicationData sender, object args)
        {
            try
            {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    try { ReloadSettingsFromStore(); }
                    catch (Exception ex) { DiagLog("reload settings fail: " + ex.Message); }
                });
            }
            catch { }
        }

        // 从 LocalSettings 重读全部外观设置并应用（主题/自定义色/字体/透明度/鼠标垫可见性/锁定）
        // 0.9.5：设置子窗口也可能改动「自定义按键集合」与布局（添加按键 / 应用布局预设 / 重置布局），
        // 因此这里检测 Custom_* 前缀键的集合指纹，有变化就整体重建键区。
        private string _customKeysFingerprint = "";
        // 0.9.5：设置窗口的「重置按键布局」通过标记键触发，由本进程（真正持有这些键的一方）执行：
        // 清掉布局/自定义键/显示名/删除记录 + 重新套用内置默认布局预设（含 Tab 与鼠标垫默认尺寸）+ 重建键区。
        // 原因：设置窗口跨进程枚举这些键时曾出现 removed 0 keys（一个都没枚举到），重置看起来毫无反应。
        private const string LayoutResetRequestKey = "LayoutResetRequest_";
        private long _lastLayoutResetReq;

        // 0.9.5：重置按键布局（由设置窗口写标记键触发）—— 清掉用户布局/自定义键/显示名/删除记录
        // 与鼠标垫自定义，再重新套用内置默认布局预设（含 Tab 自定义键与鼠标垫默认尺寸/位置），
        // 最后交给 ReloadSettingsFromStore 的结构变化分支重建键区。返回清掉的键数量（用于日志）。
        private int ResetLayoutToBuiltInDefault(Windows.Foundation.Collections.IPropertySet v)
        {
            int n = 0;
            try
            {
                var rm = new System.Collections.Generic.List<string>();
                foreach (var kv in v)
                {
                    string k = kv.Key;
                    if (k.StartsWith("Layout_", StringComparison.Ordinal) ||
                        k.StartsWith("Custom_", StringComparison.Ordinal) ||
                        k.StartsWith("CustomPos_", StringComparison.Ordinal) ||
                        k.StartsWith("CustomSize_", StringComparison.Ordinal) ||
                        k.StartsWith("DisplayName_", StringComparison.Ordinal) ||
                        k.StartsWith("Deleted_", StringComparison.Ordinal) ||
                        k == "PadCustom_" || k == "PadW" || k == "PadH" ||
                        k.StartsWith("PadPos_", StringComparison.Ordinal))
                        rm.Add(k);
                }
                foreach (var k in rm) v.Remove(k);
                n = rm.Count;

                // 关键：主动把内置键与鼠标垫还原成 XAML 初始尺寸/位置/显示名（旧位移不会自己消失）
                foreach (var kv in _keys) ResetOneKeyToDefault(kv.Key, kv.Value);
                foreach (var kv in _mouse) ResetOneKeyToDefault(kv.Key, kv.Value);
                ApplyBuiltInDefaultLayoutIfNeeded();   // 0.9.5：键位/自定义键/隐藏键回到内置默认布局
                ApplyDefaultPadOnly();                 // 0.9.5：鼠标垫恢复发布默认（223.59 宽 / (94,0)，高度按屏幕比例重算）

                // 0.9.5 修复：这里原先硬写 CustomPos_Tab="0;0"，会把内置默认里 Tab 的真实位置
                // （自定义键面板内的 0,-222，即显示在 Q 左侧、CapsLock 上方）覆盖掉 → 重置后 Tab 掉到下方。
                // 现在完全交给内置默认预设（ApplyBuiltInDefaultLayoutIfNeeded 已写入 Tab/CapsLock 的位置与尺寸）。

                DiagLog("layout reset: cleared " + n + " keys, defaults restored");
            }
            catch (Exception ex) { DiagLog("reset layout impl fail: " + ex.Message); }
            return n;
        }

        private static long ParseLongOr(object o, long def)
        {
            if (o == null) return def;
            long r;
            return long.TryParse(o.ToString(), out r) ? r : def;
        }

        // 0.9.5：把内置键/鼠标垫恢复成 XAML 初始尺寸、位置与显示名。
        // 关键：Layout_ 里的 tx/ty 是相对位移（TranslateTransform），RestoreKeyLayout 在没有对应键时
        // 直接 return，所以"只删布局键"不会清掉已应用的位移/尺寸 → 重置后仍是一片乱。必须主动还原。
        private readonly System.Collections.Generic.Dictionary<string, double[]> _defaultKeyBox =
            new System.Collections.Generic.Dictionary<string, double[]>();
        private double[] _defaultPadBox;   // [w, h, canvasLeft, canvasTop]

        private void CaptureDefaultBoxes()
        {
            try
            {
                if (_defaultKeyBox.Count > 0) return;
                foreach (var kv in _keys)
                {
                    double w = kv.Value.Width, h = kv.Value.Height;
                    if (!double.IsNaN(w) && !double.IsNaN(h)) _defaultKeyBox[kv.Key] = new[] { w, h };
                }
                foreach (var kv in _mouse)
                {
                    double w = kv.Value.Width, h = kv.Value.Height;
                    if (!double.IsNaN(w) && !double.IsNaN(h)) _defaultKeyBox[kv.Key] = new[] { w, h };
                }
                _defaultPadBox = new[] { MousePad.Width, MousePad.Height, Canvas.GetLeft(MousePad), Canvas.GetTop(MousePad) };
                DiagLog("default boxes captured: keys=" + _defaultKeyBox.Count + " pad=" + (int)_defaultPadBox[0] + "x" + (int)_defaultPadBox[1]);
            }
            catch (Exception ex) { DiagLog("capture defaults fail: " + ex.Message); }
        }

        private void ResetOneKeyToDefault(string name, Border b)
        {
            try
            {
                double[] box;
                if (_defaultKeyBox.TryGetValue(name, out box))
                {
                    b.Width = box[0];
                    b.Height = box[1];
                }
                SetTransformXY(b, 0, 0);
                string txt;
                if (_defaultKeyTexts.TryGetValue(name, out txt))
                {
                    var tb = b.Child as TextBlock;
                    if (tb != null) tb.Text = txt;
                }
                b.Visibility = Visibility.Visible;   // 被删除过的默认键一并恢复
                SetKey(b, false);
            }
            catch { }
        }

        private void ResetPadToDefault()
        {
            try
            {
                if (_defaultPadBox == null) return;
                MousePad.Width = _defaultPadBox[0];
                MousePad.Height = _defaultPadBox[1];
                Canvas.SetLeft(MousePad, _defaultPadBox[2]);
                Canvas.SetTop(MousePad, _defaultPadBox[3]);
                SetTransformXY(MousePad, 0, 0);
            }
            catch { }
        }

        // 判定"鼠标光标按键"是否按下。按 snapshot 缓存上次结果，供动画帧复用。
        private bool _dotKeyDownCached;

        private bool _dotKeyDown;
        private byte _lastMouseBits;
        private byte[] _lastExtraKeys;

        private bool IsDotKeyDown(int vk)
        {
            if (vk <= 0) return false;
            // 鼠标键：优先用 mouse 位（可靠、每帧更新）；滚轮 7/8 与其它键走 VK 位图
            switch (vk)
            {
                case 1: return (_lastMouseBits & 0x01) != 0;
                case 2: return (_lastMouseBits & 0x02) != 0;
                case 4: return (_lastMouseBits & 0x04) != 0;
                case 5: return (_lastMouseBits & 0x08) != 0;
                case 6: return (_lastMouseBits & 0x10) != 0;
            }
            return IsVkLit(vk);
        }

        // 从最近一帧快照读 VK 位（ExtraKeys[vk>>3] 的第 vk&7 位）
        private bool IsVkLit(int vk)
        {
            var ex = _lastExtraKeys;
            if (ex == null || vk < 0 || vk > 255) return false;
            return ((ex[vk >> 3] >> (vk & 7)) & 1) != 0;
        }
        // ===================== 0.9.5：布局快照（设置窗口保存布局预设时取真实值）=====================
        // 背景：设置窗口进程读 LocalSettings 的视图会滞后（曾出现"重置布局 removed 0 keys"），
        // 于是"保存布局预设"保存到的可能是小组件里的旧值 → 预设与实际不一致。
        // 解决：设置窗口写 LayoutSnapshotRequest_ 请求，本进程把**自己内存里的真实布局**写成
        // LocalFolder\layout-snapshot.json（文件不走 LocalSettings 缓存），设置窗口读取该文件。
        private long _lastLayoutSnapshotReq;

        // 把当前真实布局序列化为快照 JSON（字段与布局预设的 data 一致）
        private void WriteLayoutSnapshot(long stamp)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("{\"stamp\":").Append(stamp);
                sb.Append(",\"keyOpacity\":").Append((int)Math.Round(_keyOpacity));
                sb.Append(",\"padVisible\":").Append(_padVisible ? "true" : "false");
                sb.Append(",\"padW\":").Append(MousePad.Width.ToString("0.####", CultureInfo.InvariantCulture));
                sb.Append(",\"padH\":").Append(MousePad.Height.ToString("0.####", CultureInfo.InvariantCulture));
                double ptx, pty; GetTransformXY(MousePad, out ptx, out pty);
                sb.Append(",\"padPosX\":").Append(ptx.ToString("0.####", CultureInfo.InvariantCulture));
                sb.Append(",\"padPosY\":").Append(pty.ToString("0.####", CultureInfo.InvariantCulture));

                sb.Append(",\"keys\":{");
                bool first = true;
                foreach (var kv in _keys) AppendLiveKeyJson(sb, ref first, kv.Key, kv.Value);
                foreach (var kv in _mouse) AppendLiveKeyJson(sb, ref first, kv.Key, kv.Value);
                sb.Append("}");

                sb.Append(",\"customKeys\":{");
                first = true;
                foreach (var kv in _customKeys)
                {
                    double tx, ty; GetTransformXY(kv.Value, out tx, out ty);
                    string pos = tx.ToString("0.####", CultureInfo.InvariantCulture) + ";" + ty.ToString("0.####", CultureInfo.InvariantCulture);
                    string size = ((int)Math.Round(kv.Value.Width)) + ";" + ((int)Math.Round(kv.Value.Height));
                    var tb = kv.Value.Child as TextBlock;
                    string disp = tb != null ? tb.Text : kv.Key;
                    // 1.3：手柄部件此刻印的字是随风格/电量/扳机值变的"活文本"（摇杆等非文字部件还会回落到键名），
                    // 不是用户改的名字——按 GpIsLiveLabel 判定，活文本不写进预设（否则下次应用预设会把字形钉死）
                    if (IsGpPartName(kv.Key) && GpIsLiveLabel(kv.Key, GpPartKind(kv.Key), disp)) disp = null;
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("\"").Append(EscapeJsonStr(kv.Key)).Append("\":{\"pos\":\"").Append(pos)
                      .Append("\",\"size\":\"").Append(size).Append("\"");
                    if (!string.IsNullOrEmpty(disp)) sb.Append(",\"displayName\":\"").Append(EscapeJsonStr(disp)).Append("\"");
                    sb.Append("}");
                }
                sb.Append("}");

                sb.Append(",\"deletedKeys\":[");
                bool firstDel = true;
                var vs = ApplicationData.Current.LocalSettings.Values;
                foreach (var kv in vs)
                {
                    if (!kv.Key.StartsWith("Deleted_", StringComparison.Ordinal)) continue;
                    if (!firstDel) sb.Append(",");
                    firstDel = false;
                    sb.Append("\"").Append(EscapeJsonStr(kv.Key.Substring("Deleted_".Length))).Append("\"");
                }
                sb.Append("]}");

                string path = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "layout-snapshot.json");
                System.IO.File.WriteAllText(path, sb.ToString());
                DiagLog("layout snapshot written: stamp=" + stamp + " bytes=" + sb.Length);
            }
            catch (Exception ex) { DiagLog("layout snapshot fail: " + ex.Message); }
        }

        private void AppendLiveKeyJson(System.Text.StringBuilder sb, ref bool first, string name, Border b)
        {
            if (b == null) return;
            if (b.Visibility == Visibility.Collapsed) return;   // 已删除/隐藏的默认键不进布局
            double tx, ty; GetTransformXY(b, out tx, out ty);
            if (!first) sb.Append(",");
            first = false;
            sb.Append("\"Layout_").Append(EscapeJsonStr(name)).Append("\":\"")
              .Append(((int)Math.Round(b.Width))).Append(";").Append(((int)Math.Round(b.Height))).Append(";")
              .Append(tx.ToString("0.####", CultureInfo.InvariantCulture)).Append(";")
              .Append(ty.ToString("0.####", CultureInfo.InvariantCulture)).Append("\"");
        }

        private static string EscapeJsonStr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
        private string CustomKeysFingerprint()
        {
            var v = ApplicationData.Current.LocalSettings.Values;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in v)
            {
                // 0.9.5 修复：9 个主题色键（CustomPanel_/CustomBorder_/…）虽然也是 Custom_ 前缀，
                // 但它们只是颜色、不是键区结构。原来它们被算进指纹，导致每改一次颜色（拖调色盘时
                // 每 80ms 落盘一次）主小组件就判定"结构变化"→ 删掉所有自定义键 + 重建默认键 + 恢复布局，
                // 表现为按键区闪烁、卡顿、拖动被打断。颜色变化走下面的 RefreshCustomBrushes()+ApplyTheme() 即可。
                if (Array.IndexOf(CustomKeys, kv.Key) >= 0) continue;
                if (kv.Key.StartsWith("Custom_", StringComparison.Ordinal) ||
                    kv.Key.StartsWith("Layout_", StringComparison.Ordinal) ||
                    kv.Key.StartsWith("Deleted_", StringComparison.Ordinal))
                    sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
            }
            return sb.ToString();
        }

        private void ReloadSettingsFromStore()
        {
            var v = ApplicationData.Current.LocalSettings.Values;

            // 0) 0.9.5：处理「重置按键布局」请求（设置窗口只写标记键，实际清理与重建在这里执行）
            try
            {
                long req = ParseLongOr(v[LayoutResetRequestKey], 0);
                if (req != 0 && req != _lastLayoutResetReq)
                {
                    _lastLayoutResetReq = req;
                    int cleared = ResetLayoutToBuiltInDefault(v);
                    _customKeysFingerprint = "FORCE";   // 让下面的结构变化分支必定重建键区
                    DiagLog("layout reset: cleared " + cleared + " keys -> rebuild");
                }
            }
            catch (Exception ex) { DiagLog("layout reset fail: " + ex.Message); }

            // 0.9.5：布局快照请求（设置窗口保存布局预设时用）
            try
            {
                long sreq = ParseLongOr(v["LayoutSnapshotRequest_"], 0);
                if (sreq != 0 && sreq != _lastLayoutSnapshotReq)
                {
                    _lastLayoutSnapshotReq = sreq;
                    WriteLayoutSnapshot(sreq);
                }
            }
            catch (Exception ex) { DiagLog("layout snapshot req fail: " + ex.Message); }

            // 1) 键区结构变化（自定义键增删 / 布局 / 删除记录）→ 全量重建
            string fp = CustomKeysFingerprint();
            if (_customKeysFingerprint.Length > 0 && fp != _customKeysFingerprint)
            {
                DiagLog("settings sync: structure changed -> rebuild keys");
                try
                {
                    // 移除现有自定义键 UI 后重建（与启动恢复同路径）
                    var dead = new System.Collections.Generic.List<string>();
                    foreach (var kv in _customKeys) dead.Add(kv.Key);
                    foreach (var nm in dead)
                    {
                        Border cb;
                        if (_customKeys.TryGetValue(nm, out cb))
                        {
                            _customKeys.Remove(nm);
                            CustomKeysPanel.Children.Remove(cb);
                        }
                    }
                    if (_customKeys.Count == 0) CustomKeysPanel.Visibility = Visibility.Collapsed;
                    RegisterDefaultKeys();
                    // 0.9.6 修复：被删除过的内置键在重建时必须先恢复可见。
                    // 原来只有 ResetOneKeyToDefault 会写 Visibility.Visible，而被删的键此刻已不在 _keys/_mouse 里，
                    // 于是"删除某键 → 重置布局/应用不含它的预设"后该键永远不显示（字典里有、渲染循环还在 SetKey、
                    // 还进吸附候选），要重启一次才冒出来。这里先全部恢复，紧接着的 RestoreDeletions()
                    // 会按 Deleted_ 标记把仍然删除的键重新折叠——顺序不能反。
                    foreach (var kv in _keys) { try { kv.Value.Visibility = Visibility.Visible; } catch { } }
                    foreach (var kv in _mouse) { try { kv.Value.Visibility = Visibility.Visible; } catch { } }
                    RestoreLayout();
                    RestoreDeletions();
                    RestorePadVisibility();
                    RestoreCustomKeys();   // 内部会重建 + 应用字号字重 + 左缘补偿
                }
                catch (Exception ex) { DiagLog("rebuild keys fail: " + ex.Message); }
            }
            _customKeysFingerprint = fp;

            // 0.9.6 修复：鼠标垫自定义（PadCustom_/PadW/PadH/PadPos_*）此前只在启动时恢复一次，
            // 设置窗口「应用」带 padW/padH 的布局预设后垫子当场不变、要重启才跳到预设尺寸。
            // 这里每次重载都按存储值同步一次（未自定义时函数内部会立即返回，不影响"跟随屏幕比例"的自动路径）。
            RestorePadCustom();

            // 2) 外观设置
            string theme = (v["Theme"] as string) ?? "dark";
            if (theme != _theme)
            {
                _theme = theme;
                RefreshCustomBrushes();
            }
            else if (_theme == "custom")
            {
                RefreshCustomBrushes();
            }
            RestoreKeyFontSettings();
            ApplyKeyFont();   // 0.9.5 修复：重载设置后必须"应用"字号/字重/字体族，
                              // 原来只做了恢复设置值（RestoreKeyFontSettings 不同步到按键，
                              // 导致设置窗口换字体后要重新打开小组件才生效）
            _keyOpacity = ParseDoubleOr(v["KeyOpacity_"], _keyOpacity);
            _mouseSpeed = ParseDoubleOr(v["MouseSpeed_"], _mouseSpeed);
            if (_mouseSpeed < 0.5) _mouseSpeed = 0.5;
            if (_mouseSpeed > 4.0) _mouseSpeed = 4.0;
            _dotKeyVk = (int)ParseDoubleOr(v["MouseDotKeyVk_"], _dotKeyVk);
            _dotKeyOn = !(v["MouseDotKeyOn_"] != null && v["MouseDotKeyOn_"].ToString() == "0") && _dotKeyVk != 0;
            _dotSize = ParseDoubleOr(v["DotSize_"], _dotSize);
            if (_dotSize < 4) _dotSize = 4;
            if (_dotSize > 30) _dotSize = 30;
            try { MouseDot.Width = _dotSize; MouseDot.Height = _dotSize; } catch { }
            _keyScaleUser = ParseDoubleOr(v["KeyScale_"], 0);
            if (_keyScaleUser < -100) _keyScaleUser = -100;
            if (_keyScaleUser > 100) _keyScaleUser = 100;
            FitLayoutToWindow();   // 整体大小变化后立即重算键区缩放
            _panelTransparent = !(v["PanelTransparent_"] != null && v["PanelTransparent_"].ToString() == "0");
            DiagLog("panel transparent = " + _panelTransparent);
            // 1.2：手柄按键显示的 7 个设置（键名/类型/默认值见下面 ApplyGamepadSettings 前的契约注释）：
            // 冷启动与设置窗口 SignalDataChanged 都走这里；默认 = 自动模式 + 无手柄 → 整组不显示。
            ReloadGamepadSettings();
            ApplyGamepadSettings();
            try
            {
                _layoutLocked = (v["LayoutLocked"] is bool lb) ? lb : true;
                _padVisible = !(v["PadVisible_"] != null && v["PadVisible_"].ToString() == "0");
                MousePad.Visibility = _padVisible ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
            ApplyTheme();
            ApplyKeyOpacity();
            ApplySettingsColors();
            // 0.9.6：日志里带上新增设置的实际取值，便于确认"关闭重开后是否恢复"
            DiagLog("settings reloaded from store: theme=" + _theme + " size=" + _keyFontSize + " weight=" + _keyFontWeightLevel
                    + " keyScale=" + _keyScaleUser + " dotSize=" + _dotSize + " mouseSpeed=" + _mouseSpeed
                    + " dotKey=" + (_dotKeyOn ? _dotKeyVk : 0) + " transparent=" + _panelTransparent + " opacity=" + _keyOpacity);
        }

        private static double ParseDoubleOr(object o, double def)
        {
            if (o == null) return def;
            double d;
            return double.TryParse(o.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : def;
        }



        // 0.8.2 菜单定向控制：隐藏鼠标垫（仅切 Visibility + 持久化，位置/尺寸/transform 保留）
        private void HidePad()
        {
            _padVisible = false;
            MousePad.Visibility = Visibility.Collapsed;
            ApplicationData.Current.LocalSettings.Values["PadVisible_"] = 0;
            DiagLog("pad hidden by menu");
        }

        // 0.8.2 菜单定向控制：显示鼠标垫（恢复 Visibility + 持久化）
        private void ShowPad()
        {
            _padVisible = true;
            MousePad.Visibility = Visibility.Visible;
            ApplicationData.Current.LocalSettings.Values["PadVisible_"] = 1;
            DiagLog("pad shown by menu");
        }


        // 0.8.2 一次性迁移：1.6.0.0 版的改名宽度自适应曾把自定义键尺寸改写为 CustomKeyWidth(显示名)
        // （污染 CustomSize_）。启动时检测污染特征并恢复为内置默认布局中的 Tab 尺寸 56;48（0.8.2 新默认，
        // 见 BuiltInDefaultLayoutJson；此前误恢复 74;48 已废弃）。须在 RestoreCustomKeys（AddCustomKey 读取 CustomSize_）之前调用。
        private void MigrateTabSize()
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                string size = v["CustomSize_Tab"] as string;
                if (string.IsNullOrEmpty(size)) return;
                string repaired = RepairTabSize(size, v["DisplayName_Tab"] as string);
                if (repaired != size)
                {
                    v["CustomSize_Tab"] = repaired;
                    DiagLog("migrate: CustomSize_Tab restored to " + repaired + " (was " + size + ")");
                }
            }
            catch { }
        }

        // 0.8.2 Tab 尺寸污染检测：1.6.0.0 改名宽度自适应会把 Tab 尺寸写成 CustomKeyWidth(显示名) 的公式值
        // （52/68/96 等）；此外历史版本可能残留 68;48（v1 污染）与 74;48（0.8.2 早期误恢复值）。
        // 检测到任一特征 → 恢复为内置默认布局的 Tab 尺寸 56;48。
        // 在启动迁移 / 预设保存 / 预设应用三处统一使用，杜绝污染值再次进入预设文件与本地持久化。
        private static string RepairTabSize(string size, string disp)
        {
            if (string.IsNullOrEmpty(size)) return size;
            if (size == "68;48" || size == "74;48") return "56;48";
            return size;
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
            // 自定义键尺寸（0.7.1）：CustomSize_<名>（w;h）持久化，恢复精确尺寸（默认按名称宽度计算）
            string csize = ApplicationData.Current.LocalSettings.Values["CustomSize_" + name] as string;
            // 1.3：手柄部件（Gp_*）用部件专属默认尺寸；其余自定义键沿用按名字宽度计算
            double cw, ch;
            if (IsGpPartName(name)) { GpDefaultSize(name, out cw, out ch); }
            else { cw = CustomKeyWidth(name); ch = 48; }
            if (!string.IsNullOrEmpty(csize))
            {
                try
                {
                    var sp = csize.Split(';');
                    if (sp.Length == 2)
                    {
                        double w0 = double.Parse(sp[0], CultureInfo.InvariantCulture);
                        double h0 = double.Parse(sp[1], CultureInfo.InvariantCulture);
                        if (w0 >= 10 && w0 <= 2000 && h0 >= 10 && h0 <= 2000) { cw = w0; ch = h0; }
                    }
                }
                catch { }
            }
            var border = new Border
            {
                Width = cw,
                Height = ch,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0),   // 0.7.1 Canvas 自由布局：间距走坐标/transform，不再用 Margin 参与布局
                Tag = name
            };
            border.Child = new TextBlock
            {
                // 1.3：手柄部件印的是部件字形（随 GamepadStyle_ 变，渲染循环每帧校正）；其余自定义键用既有显示名
                Text = IsGpPartName(name) ? GpPartDefaultLabel(name) : KeyDisplayName(name),   // 显示名（0.8.1）：DisplayName_<名> 持久化，无则默认（空格键显示「空格」）
                FontSize = _keyFontSize,       // 0.9.4：跟随"字体大小"设置（原硬编码 18 导致粘贴键与默认键字号不一致）
                FontWeight = _keyFontWeight,   // 0.9.4：跟随"字体粗细"设置
                FontFamily = CurrentFontFamily(),   // 0.9.5 修复：此前漏了字体族 → 复制粘贴/新增出来的键字体与原有键不一致
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _customKeys[name] = border;
            CustomKeysPanel.Children.Add(border);
            Canvas.SetLeft(border, 0);   // 0.7.1 Canvas 自由布局：自定义键定位 = 面板相对 (0,0)（面板位于键区 (0,224)）+ 位置 transform
            Canvas.SetTop(border, 0);
            CustomKeysPanel.Visibility = Visibility.Visible;
            AttachResize(border);       // 复用拖拽缩放/hover/锁定/长按移动机制
            SetKey(border, false);      // 初始主题样式
            ApplyKeyFontTo(border);     // 0.9.5：兜底再应用一次字号/字重/字体族，确保与现有键完全一致
            // 1.3：手柄部件按类型建立内容（扳机/摇杆用 Canvas 承载填充与点，圆环/方块用 Border 自身，文字类沿用 TextBlock）。
            // 初始一律 Collapsed：显隐交给 ApplyGamepadCustomParts 按 GamepadLayout_ / GamepadMode_ 决定。
            if (IsGpPartName(name))
            {
                GpBuildPartContent(border, name);
                border.Visibility = Visibility.Collapsed;
            }
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
                // 0.7.1 无已存位置：默认排布到已有自定义键右侧（自动避让），避免全部叠在起点
                double ax = 0;
                foreach (var kv in _customKeys)
                {
                    if (kv.Value == border) continue;
                    var tt = kv.Value.RenderTransform as TranslateTransform;
                    double tx = tt != null ? tt.X : 0;
                    ax = Math.Max(ax, tx + kv.Value.Width);
                }
                SetTransformXY(border, ax + 6, 0);
                ApplicationData.Current.LocalSettings.Values["CustomPos_" + name] =
                    (ax + 6).ToString(CultureInfo.InvariantCulture) + ";0";
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
                // 0.8.3：先收集再创建——枚举期间 AddCustomKey（可能向同一集合新增 CustomPos_ 等键）
                // 会触发 IPropertySet "集合已修改" 异常导致剩余自定义键静默丢失
                var names = new List<string>();
                foreach (var kv in values)
                {
                    if (kv.Key.StartsWith("Custom_", StringComparison.Ordinal))
                    {
                        string name = kv.Key.Substring("Custom_".Length);
                        if (!string.IsNullOrEmpty(name)) names.Add(name);
                    }
                }
                foreach (var name in names) AddCustomKey(name);
            }
            catch
            {
            }
            OffsetKeyLayerForNegativeKeys();   // 0.8.3：键全部重建后重算左缘补偿（布局/预设/重置共用此路径）
            ApplyKeyFont();                    // 0.9.4：统一应用字号/字重（含新建与粘贴的键）
        }

        // 0.8.3：负坐标键修复——用户把键放到左外边（Tab/Shift/Ctrl 的 transform 为负）时，
        // 键的左半部分超出键区可视左缘被窗口/画布裁掉（"左半边显示不全"）。
        // 遍历全部键取最小视觉左边界，若越过 RootPanel 左 padding，把整个键区右移补偿，
        // 保证所有键完整可见（键间相对位置不变、不持久化、窗口宽度足够时恢复原视觉）。
        private void OffsetKeyLayerForNegativeKeys()
        {
            try
            {
                double minLeft = double.MaxValue;
                Action<Border> scan = (b) =>
                {
                    if (b == null) return;
                    double cl = Canvas.GetLeft(b);
                    var tt = b.RenderTransform as TranslateTransform;
                    double tx = tt != null ? tt.X : 0;
                    double left = cl + tx;
                    if (left < minLeft) minLeft = left;
                };
                foreach (var kv in _keys) scan(kv.Value);
                foreach (var kv in _mouse) scan(kv.Value);
                foreach (var kv in _customKeys) scan(kv.Value);
                if (minLeft == double.MaxValue) return;
                double pad = 16.0;   // RootPanel Padding 左缘
                double offset = pad - minLeft;   // 右移量：最左键贴回 padding 边缘
                if (offset < 0.5) offset = 0;
                else if (offset > 300) offset = 300;   // 异常布局保护（不无限偏）
                KeyLayer.Margin = new Thickness(offset, 0, 0, 0);
                if (offset > 0.5) DiagLog("keylayer offset right " + (int)offset + " px (most-left key at " + minLeft + ")");
                FitLayoutToWindow();   // 0.9.4：布局变化后重算键区缩放（键增减/预设应用/重置共用此路径）
            }
            catch
            {
            }
        }

        // 键名 → VK 虚拟键码：字母=ASCII 大写，数字=0x30-0x39，F1-F12=0x70-0x7B，符号/编辑/方向键查表
        private static int VkFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            // 1.3：手柄部件（Gp_*）不是虚拟键，永不参与 VK 位图映射（含复制副本 "Gp_A(2)"）
            if (name.StartsWith(GpPartPrefix, StringComparison.Ordinal)) return -1;
            // 0.8.1 粘贴副本命名 "名(n)"：循环剥离序号后缀，映射到基名 VK（"Q(2)" 映射 VK_Q，与原键同时点亮；
            // 副本再复制再粘贴产生 "Q(2)(2)" 等多层后缀，循环剥到基名为止）
            while (name.Length > 3 && name[name.Length - 1] == ')')
            {
                int ip = name.LastIndexOf('(');
                if (ip <= 0) break;
                bool digits = true;
                for (int i = ip + 1; i < name.Length - 1; i++)
                    if (name[i] < '0' || name[i] > '9') { digits = false; break; }
                if (!digits) break;
                name = name.Substring(0, ip);
            }
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
                // 0.9.5：设置窗口「添加按键」键盘使用的键名别名（此前对不上映射表 → VK 取 -1 → 永不点亮）
                case "CapsLock": return 0x14;
                case "Win": return 0x5B;          // 左Win（右Win 见 右Win）
                case "Shift": return 0xA0;        // 左Shift
                case "Ctrl": return 0xA2;         // 左Ctrl
                case "Alt": return 0xA4;          // 左Alt
                case "Insert": return 0x2D;
                case "Delete": return 0x2E;
                case "Up": return 0x26;
                case "Down": return 0x28;
                case "Left": return 0x25;
                case "Right": return 0x27;
                case "\u2191": return 0x26;       // ↑
                case "\u2193": return 0x28;       // ↓
                case "\u2190": return 0x25;       // ←
                case "\u2192": return 0x27;       // →
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
                case "\u5de6\u952e": return 0x01;   // 左键 (VK_LBUTTON)
                case "\u53f3\u952e": return 0x02;   // 右键 (VK_RBUTTON)
                case "\u4e2d\u952e": return 0x04;   // 中键 (VK_MBUTTON)
                case "\u4fa7\u4e0a": return 0x05;   // 侧上 (VK_XBUTTON1)
                case "\u4fa7\u4e0b": return 0x06;   // 侧下 (VK_XBUTTON2)
                case "\u6eda\u8f6e\u4e0a": return 0x07;   // 滚轮上
                case "\u6eda\u8f6e\u4e0b": return 0x08;   // 滚轮下
            }
            return -1;   // 未识别键名（如"触摸板"）→ 越界，渲染循环视为未按下，绝不抛异常
        }

        // 点击锁定菜单框内部：标记已处理，避免冒泡到遮罩触发关闭
        private void LockMenu_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }


        // 删除确认框：确认删除（0.9.4：多选模式下走批量删除分支）
        private void DeleteConfirmYes_Click(object sender, TappedRoutedEventArgs e)
        {
            DeleteConfirmPanel.Visibility = Visibility.Collapsed;
            if (_multiDeletePending)
            {
                _deleteConfirmKey = null;
                MultiDeleteApply();
                e.Handled = true;
                return;
            }
            if (_deleteConfirmKey != null) ConfirmDeleteKey(_deleteConfirmKey);
            _deleteConfirmKey = null;
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



        // 应用按键透明度：恒按滑条设定值（0.8.3 改回：锁定开关不再影响透明度——
// 原来解锁（编辑布局）时强制 100% 便于看清，用户要求保持滑条设定值不变）。
// 关键：始终从 _keyOpacity 设定值计算，绝不从当前 Opacity 推导。
// 被删内置键不在字典，foreach 天然跳过；菜单/关于面板/参考线不设 Opacity
        private void ApplyKeyOpacity()
        {
            double target = _keyOpacity / 100.0;
            foreach (var kv in _keys) kv.Value.Opacity = target;
            foreach (var kv in _mouse) kv.Value.Opacity = target;
            foreach (var kv in _customKeys) kv.Value.Opacity = target;
            if (MousePad != null) MousePad.Opacity = target;
        }



        // 启动恢复：读持久化的字号/字重并同步滑条（值非法时保持默认 18 / SemiBold）
        private void RestoreKeyFontSettings()
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                object fs = v["KeyFontSize_"];
                if (fs != null)
                {
                    double d;
                    if (double.TryParse(fs.ToString(), out d) && d >= 8 && d <= 30) _keyFontSize = d;
                }
                object fw = v["KeyFontWeight_"];
                bool newScale = v[FontWeightScaleKey] != null;   // 0.9.5：是否已是新刻度(1..10)
                if (fw != null)
                {
                    int lv;
                    if (int.TryParse(fw.ToString(), out lv) && lv >= 1 && lv <= 10)
                        _keyFontWeightLevel = MigrateFontWeightLevel(lv, newScale);
                }
                if (!newScale)
                {
                    // 首次升级：把旧刻度(1..5)迁移成新刻度(1..10)并落盘，保证视觉不变
                    v[FontWeightScaleKey] = 1;
                    v["KeyFontWeight_"] = _keyFontWeightLevel;
                    DiagLog("font weight scale migrated -> " + _keyFontWeightLevel);
                }
                _keyFontWeight = FontWeightFromLevel(_keyFontWeightLevel);
                object ft = v["KeyFontTag_"];
                if (ft != null && !string.IsNullOrEmpty(ft.ToString())) _keyFontTag = ft.ToString();
                DiagLog("key font restored: size=" + _keyFontSize + " weight=" + _keyFontWeightLevel + " tag=" + _keyFontTag);
            }
            catch (Exception ex) { DiagLog("restore key font fail: " + ex.Message); }
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


        // 当前主题预设的第 k 槽颜色（dark/gray/light/pink/blue 从现有画刷字段取，与五态主题一致）
        private Color PresetColor(int k)
        {
            Brush[] d = { _darkPanel, _darkBorder, _darkDefaultBg, _darkDefaultFg, _darkPressedBg, _darkPressedFg, _darkPad, _darkDot, _darkAccent, _darkDotPressed };
            Brush[] g = { _grayPanel, _grayBorder, _grayKeyBg, _grayKeyFg, _grayPressedBg, _grayPressedFg, _grayPad, _grayDot, _grayAccent, _grayDotPressed };
            Brush[] l = { _lightPanel, _lightBorder, _lightDefaultBg, _lightDefaultFg, _lightPressedBg, _lightPressedFg, _lightPad, _darkDefaultBg, _lightAccent, _grayDotPressed };
            Brush[] p = { _pinkPanel, _pinkBorder, _pinkKeyBg, _pinkKeyFg, _pinkPressedBg, _pinkPressedFg, _pinkPad, _pinkDot, _pinkAccent, _pinkDotPressed };
            Brush[] b = { _bluePanel, _blueBorder, _blueKeyBg, _blueKeyFg, _bluePressedBg, _bluePressedFg, _bluePad, _blueDot, _blueAccent, _blueDotPressed };
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


        // 用 9 个 Custom_ 键刷新动态画刷（缺省回落 dark 预设，GetSlotDisplayColor 已处理回落逻辑）
        private void RefreshCustomBrushes()
        {
            for (int k = 0; k < CustomKeys.Length; k++)
            {
                _customBrushes[k].Color = GetSlotDisplayColor(k);
            }
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
            BeginGroupMoveIfNeeded(b);   // 0.9.4：多选集合内长按拖动 = 整组移动（未选中键仍为单键移动）
            DiagLog("move mode: " + (b.Tag ?? "?"));
        }

        // 0.9.4：移动落位的位置持久化（单键与整组移动共用）
        // 鼠标垫 → Pad 持久化；自定义键 → CustomPos_；默认键/鼠标键 → Layout_<名>
        private void PersistKeyPosition(Border key)
        {
            if (key == null) return;
            string nm = key.Tag as string;
            if (nm == null) nm = NameOf(key);   // 默认键（KeyQ..Space/鼠标键）无 Tag
            if (string.IsNullOrEmpty(nm)) return;
            try
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
                    SaveKeyLayout(nm, key);   // 默认键走 Layout_ 持久化（位置=transform）
                }
                var tt2 = key.RenderTransform as TranslateTransform;
                DiagLog("key moved " + nm + " tx=" + (int)(tt2 != null ? tt2.X : 0) + " ty=" + (int)(tt2 != null ? tt2.Y : 0));
            }
            catch (Exception ex) { DiagLog("persist position fail " + nm + ": " + ex.Message); }
        }

        // 确认删除自定义键：移除字典/面板/LocalSettings/CustomPos，并清理状态
        // 确认删除键：自定义键（移除字典/面板/持久化）或内置键（字典移除/Collapsed/清 Layout_ 并记录 Deleted_），并清理状态
        private void ConfirmDeleteKey(Border b)
        {
            string name = NameOf(b);
            if (string.IsNullOrEmpty(name) || name == "?" || name == "Pad") return;
            // 0.8.1 防同名误删：默认键与自定义键可同名（配列选择器可添加自定义 Q），分支须按 Border 实例身份判定，
            // 不能只查字典 ContainsKey（否则右键默认 Q 会误删自定义 Q）
            Border cb;
            bool isCustom = _customKeys.TryGetValue(name, out cb) && ReferenceEquals(cb, b);
            if (isCustom)
            {
                // 自定义键：移除面板 + 清 Custom_/CustomPos_ 持久化
                _customKeys.Remove(name);
                CustomKeysPanel.Children.Remove(b);
                ApplicationData.Current.LocalSettings.Values.Remove("Custom_" + name);
                ApplicationData.Current.LocalSettings.Values.Remove("CustomPos_" + name);
                ApplicationData.Current.LocalSettings.Values.Remove("CustomSize_" + name);
                // 1.3：手柄部件额外留下删除记录（与内置键的 Deleted_ 约定一致；设置窗口/AddGamepadPart
                // 重新添加时会一并清掉该标记）
                if (IsGpPartName(name)) ApplicationData.Current.LocalSettings.Values["Deleted_" + name] = 1;
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
                    _cursorRef = def;
                    cw.PointerCursor = _cursorRef;
                }
                else
                {
                    // 持有强引用：Game Bar 合成环境下 CoreCursor 被 GC 回收会导致光标消失（0.8.2 修复）
                    _cursorRef = new CoreCursor(target.Value, 0);
                    cw.PointerCursor = _cursorRef;
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
            // 0.9.4：多选选中的键保持红框，悬停不覆盖
            if (!IsKeySelected(b))
                b.BorderBrush = _theme == "dark" ? _darkDefaultFg : _darkDefaultBg;   // 深色主题白高亮，浅色主题黑高亮
            ApplyCursor(mode);   // 鼠标垫边缘同样显示拉伸样式（0.8.2 恢复；消失问题由 ApplyCursor 持有引用修复）   // 0.8.2 鼠标垫不设自定义光标（Game Bar 宿主下自定义光标渲染异常→消失）
        }

        private void ClearHover()
        {
            if (_hoverKey == null) return;
            // 0.9.4：选中键恢复为红框，其余恢复主题边框色
            if (IsKeySelected(_hoverKey))
            {
                _hoverKey.BorderBrush = MultiSelectBrush;
                _hoverKey.BorderThickness = new Thickness(2);
            }
            else
            {
                _hoverKey.BorderBrush = BorderB();
            }
            ApplyCursor(null);
            _hoverKey = null;
            _hoverMode = null;
        }

        // 按下：右键→删除确认（仅自定义键）；非右键→启动长按计时 + 边缘缩放判定
        private void Key_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var b = sender as Border;
            if (b == null) return;
            // 右键：按键→按键菜单（删除/复制/修改显示名）；鼠标垫→鼠标垫菜单（隐藏鼠标垫）；0.8.2
            if (e.GetCurrentPoint(b).Properties.IsRightButtonPressed)
            {
                CancelLongPress();
                if (_dragKey != null || _moveKey != null)
                {
                    e.Handled = true;
                    return;
                }
                string nm = NameOf(b);
                if (nm == "Pad")
                {
                    ShowPadContextMenu(e.GetCurrentPoint(KeyLayer).Position);
                }
                else if (nm != "?")
                {
                    ShowKeyContextMenu(b, e.GetCurrentPoint(KeyLayer).Position);
                }
                e.Handled = true;
                return;
            }
            _pressPointerRoot = e.GetCurrentPoint(null).Position;
            // 0.9.4 多选模式：按下即记录"点击候补"（**任何位置，含边缘**——键很小，
            // 原实现排除边缘 8px 会让大量点击落空，表现为"要多点几下才选上"）。
            // 判定交给抬起时的几何比较（位置+尺寸都没变 = 点击；变了 = 移动/缩放），
            // 因此这里**不能**提前 return，缩放/移动逻辑照常走。
            if (_multiSelectMode && b != MousePad)
            {
                BeginMultiSelectTapCandidate(b);
            }
            StartLongPress(b);
            // 捕获指针：保证长按移动模式中指针移出按键仍持续收到 PointerMoved/Released
            try { b.CapturePointer(e.Pointer); } catch { }
            // 双保险：键上的左键按下统一标记已处理，绝不允许冒泡到 KeyLayer
            // （KeyLayer 的左键分支是"点空白退出多选"，冒泡过去会造成误退出）
            e.Handled = true;
            if (_layoutLocked) return;
            string mode = HitTestEdge(b, e.GetCurrentPoint(b).Position);
            if (mode == null) return;
            ApplyCursor(mode);   // 0.8.2 鼠标垫边缘同样设置拉伸样式（恢复）；消失问题由 ApplyCursor 持有引用修复
            _dragKey = b;
            _dragMode = mode;
            _dragStartX = _pressPointerRoot.X;
            _dragStartY = _pressPointerRoot.Y;
            _dragStartW = b.Width;
            _dragStartH = b.Height;
            _dragStartML = b.Margin.Left;
            _dragStartMT = b.Margin.Top;
            var tt0 = b.RenderTransform as TranslateTransform;
            _dragStartTx = tt0 != null ? tt0.X : 0;
            _dragStartTy = tt0 != null ? tt0.Y : 0;
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
        // 0.9.6 修复：当前键区图层缩放（窗口自适应 × 整体按键大小）。
        // 指针增量是屏幕像素，而按键的 Width/Height/Margin/TranslateTransform 都在 KeyLayer 图层单位里，
        // 而 KeyLayer 上挂着 _keyScale 缩放。把屏幕增量直接当图层增量会让拖拽/缩放速度乘上缩放倍数
        // （整体按键大小调到非 0 时尤其明显），吸附用的 SnapCanvas 视觉边也会算错。
        // scale = 1（默认）时乘除都是 1，行为与修复前完全一致。
        private double CurrentLayerScale()
        {
            try
            {
                double s = _keyScale != null ? _keyScale.ScaleX : 1.0;
                if (s > 0.01 && !double.IsNaN(s) && !double.IsInfinity(s)) return s;
            }
            catch { }
            return 1.0;
        }

        private void Key_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var b = sender as Border;
            if (b == null) return;
            double layerScale = CurrentLayerScale();   // 图层缩放：屏幕像素增量 ↔ 图层单位的换算系数
            if (_moveKey != null)
            {
                var key = _moveKey;
                if (b != key) return;
                double dx = (e.GetCurrentPoint(null).Position.X - _moveStartX) / layerScale;
                double dy = (e.GetCurrentPoint(null).Position.Y - _moveStartY) / layerScale;
                // 位置用 TranslateTransform 渲染变换表达（不写 Margin，避免 StackPanel 流式布局挤压兄弟元素）
                double tx = _moveStartTX + dx;
                double ty = _moveStartTY + dy;
                double w = (key.ActualWidth > 0 ? key.ActualWidth : key.Width);
                double h = (key.ActualHeight > 0 ? key.ActualHeight : key.Height);
                // 被拖按键当前四边（SnapCanvas 坐标）：起点视觉基准 + transform 位移增量换算成视觉量（免布局刷新）
                double[] ea = new double[4];
                ea[0] = _moveBaseLeft + (tx - _moveStartTX) * layerScale;
                ea[1] = ea[0] + w * layerScale;
                ea[2] = _moveBaseTop + (ty - _moveStartTY) * layerScale;
                ea[3] = ea[2] + h * layerScale;
                var rects = CollectOtherRects(key);
                var hitH = ComputeAxisSnap(true, ea, rects);
                var hitV = ComputeAxisSnap(false, ea, rects);
                // 滞回：未吸附 ≤8 触发、已吸附 ≤10 保持、>10 脱离；两轴独立，只修正吸附到的轴
                bool snapH = hitH.Active && ShouldSnap(hitH.Delta, ref _snapActiveH);
                bool snapV = hitV.Active && ShouldSnap(hitV.Delta, ref _snapActiveV);
                // 吸附修正量是 SnapCanvas 视觉像素，写回 transform 前要换算回图层单位
                if (snapH) tx += hitH.Delta / layerScale;
                if (snapV) ty += hitV.Delta / layerScale;
                key.RenderTransform = new TranslateTransform { X = tx, Y = ty };
                // 0.9.4：整组移动 —— 把被拖键的最终位移（含吸附修正）同步给组内其他键
                ApplyGroupMove(tx - _moveStartTX, ty - _moveStartTY);
                // 参考线分级显示（v2）：吸中=实线、8~40px=半透明虚线、>40px 或无候选=隐藏；每轴最近 1 条
                UpdateSnapLine(0, false, hitH.LinePos, hitH.Active ? Math.Abs(hitH.Delta) : double.MaxValue, snapH);
                UpdateSnapLine(1, true, hitV.LinePos, hitV.Active ? Math.Abs(hitV.Delta) : double.MaxValue, snapV);
                return;
            }
            if (_dragKey != null)
            {
                var key = _dragKey;   // 捕获期间 CaptureLost 可能已把 _dragKey 置空，用局部变量
                if (b != key) return;
                // 0.9.6 修复：屏幕像素增量 → 图层单位（w/h/ml/mt 都是图层单位，见 CurrentLayerScale 注释）
                double dx = (e.GetCurrentPoint(null).Position.X - _dragStartX) / layerScale;
                double dy = (e.GetCurrentPoint(null).Position.Y - _dragStartY) / layerScale;
                double w = _dragStartW, h = _dragStartH, ml = _dragStartML, mt = _dragStartMT;
                if (key == MousePad)
                {
                    // 鼠标垫：任意边/四角拖动都等比例缩放（宽高比 = 起点比例），不做自由缩放；0.9.5 起同样支持参考线吸附（等比保持）。
                    ComputePadEqualScale(ref w, ref h, ref ml, ref mt, dx, dy);
                    // 0.7.1 尺寸上限：鼠标垫同样按窗口可视边界钳制（等比保比例，锚定边补偿按主导轴重算）。
                    // 宽 = RootPanel 可视边界；高 = 面板底边上方（不遮挡面板下边缘）。
                    if (_dragBaseLeft < RootPanel.ActualWidth - 8 && _dragBaseTop < RootPanel.ActualHeight - 16 - 8)
                    {
                        // 0.9.6：视觉坐标与图层单位统一（padL/padT/maxW/maxH 为视觉量，w/h 为图层单位）
                        double padL = _dragBaseLeft + (ml - _dragStartML) * layerScale, padT = _dragBaseTop + (mt - _dragStartMT) * layerScale;
                        double maxW = RootPanel.ActualWidth - 8 - padL, maxH = RootPanel.ActualHeight - 16 - 8 - padT;
                        double f = 1.0;
                        if (w * layerScale > maxW) f = Math.Min(f, maxW / (w * layerScale));
                        if (h * layerScale > maxH) f = Math.Min(f, maxH / (h * layerScale));
                        if (f < 1.0)
                        {
                            // 0.9.6 修复：两轴共用同一个缩放系数（原来各自 Math.Max 兜最小值会把等比拉坏，
                            // 抬起后被持久化，此后光标映射看起来被拉伸）
                            double fMin = Math.Max(MinPadW / Math.Max(1.0, w), MinPadH / Math.Max(1.0, h));
                            if (f < fMin) f = fMin;
                            w *= f;
                            h *= f;
                            bool hasH = _dragMode.Contains("l") || _dragMode.Contains("r");
                            bool hasV = _dragMode.Contains("t") || _dragMode.Contains("b");
                            bool hDom = hasH && (!hasV || (Math.Abs(dx) / _dragStartW) >= (Math.Abs(dy) / _dragStartH));
                            if (hDom)
                            {
                                ml = _dragMode.Contains("l") ? _dragStartML + (_dragStartW - w) : _dragStartML;
                                mt = _dragStartMT;
                            }
                            else
                            {
                                mt = _dragMode.Contains("t") ? _dragStartMT + (_dragStartH - h) : _dragStartMT;
                                ml = _dragStartML;
                            }
                        }
                    }
                    // 0.9.5（用户要求）：鼠标垫等比缩放也做参考线吸附。
                    // 做法：先用与按键同一套 ApplyDragSnap 判定"被调整的边"，取主导轴作为吸附结果，
                    // 另一轴按起点比例重算，从而在吸附的同时保持等比；吸附到的最小尺寸不得小于 MinPadW/MinPadH。
                    {
                        double sw = w, sh = h, sml = ml, smt = mt;
                        ApplyDragSnap(key, ref sw, ref sh, ref sml, ref smt);
                        double dw = Math.Abs(sw - w), dh = Math.Abs(sh - h);
                        // 0.9.5：比例缩放下"另一条边"同样在动（拖右缘 → 下缘也外移；拖下缘 → 右缘也外移），
                        // 主边没吸到就用垂直方向那条边再试一次（临时替换 _dragMode，仅取吸附增量）
                        if (dw <= 0.01 && dh <= 0.01)
                        {
                            string saved = _dragMode;
                            try
                            {
                                bool horiz = _dragMode.Contains("l") || _dragMode.Contains("r");
                                bool vert = _dragMode.Contains("t") || _dragMode.Contains("b");
                                if (horiz && !vert) _dragMode = "b";        // 拖水平边 → 试下缘
                                else if (vert && !horiz) _dragMode = "r";   // 拖垂直边 → 试右缘
                                else _dragMode = null;                      // 边角同时拖：主判定已覆盖两轴
                                if (!string.IsNullOrEmpty(_dragMode))
                                {
                                    double sw2 = w, sh2 = h, sml2 = ml, smt2 = mt;
                                    ApplyDragSnap(key, ref sw2, ref sh2, ref sml2, ref smt2);
                                    if (Math.Abs(sw2 - w) > 0.01 || Math.Abs(sh2 - h) > 0.01)
                                    { sw = sw2; sh = sh2; sml = sml2; smt = smt2; dw = Math.Abs(sw - w); dh = Math.Abs(sh - h); }
                                }
                            }
                            finally { _dragMode = saved; }
                        }
                        if (dw > 0.01 || dh > 0.01)
                        {
                            double rw = _dragStartW > 1 ? _dragStartW : 1, rh = _dragStartH > 1 ? _dragStartH : 1;
                            double nw, nh;
                            if (dw / rw >= dh / rh) { nw = sw; nh = sw * (rh / rw); }
                            else { nh = sh; nw = sh * (rw / rh); }
                            if (nw >= MinPadW && nh >= MinPadH)
                            {
                                w = nw; h = nh;
                                if (_dragMode.Contains("l")) ml = (_dragStartML + _dragStartW) - w;
                                if (_dragMode.Contains("t")) mt = (_dragStartMT + _dragStartH) - h;
                            }
                        }
                    }
                    key.Width = w;
                    key.Height = h;
                    // 缩放补偿走 transform（Margin 保持起点）——避免 StackPanel 流式布局推挤兄弟按键（0.7.1）
                    SetTransformXY(key, _dragStartTx + (ml - _dragStartML), _dragStartTy + (mt - _dragStartMT));
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

                // 0.7.1 尺寸上限：宽度钳制 = 窗口实际可视边界（RootPanel 内容区；窗口固定=撞窗口边框，窗口随内容自适应=可拉很大）；
                // 高度钳制 = 面板底边上方。钳制仅在"锚定边未出界"时生效，
                // 避免把已拖出界的键突然压小；钳制触发时按锚定规则重算 l/t 补偿（保持对边不动）。
                // 窗口可视边界（页面坐标）：宽 = RootPanel.ActualWidth - 8；底 = RootPanel.ActualHeight - 16(Padding 底) - 8
                // 0.9.6：钳制量按图层缩放换算（_dragBaseLeft/_dragBaseTop 是视觉量，_dragStartW/H 与 w/h 是图层单位）
                double clampWv = double.MaxValue, clampHv = double.MaxValue;
                if (_dragMode.Contains("r") && _dragBaseLeft + _dragStartW * layerScale <= RootPanel.ActualWidth - 8)
                    clampWv = RootPanel.ActualWidth - 8 - _dragBaseLeft;
                else if (_dragMode.Contains("l") && _dragBaseLeft >= 8)
                    clampWv = (_dragBaseLeft + _dragStartW * layerScale) - 8;
                if (_dragMode.Contains("b") && _dragBaseTop + _dragStartH * layerScale <= RootPanel.ActualHeight - 16 - 8)
                    clampHv = RootPanel.ActualHeight - 16 - 8 - _dragBaseTop;
                else if (_dragMode.Contains("t") && _dragBaseTop >= 16 + 8)
                    clampHv = (_dragBaseTop + _dragStartH * layerScale) - (16 + 8);
                double clampW = clampWv == double.MaxValue ? double.MaxValue : clampWv / layerScale;
                double clampH = clampHv == double.MaxValue ? double.MaxValue : clampHv / layerScale;
                if (w > clampW) { w = Math.Max(MinKeyW, clampW); if (_dragMode.Contains("l")) ml = (_dragStartML + _dragStartW) - w; }
                if (h > clampH) { h = Math.Max(MinKeyH, clampH); if (_dragMode.Contains("t")) mt = (_dragStartMT + _dragStartH) - h; }

                key.Width = w;
                key.Height = h;
                // 缩放补偿走 transform（Margin 保持起点）——避免 StackPanel 流式布局推挤兄弟按键（0.7.1）
                SetTransformXY(key, _dragStartTx + (ml - _dragStartML), _dragStartTy + (mt - _dragStartMT));
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
            // 0.9.4 多选模式：短按（未发生移动/缩放）→ 切换选中；长按移动与边缘缩放照常走下方流程
            if (TryFinishMultiSelectTap())
            {
                try { (sender as Border)?.ReleasePointerCapture(e.Pointer); } catch { }
                ApplyCursor(null);
                HideSnapLines();
                e.Handled = true;
                return;
            }
            if (_moveKey != null)
            {
                var key = _moveKey;
                _moveKey = null;
                try { key.ReleasePointerCapture(e.Pointer); } catch { }
                EndMoveStyle(key);   // 恢复样式（鼠标垫走专属恢复，其余 SetKey(false) 清移动高亮）
                HideSnapLines();      // 落位隐藏吸附参考线
                // 0.9.4：整组移动落位 —— 组内其他键一并持久化（被拖键走下面的常规流程）
                var groupOthers = TakeGroupMoveOthers();
                foreach (var gk in groupOthers)
                {
                    try { PersistKeyPosition(gk); } catch { }
                }
                PersistKeyPosition(key);
                if (groupOthers.Count > 0)
                    DiagLog("group move end: " + (groupOthers.Count + 1) + " keys persisted");
                OffsetKeyLayerForNegativeKeys();   // 0.8.3：移动落位后重算左缘补偿（键可能被拖出左界）
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
                string dnm = NameOf(dragKey);
                // 0.8.3 修复：自定义键缩放后持久化尺寸与位置（此前 SaveLayout 只覆盖默认键，
                // 自定义键缩放结果重启即丢）
                if (!string.IsNullOrEmpty(dnm) && _customKeys.ContainsKey(dnm))
                {
                    double dtx = 0, dty = 0;
                    var dtt = dragKey.RenderTransform as TranslateTransform;
                    if (dtt != null) { dtx = dtt.X; dty = dtt.Y; }
                    ApplicationData.Current.LocalSettings.Values["CustomSize_" + dnm] =
                        ((int)dragKey.Width) + ";" + ((int)dragKey.Height);
                    ApplicationData.Current.LocalSettings.Values["CustomPos_" + dnm] =
                        dtx.ToString(CultureInfo.InvariantCulture) + ";" + dty.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    SaveLayout();
                }
                OffsetKeyLayerForNegativeKeys();   // 0.8.3：缩放落位后重算左缘补偿
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
            foreach (var kv in _keys) if (kv.Value != exclude) AddSnapRects(rects, kv.Value);
            foreach (var kv in _mouse) if (kv.Value != exclude) AddSnapRects(rects, kv.Value);
            // 1.3：手柄部件隐藏时（组合模式 / 未连接手柄）不作为吸附目标——否则会出现"看不到的键"在吸参考线
            foreach (var kv in _customKeys)
                if (kv.Value != exclude && !(IsGpPartName(kv.Key) && kv.Value.Visibility != Visibility.Visible))
                    AddSnapRects(rects, kv.Value);
            if (MousePad != exclude) AddSnapRects(rects, MousePad);   // 0.7.1：鼠标垫也是吸附目标（靠近鼠标垫有参考线）
            return rects;
        }

        // 0.7.1 间隔吸附：每个候选生成两个矩形——原矩形（0px 贴边/对齐）+ 四边外扩 SnapGap 的间隔矩形
        // （"相邻但不接触"的 10px 间距吸附）。参考线显示在命中矩形边沿，即按键四边外 10px 处。
        private void AddSnapRects(List<Rect> rects, Border b)
        {
            var r = VisualRectOf(b);
            if (r.Width <= 0 || r.Height <= 0) return;
            rects.Add(r);
            rects.Add(new Rect(r.X - SnapGap, r.Y - SnapGap, r.Width + 2 * SnapGap, r.Height + 2 * SnapGap));
        }

        // 缩放吸附：对"被调整的边"做十字方向边对边贴齐（统一 SnapCanvas 坐标）。
        // 被调边由 _dragMode 决定（含 l/r/t/b）；只吸附被调整的那条边（对应的固定边不动）。
        // 修正直接写回 w/h/ml/mt，仍受调用方的最小尺寸保护。
        private void ApplyDragSnap(Border key, ref double w, ref double h, ref double ml, ref double mt)
        {
            // 0.9.6 修复：w/h/ml/mt 是图层单位，而吸附判定统一用 SnapCanvas 视觉坐标（其他键的视觉矩形），
            // 所以这里按图层缩放把尺寸/位移换算成视觉量再比较；命中增量写回图层单位前再除回去。
            // scale = 1 时与修复前完全一致。
            double s = CurrentLayerScale();
            // 被调按键当前四边（SnapCanvas 坐标）：起点视觉基准 + 缩放增量反推
            double baseL = _dragBaseLeft, baseT = _dragBaseTop;
            double baseRight = baseL + _dragStartW * s, baseBot = baseT + _dragStartH * s;
            double left = baseL + (ml - _dragStartML) * s;
            double top = baseT + (mt - _dragStartMT) * s;
            double right = left + w * s;
            double bot = top + h * s;
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
                    // 右缘移动：delta 加到宽度（左缘固定）；视觉增量换算回图层单位
                    w = Math.Max(MinKeyW, w + hitH.Delta / s);
                }
                else
                {
                    // 左缘移动：delta 加到 ml，宽度反向收缩（右缘固定）
                    ml += hitH.Delta / s;
                    w = Math.Max(MinKeyW, w - hitH.Delta / s);
                }
            }
            if (snappedV)
            {
                if (_dragMode.Contains("b"))
                {
                    h = Math.Max(MinKeyH, h + hitV.Delta / s);
                }
                else
                {
                    mt += hitV.Delta / s;
                    h = Math.Max(MinKeyH, h - hitV.Delta / s);
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
            _mouse["WheelUp"] = MouseWheelUp; _mouse["WheelDown"] = MouseWheelDown;   // 0.7.0 滚轮上/下（VK 0x07/0x08）
            // 0.8.2 缓存 XAML 初始文本（仅首次）：重置按键布局时按此还原显示名（改名只写运行时 Text，XAML 初始值仍可从控件读到）
            if (_defaultKeyTexts.Count == 0)
            {
                foreach (var kv in _keys)
                {
                    var tb0 = kv.Value.Child as TextBlock;
                    if (tb0 != null && !string.IsNullOrEmpty(tb0.Text)) _defaultKeyTexts[kv.Key] = tb0.Text;
                }
                foreach (var kv in _mouse)
                {
                    var tb0 = kv.Value.Child as TextBlock;
                    if (tb0 != null && !string.IsNullOrEmpty(tb0.Text)) _defaultKeyTexts[kv.Key] = tb0.Text;
                }
            }
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
                // 0.8.3：TryParse + NaN/Infinity 拒绝 + 上限钳制（原 double.Parse 能吃进 "NaN"，
                // 而 NaN < MinKeyW 为 false 会绕过下限校验，把 NaN 宽写进 Border）
                double w, h, tx, ty;
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out w) ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out h) ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out tx) ||
                    !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out ty)) return;
                if (double.IsNaN(w) || double.IsInfinity(w) || double.IsNaN(h) || double.IsInfinity(h) ||
                    double.IsNaN(tx) || double.IsInfinity(tx) || double.IsNaN(ty) || double.IsInfinity(ty)) return;
                if (w < MinKeyW || h < MinKeyH) return;
                if (w > 2000 || h > 2000) return;
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


        // ===================== 手柄按键显示（1.2）=====================
        // 数据来源：伴生进程在连接握手 CMD|PROTO|92 之后，把 16 字节手柄尾块追加到快照末尾
        // （见 KeyDisplay.Companion/state.py 的 _FMT_V5_TAIL 与 gamepad.py）；本类只做"状态 → 颜色/位置"的映射。
        // 几何、字形与配色全部照 tools\pad_preview.py 的 PadPanel（_build/apply_theme/update_state），
        // 元素本体在 Widget1.xaml 的 GamepadPanel 里（在 KeyLayer 内 → 自动继承键区整体缩放、主题与透明度）。
        //
        // 设置键契约（与设置窗口同事约定：键名/类型/取值/默认值完全一致，不可单方面改动）：
        //   GamepadMode_     int     0=关闭 1=自动（检测到已连接手柄才显示） 2=始终显示     默认 1
        //   GamepadSlot_     int     -1=自动跟随活跃手柄 0..3=固定 P1..P4                  默认 -1
        //   GamepadStyle_    string  "xbox"/"ps"/"switch"（物理位置固定，只换印在按键上的字） 默认 "xbox"
        //   GamepadParts_    int     位掩码 bit0 左摇杆 … bit8 电量                        默认 0x1FF（全开）
        //   GamepadTrigger_  string  "bar"/"value"/"highlight"                            默认 "bar"
        //   GamepadDeadzone_ int     0..40（百分比）                                       默认 24
        //   GamepadBrand_    int     0/1（ABXY 是否用品牌色）                              默认 0
        // 说明：尾块只带伴生进程选定的那一个手柄的明细（活跃槽优先），协议层没有逐槽明细，
        // 因此 GamepadSlot_ 在这里只用于判定"固定槽是否已连接"：该槽未连接时按无手柄处理，
        // 已连接时显示的仍是尾块下发的那一份数据。
        private int _gpMode = 1;
        private int _gpSlot = -1;
        private string _gpStyle = "xbox";
        private int _gpParts = 0x1FF;
        private string _gpTrigger = "bar";
        private int _gpDeadzone = 24;
        private int _gpBrand;
        // 1.4：键位风格新增 "auto"（按实际连接的手柄品牌自动挑模板与字形）：
        //   _gpStyleRaw = GamepadStyle_ 的原值（xbox/ps/switch/auto）；_gpStyle = 实际生效的风格
        //   （auto 时 = 检测到的品牌，检测不到或 unknown 一律按 xbox）。用 _gpStyleRaw 判断"是否 auto"。
        private string _gpStyleRaw = "xbox";
        private string _gpAutoBrand;       // 自动检测到的品牌（null = 还没有拿到品牌 → 按 xbox）
        private long _gpAutoLastTicks;     // 上次查询时刻（限频用；单飞避免重复发 CMD|PAD）
        private bool _gpAutoBusy;          // 正在查询中
        private bool _gpAutoAnswered;      // 接收器是否应答过 CMD|PAD（决定 10s / 60s 的重试间隔）
        private string _gpTemplateStyle;   // 已建立的模板风格（null = 还没建立；仅变化时重建）

        // XINPUT_GAMEPAD_* 掩码（与 KeyDisplay.Companion/gamepad.py 的常量逐位一致；0x0400 Guide 为未公开位）
        private const uint GP_DPAD_UP = 0x0001;
        private const uint GP_DPAD_DOWN = 0x0002;
        private const uint GP_DPAD_LEFT = 0x0004;
        private const uint GP_DPAD_RIGHT = 0x0008;
        private const uint GP_START = 0x0010;
        private const uint GP_BACK = 0x0020;
        private const uint GP_LEFT_THUMB = 0x0040;
        private const uint GP_RIGHT_THUMB = 0x0080;
        private const uint GP_LEFT_SHOULDER = 0x0100;
        private const uint GP_RIGHT_SHOULDER = 0x0200;
        private const uint GP_GUIDE = 0x0400;
        private const uint GP_A = 0x1000;
        private const uint GP_B = 0x2000;
        private const uint GP_X = 0x4000;
        private const uint GP_Y = 0x8000;

        // GamepadParts_ 位掩码（bit0 左摇杆 … bit8 电量）
        private const int GP_PART_LS = 1 << 0;
        private const int GP_PART_RS = 1 << 1;
        private const int GP_PART_DPAD = 1 << 2;
        private const int GP_PART_FACE = 1 << 3;
        private const int GP_PART_SHOULDER = 1 << 4;
        private const int GP_PART_TRIGGER = 1 << 5;
        private const int GP_PART_MENU = 1 << 6;
        private const int GP_PART_GUIDE = 1 << 7;
        private const int GP_PART_BATTERY = 1 << 8;

        // 几何常量（与 Widget1.xaml 里 GamepadPanel 各元素的 Canvas.Left/Top 同源，数值取自 tools\pad_preview.py）
        private const double GpTriggerInnerH = 28.0;   // 扳机条内可用高度 = 条高 30 − 上下各 1 的边框
        private const double GpStickRingR = 15.0;      // 摇杆环半径（φ30）
        private const double GpStickDotR = 5.0;        // 摇杆点半径（φ10）
        private const double GpStickTravel = GpStickRingR - GpStickDotR;   // 点圆心的可移动半径
        private const double GpLsCenterX = 34.0, GpLsCenterY = 44.0;       // 左摇杆环心
        private const double GpRsCenterX = 126.0, GpRsCenterY = 88.0;      // 右摇杆环心
        private const int GpTriggerHighlightThreshold = 30;   // 与 gamepad.py 的 TRIGGER_THRESHOLD 一致

        // 摇杆点平滑（与鼠标点同一套指数插值：alpha = 1-0.75^(dt/16.67)，帧率无关）。
        // Tx/Ty = 本帧目标（归一化 -1..1，Y 向上为正），Sx/Sy = 当前平滑值；
        // 到位后 _gpStickAnim=false → UpdateGamepadSticks 直接返回（静止时零开销）。
        private double _gpLsTx, _gpLsTy, _gpLsSx, _gpLsSy;
        private double _gpRsTx, _gpRsTy, _gpRsSx, _gpRsSy;
        private bool _gpStickInit;      // 摇杆点是否已就位（整组重新显示时置 false → 直接就位，不从旧位置飞过来）
        private bool _gpStickAnim;      // 是否有摇杆正在平滑追赶
        private long _gpLastTicks = -1;
        private bool _gpPanelShown;     // 上一帧整组是否显示（用于检测"重新显示"）

        // 品牌色（GamepadBrand_=1 时 ABXY 未按下的文字用品牌色；默认关，跟随主题色）
        private readonly SolidColorBrush _gpBrandA = new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x7C, 0x10));   // 绿
        private readonly SolidColorBrush _gpBrandB = new SolidColorBrush(Color.FromArgb(0xFF, 0xD8, 0x3B, 0x01));   // 红
        private readonly SolidColorBrush _gpBrandX = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD7));   // 蓝
        private readonly SolidColorBrush _gpBrandY = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xB9, 0x00));   // 黄

        // 从 LocalSettings 读 7 个手柄设置进字段（越界/非法值一律回落到契约里的默认值）。
        // 由 ReloadSettingsFromStore（冷启动 + 设置窗口 SignalDataChanged）调用，应用在 ApplyGamepadSettings。
        private void ReloadGamepadSettings()
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                _gpMode = (int)ParseDoubleOr(v["GamepadMode_"], 1);
                if (_gpMode < 0 || _gpMode > 2) _gpMode = 1;
                _gpSlot = (int)ParseDoubleOr(v["GamepadSlot_"], -1);
                if (_gpSlot < -1 || _gpSlot > 3) _gpSlot = -1;
                string style = (v["GamepadStyle_"] as string ?? "").Trim().ToLowerInvariant();
                // 1.4：白名单新增 "auto"（自动检测）；缺省/非法值仍回落 xbox，保证默认行为与改动前一致
                _gpStyleRaw = (style == "auto" || style == "ps" || style == "switch") ? style : "xbox";
                _gpStyle = _gpStyleRaw == "auto" ? (_gpAutoBrand != null ? _gpAutoBrand : "xbox") : _gpStyleRaw;
                _gpParts = (int)ParseDoubleOr(v["GamepadParts_"], 0x1FF) & 0x1FF;   // 只有 9 个部件位有效
                string trig = (v["GamepadTrigger_"] as string ?? "").Trim().ToLowerInvariant();
                _gpTrigger = (trig == "value" || trig == "highlight") ? trig : "bar";
                _gpDeadzone = (int)ParseDoubleOr(v["GamepadDeadzone_"], 24);
                if (_gpDeadzone < 0) _gpDeadzone = 0;
                if (_gpDeadzone > 40) _gpDeadzone = 40;
                _gpBrand = (int)ParseDoubleOr(v["GamepadBrand_"], 0) != 0 ? 1 : 0;
                // 1.3：布局模式（组合面板 / 逐个添加的手柄部件）。缺省或非法值一律 composite ——
                // 用户从未设置过时看到的必须与改动前完全一致。
                string layout = (v["GamepadLayout_"] as string ?? "").Trim().ToLowerInvariant();
                _gpLayout = (layout == "custom") ? "custom" : "composite";
                // 1.4：auto 风格 → 立即起一次品牌查询（内部 10s 限频 + 单飞；未连接/失败一律静默按 xbox）
                if (_gpStyleRaw == "auto") TryStartGamepadBrandDetect();
            }
            catch (Exception ex) { DiagLog("gamepad settings read fail: " + ex.Message); }
        }

        // 应用手柄设置：字形（风格）→ 静息态配色（主题）→ 部件显隐 → 整组可见性。
        // ApplyTheme 也会调用（主题/风格变化后立即重刷，与 ApplySettingsColors 同一时机）。
        private void ApplyGamepadSettings()
        {
            try
            {
                ApplyGamepadLabels();
                ApplyGamepadColors();
                ApplyGamepadParts();
                // 1.4：组合模式的手柄轮廓模板（风格切换即时换模板；custom 模式整块隐藏）
                ApplyGamepadTemplate();
                // 整组可见性：关闭 → 立即折叠；始终显示 → 立即显示（没有快照时显示静息态）；
                // 自动 → 完全交给每帧的 ApplyGamepadState（检测到已连接手柄才显示，默认行为与改动前一致）。
                if (_gpMode == 0)
                {
                    GpSetVisible(GamepadPanel, false);
                    _gpPanelShown = false;
                    _gpStickInit = false;
                    _gpStickAnim = false;
                }
                else if (_gpMode == 2)
                {
                    GpSetVisible(GamepadPanel, true);
                }
                // 1.3：custom 布局下整块组合面板始终隐藏（数据仍在，切回 composite 立即恢复显示）
                if (_gpLayout == "custom")
                {
                    GpSetVisible(GamepadPanel, false);
                    _gpPanelShown = false;
                    _gpStickInit = false;
                    _gpStickAnim = false;
                }
                // 1.3：自定义手柄部件（Gp_*）的显隐按布局模式 + GamepadMode_ 立即生效（切换模式即时可见）
                ApplyGamepadCustomParts(_latest);
            }
            catch (Exception ex) { DiagLog("gamepad settings apply fail: " + ex.Message); }
        }

        // 风格 → 字形（物理位置固定，只换印在按键上的字）：
        // 肩键 LB/RB → L1/R1 → L/R；扳机 LT/RT → L2/R2 → ZL/ZR；
        // 面位 下/右/左/上 = Xbox A/B/X/Y → PS ✕/○/□/△ → Switch B/A/Y/X。
        private void ApplyGamepadLabels()
        {
            try
            {
                bool ps = _gpStyle == "ps", sw = _gpStyle == "switch";
                GpSetText(GpLBText, ps ? "L1" : sw ? "L" : "LB");
                GpSetText(GpRBText, ps ? "R1" : sw ? "R" : "RB");
                GpSetText(GpLTText, GpTriggerName(true));
                GpSetText(GpRTText, GpTriggerName(false));
                GpSetText(GpFaceBottomText, ps ? "\u2715" : sw ? "B" : "A");   // ✕ / B / A
                GpSetText(GpFaceRightText, ps ? "\u25CB" : sw ? "A" : "B");    // ○ / A / B
                GpSetText(GpFaceLeftText, ps ? "\u25A1" : sw ? "Y" : "X");     // □ / Y / X
                GpSetText(GpFaceTopText, ps ? "\u25B3" : sw ? "X" : "Y");      // △ / X / Y
            }
            catch (Exception ex) { DiagLog("gamepad labels fail: " + ex.Message); }
        }

        // 扳机标签（三种风格）：LT/RT、L2/R2、ZL/ZR（与 TextBlock 内百分比文字拼接使用）
        private string GpTriggerName(bool left)
        {
            bool ps = _gpStyle == "ps", sw = _gpStyle == "switch";
            return left ? (ps ? "L2" : sw ? "ZL" : "LT") : (ps ? "R2" : sw ? "ZR" : "RT");
        }

        // 手柄元素的静息态配色（主题/风格变化后调用）。按下态由每帧 ApplyGamepadState 覆盖；
        // 配色只用现有语义画刷方法（KeyBgB/BorderB/KeyFgB/DotB/PressBgB/PressFgB/AccentB），不新增主题色常量。
        private void ApplyGamepadColors()
        {
            try
            {
                SetKey(GpLB, false);
                SetKey(GpRB, false);
                SetKey(GpView, false);
                SetKey(GpMenu, false);
                SetKey(GpFaceBottom, false);   // 品牌色由 ApplyGamepadState 在未按下时叠加
                SetKey(GpFaceRight, false);
                SetKey(GpFaceLeft, false);
                SetKey(GpFaceTop, false);
                SetKey(GpDpadUp, false);
                SetKey(GpDpadDown, false);
                SetKey(GpDpadLeft, false);
                SetKey(GpDpadRight, false);

                var bd = BorderB();
                if (!ReferenceEquals(GpLsRing.Stroke, bd)) GpLsRing.Stroke = bd;
                if (!ReferenceEquals(GpRsRing.Stroke, bd)) GpRsRing.Stroke = bd;
                var dot = DotB();
                if (!ReferenceEquals(GpLsDot.Fill, dot)) GpLsDot.Fill = dot;
                if (!ReferenceEquals(GpRsDot.Fill, dot)) GpRsDot.Fill = dot;
                var bg = KeyBgB();
                if (!ReferenceEquals(GpGuide.Fill, bg)) GpGuide.Fill = bg;
                if (!ReferenceEquals(GpGuide.Stroke, bd)) GpGuide.Stroke = bd;
                var fg = KeyFgB();
                if (!ReferenceEquals(GpLsHint.Foreground, fg)) GpLsHint.Foreground = fg;
                if (!ReferenceEquals(GpRsHint.Foreground, fg)) GpRsHint.Foreground = fg;
                if (!ReferenceEquals(GpBattery.Foreground, fg)) GpBattery.Foreground = fg;

                // 扳机条回到静息态（值 0 = 空填充 + 主题字色）
                GpApplyTrigger(GpLT, GpLTFill, GpLTText, 0, GpTriggerName(true));
                GpApplyTrigger(GpRT, GpRTFill, GpRTText, 0, GpTriggerName(false));
            }
            catch (Exception ex) { DiagLog("gamepad colors fail: " + ex.Message); }
        }

        // 部件显隐：按 GamepadParts_ 位掩码逐组折叠/显示（只切 Visibility，不动位置/尺寸/颜色）
        private void ApplyGamepadParts()
        {
            try
            {
                bool ls = (_gpParts & GP_PART_LS) != 0;
                bool rs = (_gpParts & GP_PART_RS) != 0;
                bool dpad = (_gpParts & GP_PART_DPAD) != 0;
                bool face = (_gpParts & GP_PART_FACE) != 0;
                bool shoulder = (_gpParts & GP_PART_SHOULDER) != 0;
                bool trigger = (_gpParts & GP_PART_TRIGGER) != 0;
                bool menu = (_gpParts & GP_PART_MENU) != 0;

                GpSetVisible(GpLsRing, ls);
                GpSetVisible(GpLsDot, ls);
                GpSetVisible(GpLsHint, ls);
                GpSetVisible(GpRsRing, rs);
                GpSetVisible(GpRsDot, rs);
                GpSetVisible(GpRsHint, rs);
                GpSetVisible(GpDpadUp, dpad);
                GpSetVisible(GpDpadDown, dpad);
                GpSetVisible(GpDpadLeft, dpad);
                GpSetVisible(GpDpadRight, dpad);
                GpSetVisible(GpFaceBottom, face);
                GpSetVisible(GpFaceRight, face);
                GpSetVisible(GpFaceLeft, face);
                GpSetVisible(GpFaceTop, face);
                GpSetVisible(GpLB, shoulder);
                GpSetVisible(GpRB, shoulder);
                GpSetVisible(GpLT, trigger);
                GpSetVisible(GpLTFill, trigger);
                GpSetVisible(GpLTText, trigger);
                GpSetVisible(GpRT, trigger);
                GpSetVisible(GpRTFill, trigger);
                GpSetVisible(GpRTText, trigger);
                GpSetVisible(GpView, menu);
                GpSetVisible(GpMenu, menu);
                GpSetVisible(GpGuide, (_gpParts & GP_PART_GUIDE) != 0);
                GpSetVisible(GpBattery, (_gpParts & GP_PART_BATTERY) != 0);
            }
            catch (Exception ex) { DiagLog("gamepad parts fail: " + ex.Message); }
        }

        // 每帧：把一份新快照映射到界面（只在 snap.Seq 变化时由 OnRendering 调用）。
        // 整组是否显示由 GamepadMode_ 决定；未连接 / 该部件被设置关闭 → 对应元素 Collapsed。
        // 全部包在 try/catch 内：手柄侧的任何异常只记 DiagLog，绝不影响键鼠渲染。
        private void ApplyGamepadState(InputSnapshot snap)
        {
            try
            {
                // 1.3：custom 布局下组合面板整块交给部件显示：隐藏面板并跳过组合渲染（快照数据不受影响）
                if (_gpLayout == "custom")
                {
                    GpSetVisible(GamepadPanel, false);
                    _gpPanelShown = false;
                    _gpStickInit = false;
                    _gpStickAnim = false;
                    return;
                }
                int conn = snap != null ? snap.GamepadConnected : 0;
                bool hasTail = snap != null && snap.HasGamepad;   // 只有握手过 92 字节帧的伴生进程才带手柄尾块
                bool show = snap != null && (_gpMode == 2 || (_gpMode == 1 && hasTail && conn != 0));
                // 固定槽位（0..3）：该槽未连接时按"无手柄"处理（见上面的协议说明）
                if (_gpSlot >= 0 && _gpSlot <= 3 && (conn & (1 << _gpSlot)) == 0) show = false;
                if (_gpMode == 0) show = false;
                if (!show)
                {
                    GpSetVisible(GamepadPanel, false);
                    _gpPanelShown = false;
                    _gpStickInit = false;
                    _gpStickAnim = false;
                    return;
                }
                GpSetVisible(GamepadPanel, true);
                if (!_gpPanelShown) _gpStickInit = false;   // 重新显示：摇杆点直接就位
                _gpPanelShown = true;

                uint btn = snap.GamepadButtons;

                // ---- 肩键 / View / Menu / Guide ----
                SetKey(GpLB, (btn & GP_LEFT_SHOULDER) != 0);
                SetKey(GpRB, (btn & GP_RIGHT_SHOULDER) != 0);
                SetKey(GpView, (btn & GP_BACK) != 0);
                SetKey(GpMenu, (btn & GP_START) != 0);
                GpSetGuide((btn & GP_GUIDE) != 0);

                // ---- 十字键 ----
                SetKey(GpDpadUp, (btn & GP_DPAD_UP) != 0);
                SetKey(GpDpadDown, (btn & GP_DPAD_DOWN) != 0);
                SetKey(GpDpadLeft, (btn & GP_DPAD_LEFT) != 0);
                SetKey(GpDpadRight, (btn & GP_DPAD_RIGHT) != 0);

                // ---- ABXY（物理位置固定，只看对应的掩码位）----
                GpSetFaceKey(GpFaceBottom, (btn & GP_A) != 0);
                GpSetFaceKey(GpFaceRight, (btn & GP_B) != 0);
                GpSetFaceKey(GpFaceLeft, (btn & GP_X) != 0);
                GpSetFaceKey(GpFaceTop, (btn & GP_Y) != 0);

                // ---- 扳机（0~255；bar / value / highlight 三选一）----
                GpApplyTrigger(GpLT, GpLTFill, GpLTText, snap.GamepadLT, GpTriggerName(true));
                GpApplyTrigger(GpRT, GpRTFill, GpRTText, snap.GamepadRT, GpTriggerName(false));

                // ---- 摇杆：死区 + 径向钳制 → 归一化目标（点圆心 = 环心 + (nx, -ny) × 可移动半径）----
                double lnx, lny, rnx, rny;
                GpStickNormalize(snap.GamepadLX, snap.GamepadLY, _gpDeadzone, out lnx, out lny);
                GpStickNormalize(snap.GamepadRX, snap.GamepadRY, _gpDeadzone, out rnx, out rny);
                _gpLsTx = lnx;
                _gpLsTy = lny;
                _gpRsTx = rnx;
                _gpRsTy = rny;

                var ringStroke = BorderB();
                if (!ReferenceEquals(GpLsRing.Stroke, ringStroke)) GpLsRing.Stroke = ringStroke;
                if (!ReferenceEquals(GpRsRing.Stroke, ringStroke)) GpRsRing.Stroke = ringStroke;
                // L3/R3 按下时用"鼠标点按下色"（与鼠标点按下的处理一致）
                var lDot = (btn & GP_LEFT_THUMB) != 0 ? DotPressedB() : DotB();
                if (!ReferenceEquals(GpLsDot.Fill, lDot)) GpLsDot.Fill = lDot;
                var rDot = (btn & GP_RIGHT_THUMB) != 0 ? DotPressedB() : DotB();
                if (!ReferenceEquals(GpRsDot.Fill, rDot)) GpRsDot.Fill = rDot;

                if (!_gpStickInit)
                {
                    // 首次显示 / 重新显示：直接就位（与鼠标点首帧直接就位同理，避免点从旧位置飞过来）
                    _gpLsSx = _gpLsTx;
                    _gpLsSy = _gpLsTy;
                    _gpRsSx = _gpRsTx;
                    _gpRsSy = _gpRsTy;
                    _gpStickInit = true;
                    GpPlaceDot(GpLsDot, GpLsCenterX, GpLsCenterY, _gpLsSx, _gpLsSy);
                    GpPlaceDot(GpRsDot, GpRsCenterX, GpRsCenterY, _gpRsSx, _gpRsSy);
                    _gpStickAnim = false;
                }
                else
                {
                    if (!_gpStickAnim) _gpLastTicks = -1;   // 新一段移动：首帧用默认帧间隔（与鼠标点一致）
                    _gpStickAnim = true;                    // 交给 UpdateGamepadSticks 平滑追赶，到位后自动置 false
                }

                // ---- 电量角标（0/1/2/3 → 空/低/中/满；0xFF 或未知 → 空）----
                int lv = snap.GamepadBattery;
                GpSetText(GpBattery, lv == 1 ? "\u7535\u91cf \u4f4e"
                    : lv == 2 ? "\u7535\u91cf \u4e2d"
                    : lv == 3 ? "\u7535\u91cf \u6ee1" : "");
            }
            catch (Exception ex) { DiagLog("gamepad state fail: " + ex.Message); }
        }

        // 扳机条：按显示方式设置填充与文字（值 0~255）。
        //   bar       → 填充高度 = 条内高 × 值/255，填充色 AccentB()
        //   value     → 同 bar，另在文字上追加百分比
        //   highlight → 不填充，值 > 30（同 gamepad.py 的 TRIGGER_THRESHOLD）时整条用按下底/按下字
        // 全部走"值未变不写依赖属性"的幂等写入（本方法在渲染循环里每帧被调用）。
        private void GpApplyTrigger(Border bar, Rectangle fill, TextBlock label, int value, string name)
        {
            if (value < 0) value = 0;
            if (value > 255) value = 255;
            bool highlight = _gpTrigger == "highlight";
            bool lit = value > GpTriggerHighlightThreshold;

            var bd = BorderB();
            if (!ReferenceEquals(bar.BorderBrush, bd)) bar.BorderBrush = bd;
            var bg = highlight && lit ? PressBgB() : KeyBgB();
            if (!ReferenceEquals(bar.Background, bg)) bar.Background = bg;

            if (highlight)
            {
                GpSetHeight(fill, 0.0);   // 仅高亮：不填充（整条换色）
            }
            else
            {
                var accent = AccentB();
                if (!ReferenceEquals(fill.Fill, accent)) fill.Fill = accent;
                GpSetHeight(fill, GpTriggerInnerH * (value / 255.0));
            }

            var fg = highlight ? (lit ? PressFgB() : KeyFgB()) : (lit ? AccentB() : KeyFgB());
            if (!ReferenceEquals(label.Foreground, fg)) label.Foreground = fg;
            GpSetText(label, _gpTrigger == "value" ? name + " " + (int)(value / 2.55) + "%" : name);
        }

        // Guide（φ22 圆环）：按下时圆环用按下底色填充（与预览一致）
        private void GpSetGuide(bool down)
        {
            var bg = down ? PressBgB() : KeyBgB();
            if (!ReferenceEquals(GpGuide.Fill, bg)) GpGuide.Fill = bg;
            var bd = BorderB();
            if (!ReferenceEquals(GpGuide.Stroke, bd)) GpGuide.Stroke = bd;
        }

        // 面位按键：与 SetKey 同样的"底/字对调"，品牌色开启时再给未按下态的文字叠品牌色
        private void GpSetFaceKey(Border b, bool down)
        {
            SetKey(b, down);
            if (_gpBrand == 0 || down) return;
            var tb = b.Child as TextBlock;
            if (tb == null) return;
            var brand = GpBrandBrush(tb.Text);
            if (brand == null) return;
            if (!ReferenceEquals(tb.Foreground, brand)) tb.Foreground = brand;
        }

        // 品牌色按"印在按键上的字"取（与 pad_preview.py 的 BRAND 表一致）：
        // PS/Switch 风格下没有 A/B/X/Y（或字母位置与 Xbox 不同）时返回 null → 保持主题字色
        private SolidColorBrush GpBrandBrush(string label)
        {
            switch (label)
            {
                case "A": return _gpBrandA;
                case "B": return _gpBrandB;
                case "X": return _gpBrandX;
                case "Y": return _gpBrandY;
            }
            return null;
        }

        // 死区 + 径向钳制（与 KeyDisplay.Companion/gamepad.py 的 apply_stick_deadzone 同一算法）：
        // 1) 幅值 ≤ 死区（deadzonePercent/100 × 32767）→ 归零；否则把"死区 → 满量程"重新线性映射到 0..1
        //    （点从死区边界开始动，不会有跳变）；
        // 2) 幅值 > 1 时整体缩放，保证斜角方向刚好贴住圆环内缘
        //    （XInput 摇杆是方框钳制，斜角能到 ±32767，直接按 x/32767 画点会戳出环外）。
        private static void GpStickNormalize(int x, int y, int deadzonePercent, out double nx, out double ny)
        {
            nx = 0.0;
            ny = 0.0;
            double dz = deadzonePercent / 100.0;
            if (dz < 0.0) dz = 0.0;
            if (dz > 0.99) dz = 0.99;
            double mag = Math.Sqrt((double)x * x + (double)y * y);
            if (mag <= 0.0) return;
            double norm = mag / 32767.0;
            if (norm <= dz) return;
            double scaled = (norm - dz) / (1.0 - dz);
            if (scaled > 1.0) scaled = 1.0;
            double k = scaled / norm;
            nx = x * k / 32767.0;
            ny = y * k / 32767.0;
        }

        // 摇杆点圆心 = 环心 + (nx, -ny) × 可移动半径（协议 Y 轴向上为正，画布 Y 向下）
        private static void GpPlaceDot(Ellipse dot, double cx, double cy, double nx, double ny)
        {
            double left = cx + nx * GpStickTravel - GpStickDotR;
            double top = cy - ny * GpStickTravel - GpStickDotR;
            double curL = Canvas.GetLeft(dot);
            if (double.IsNaN(curL) || Math.Abs(curL - left) > 0.01) Canvas.SetLeft(dot, left);
            double curT = Canvas.GetTop(dot);
            if (double.IsNaN(curT) || Math.Abs(curT - top) > 0.01) Canvas.SetTop(dot, top);
        }

        // 摇杆点平滑追赶（与鼠标点同一套指数插值；到位即吸附停止 → 静止时零开销）
        private void UpdateGamepadSticks()
        {
            if (!_gpStickAnim) return;
            long now = _frameClock.ElapsedTicks;
            double dtMs = _gpLastTicks < 0
                ? 16.7
                : (now - _gpLastTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _gpLastTicks = now;
            double alpha = 1.0 - Math.Pow(CursorDampingDecay, dtMs / (1000.0 / 60.0));
            bool busy = GpStepStick(GpLsDot, GpLsCenterX, GpLsCenterY, ref _gpLsSx, ref _gpLsSy, _gpLsTx, _gpLsTy, alpha);
            busy |= GpStepStick(GpRsDot, GpRsCenterX, GpRsCenterY, ref _gpRsSx, ref _gpRsSy, _gpRsTx, _gpRsTy, alpha);
            _gpStickAnim = busy;
        }

        private bool GpStepStick(Ellipse dot, double cx, double cy,
                                 ref double sx, ref double sy, double tx, double ty, double alpha)
        {
            double dx = tx - sx, dy = ty - sy;
            bool moving = dx * dx + dy * dy >= 0.0001;   // 距目标 ≥0.01：继续追赶
            if (moving)
            {
                sx += dx * alpha;
                sy += dy * alpha;
            }
            else
            {
                sx = tx;
                sy = ty;
            }
            GpPlaceDot(dot, cx, cy, sx, sy);
            return moving;
        }

        // ===================== 1.3：手柄部件（自定义模式，逐个添加）=====================
        // 与上面的"整块组合面板"（GamepadPanel）并存，由 GamepadLayout_ 切换：
        //   composite（默认）→ 组合面板照旧，全部部件隐藏；custom → 组合面板隐藏，显示已添加的部件。
        // 部件就是普通的自定义键：Custom_Gp_A=1 即"已添加"，位置/尺寸/显示名沿用 CustomPos_/CustomSize_/
        // DisplayName_，删除走既有自定义键删除路径（清 Custom_/CustomPos_/CustomSize_），
        // 因此拖动、四角缩放、吸附、整体按键大小、主题 10 色槽、透明度、布局预设、改名、删除、多选全部自动继承。
        // 唯一区别：这些键没有虚拟键码，按下态一律按手柄快照更新（绝不喂给 VkFromName / VK 位图逻辑）。
        private const string GpPartPrefix = "Gp_";
        private string _gpLayout = "composite";   // GamepadLayout_：composite=整块组合面板（默认）/ custom=逐部件

        // 20 个部件：名字前缀统一 Gp_（顺序即设置窗口里的展示顺序）
        private static readonly string[] GpParts =
        {
            "Gp_A", "Gp_B", "Gp_X", "Gp_Y",
            "Gp_LB", "Gp_RB", "Gp_LT", "Gp_RT",
            "Gp_LS", "Gp_RS", "Gp_L3", "Gp_R3",
            "Gp_DpadUp", "Gp_DpadDown", "Gp_DpadLeft", "Gp_DpadRight",
            "Gp_View", "Gp_Menu", "Gp_Guide", "Gp_Battery"
        };

        // 剥离复制副本的 "名(n)" 后缀（与 VkFromName 同一规则）→ 部件基名
        private static string GpBaseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int lp = name.LastIndexOf('(');
            if (lp > 0 && name[name.Length - 1] == ')')
            {
                bool digits = true;
                for (int i = lp + 1; i < name.Length - 1; i++)
                    if (name[i] < '0' || name[i] > '9') { digits = false; break; }
                if (digits) return name.Substring(0, lp);
            }
            return name;
        }

        // 是否手柄部件名（Gp_ 前缀；含复制副本 "Gp_A(2)"）——所有"按名字分流"的地方都用它
        private static bool IsGpPartName(string name)
        {
            return GpBaseName(name).StartsWith(GpPartPrefix, StringComparison.Ordinal);
        }

        // 部件类型（决定渲染方式）：
        // face=ABXY 圆 + 字母 / shoulder=LB/RB 圆角块 + 字 / trigger=LT/RT 竖条 + 底部填充 /
        // stick=LS/RS 圆环 + 随摇杆移动的点 / thumb=L3/R3 圆环 + 中心点（按下变按下色）/
        // dpad=小圆角方块 / guide=圆环 / battery=文字 / plain=非清单内的 Gp_ 名（当普通自定义键画）
        private static string GpPartKind(string name)
        {
            switch (GpBaseName(name))
            {
                case "Gp_A":
                case "Gp_B":
                case "Gp_X":
                case "Gp_Y": return "face";
                case "Gp_LB":
                case "Gp_RB": return "shoulder";
                case "Gp_LT":
                case "Gp_RT": return "trigger";
                case "Gp_LS":
                case "Gp_RS": return "stick";
                case "Gp_L3":
                case "Gp_R3": return "thumb";
                case "Gp_DpadUp":
                case "Gp_DpadDown":
                case "Gp_DpadLeft":
                case "Gp_DpadRight": return "dpad";
                case "Gp_View":
                case "Gp_Menu": return "menu";
                case "Gp_Guide": return "guide";
                case "Gp_Battery": return "battery";
            }
            return "plain";
        }

        // 部件默认尺寸（无 CustomSize_ 时的落位尺寸；之后用户可自由拖动/缩放，尺寸走既有 CustomSize_ 持久化）
        private static void GpDefaultSize(string name, out double w, out double h)
        {
            switch (GpBaseName(name))
            {
                case "Gp_A":
                case "Gp_B":
                case "Gp_X":
                case "Gp_Y": w = 40; h = 40; break;
                case "Gp_LB":
                case "Gp_RB": w = 64; h = 28; break;
                case "Gp_LT":
                case "Gp_RT": w = 24; h = 64; break;
                case "Gp_LS":
                case "Gp_RS":
                case "Gp_L3":
                case "Gp_R3": w = 56; h = 56; break;
                // 十字四向：不做十字合体，各自一块；上/下取竖长、左/右取横长，便于一眼区分方向
                case "Gp_DpadUp":
                case "Gp_DpadDown": w = 28; h = 34; break;
                case "Gp_DpadLeft":
                case "Gp_DpadRight": w = 34; h = 28; break;
                case "Gp_View":
                case "Gp_Menu": w = 56; h = 30; break;
                case "Gp_Guide": w = 48; h = 48; break;
                case "Gp_Battery": w = 96; h = 28; break;
                default: w = 52; h = 48; break;
            }
        }

        // 部件默认字形（随 GamepadStyle_ 变；与组合模式 ApplyGamepadLabels 同一张表：物理位置固定，只换印字）
        private string GpPartDefaultLabel(string name)
        {
            return GpPartLabelWithStyle(name, _gpStyle);
        }

        private static string GpPartLabelWithStyle(string name, string style)
        {
            bool ps = style == "ps", sw = style == "switch";
            switch (GpBaseName(name))
            {
                case "Gp_A": return ps ? "\u2715" : sw ? "B" : "A";   // ✕ / B / A
                case "Gp_B": return ps ? "\u25CB" : sw ? "A" : "B";   // ○ / A / B
                case "Gp_X": return ps ? "\u25A1" : sw ? "Y" : "X";   // □ / Y / X
                case "Gp_Y": return ps ? "\u25B3" : sw ? "X" : "Y";   // △ / X / Y
                case "Gp_LB": return ps ? "L1" : sw ? "L" : "LB";
                case "Gp_RB": return ps ? "R1" : sw ? "R" : "RB";
                case "Gp_LT": return ps ? "L2" : sw ? "ZL" : "LT";
                case "Gp_RT": return ps ? "R2" : sw ? "ZR" : "RT";
                case "Gp_View": return "View";
                case "Gp_Menu": return "Menu";
                case "Gp_LS": return "LS";
                case "Gp_RS": return "RS";
                case "Gp_L3": return "L3";
                case "Gp_R3": return "R3";
                case "Gp_Guide": return "Guide";
                case "Gp_Battery": return "";
                case "Gp_DpadUp": return "\u2191";   // ↑
                case "Gp_DpadDown": return "\u2193"; // ↓
                case "Gp_DpadLeft": return "\u2190"; // ←
                case "Gp_DpadRight": return "\u2192";// →
            }
            return GpBaseName(name);
        }

        // 布局预设会把小组件当时的"可见文本"当成显示名回写（WriteLayoutSnapshot：文字类取 TextBlock 文本、
        // 无文字类回落成键名；扳机在 value 模式下还带百分比）。这些都不是用户改的名字——若显示名等于键名/
        // 部件名、等于该部件在三种风格下的任一印字、或等于电量文字，一律视为"无覆盖"，
        // 否则风格切换与电量显示会被预设保存那一刻的值钉死。
        private static bool GpIsLiveLabel(string name, string kind, string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            string b = GpBaseName(name);
            if (text == name || text == b) return true;
            if (kind == "battery")
                return text == "\u7535\u91cf \u4f4e" || text == "\u7535\u91cf \u4e2d" || text == "\u7535\u91cf \u6ee1";
            if (kind == "trigger" && text[text.Length - 1] == '%')
            {
                int sp = text.LastIndexOf(' ');
                if (sp > 0) text = text.Substring(0, sp);
            }
            if (kind == "face" || kind == "shoulder" || kind == "trigger" || kind == "menu")
                return text == GpPartLabelWithStyle(b, "xbox")
                    || text == GpPartLabelWithStyle(b, "ps")
                    || text == GpPartLabelWithStyle(b, "switch");
            return false;
        }

        // 显示名（DisplayName_<名> 优先，沿用既有改名机制）——无覆盖值时返回 null
        private static string GpDisplayNameOrNull(string name, Windows.Foundation.Collections.IPropertySet vals)
        {
            try
            {
                if (vals == null) return null;
                string s = vals["DisplayName_" + name] as string;
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
        }

        // 逐个添加一个手柄部件（可被外部调用；行为与键盘自定义键一致）：
        // 参数可写 "Gp_A" 或简写 "A"；已存在则只按当前模式刷新显隐，并清掉删除记录（= 重新显示被删的）；
        // 不存在则在面板空白处落位并持久化（走既有 AddCustomKey 创建链）。未识别的名字只记 DiagLog，绝不抛异常。
        public void AddGamepadPart(string partName)
        {
            try
            {
                string name = GpNormalizePartName(partName);
                if (name == null)
                {
                    DiagLog("gamepad part add ignored: " + (partName == null ? "(null)" : partName));
                    return;
                }
                ApplicationData.Current.LocalSettings.Values.Remove("Deleted_" + name);
                if (_customKeys.ContainsKey(name))
                {
                    DiagLog("gamepad part already exists: " + name);
                }
                else
                {
                    AddCustomKey(name);
                    DiagLog("gamepad part added: " + name);
                }
                ApplyGamepadCustomParts(_latest);
            }
            catch (Exception ex) { DiagLog("gamepad part add fail: " + ex.Message); }
        }

        // 部件名归一：补 Gp_ 前缀、大小写不敏感匹配 20 个部件清单；未识别返回 null
        private static string GpNormalizePartName(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return null;
            string s = partName.Trim();
            if (s.Length == 0) return null;
            if (!s.StartsWith(GpPartPrefix, StringComparison.OrdinalIgnoreCase)) s = GpPartPrefix + s;
            for (int i = 0; i < GpParts.Length; i++)
                if (string.Equals(GpParts[i], s, StringComparison.OrdinalIgnoreCase)) return GpParts[i];
            return null;
        }

        // 是否存在手柄部件自定义键（未添加任何部件时，渲染循环的整段更新立即返回）
        private bool HasGpCustomParts()
        {
            foreach (var kv in _customKeys) if (IsGpPartName(kv.Key)) return true;
            return false;
        }

        // 部件是否可见：GamepadLayout_ + GamepadMode_（0 关闭 / 1 自动=检测到已连接手柄 / 2 始终显示）。
        // 注意：GamepadParts_（组合面板的部件位掩码）只作用于组合面板；自定义部件由"是否已添加"决定，不受它影响。
        private bool GpCustomPartsVisible(InputSnapshot snap)
        {
            if (_gpLayout != "custom") return false;   // 组合模式：部件一律不显示，组合面板照旧
            if (_gpMode == 0) return false;
            if (_gpMode == 2) return true;             // 始终显示：没有数据时也显示静息态
            if (snap == null || !snap.HasGamepad) return false;
            if (snap.GamepadConnected == 0) return false;
            if (_gpSlot >= 0 && _gpSlot <= 3 && (snap.GamepadConnected & (1 << _gpSlot)) == 0) return false;
            return true;
        }

        // 逐帧更新全部手柄部件（在 ApplyGamepadState 之外独立运行，与组合面板互不影响）。
        // 无部件时立即返回 → 默认（从未添加过部件）行为与改动前完全一致；整段自带 try/catch，异常只记 DiagLog。
        private void ApplyGamepadCustomParts(InputSnapshot snap)
        {
            try
            {
                if (!HasGpCustomParts()) return;
                bool show = GpCustomPartsVisible(snap);
                Windows.Foundation.Collections.IPropertySet vals = null;
                try { vals = ApplicationData.Current.LocalSettings.Values; } catch { }
                foreach (var kv in _customKeys)
                {
                    if (!IsGpPartName(kv.Key)) continue;
                    Border b = kv.Value;
                    if (b == null) continue;
                    // 删除记录（Deleted_Gp_*）优先：被标记为已删的部件不显示（重新添加时由 AddGamepadPart 清掉）
                    bool marked = false;
                    try { marked = vals != null && vals["Deleted_" + kv.Key] != null; } catch { }
                    if (!show || marked)
                    {
                        GpSetVisible(b, false);
                        continue;
                    }
                    if (ReferenceEquals(b, _moveKey))
                    {
                        GpSetVisible(b, true);   // 移动高亮不被快照重绘覆盖（与既有自定义键同一处理）
                        continue;
                    }
                    GpSetVisible(b, true);
                    GpRenderPart(b, kv.Key, snap, vals);
                }
            }
            catch (Exception ex) { DiagLog("gamepad custom parts fail: " + ex.Message); }
        }

        // 建立/校正部件内容：扳机与摇杆用 Canvas 承载（填充条 / 中心点），圆环与方块由 Border 自身承担（无内容），
        // 文字类部件沿用自定义键自带的 TextBlock（改名机制可直接作用于它）。幂等，可重复调用。
        private void GpBuildPartContent(Border b, string name)
        {
            if (b == null) return;
            string kind = GpPartKind(name);
            if (kind == "dpad" || kind == "guide")
            {
                if (b.Child != null) b.Child = null;   // 形状由 Border 自身（圆角/描边）承担
                return;
            }
            if (kind != "trigger" && kind != "stick" && kind != "thumb")
            {
                GpEnsureTextChild(b);
                return;
            }
            var old = b.Child as Canvas;
            if (old != null && (old.Tag as string) == kind) return;   // 已是目标结构
            var cv = new Canvas
            {
                IsHitTestVisible = false,   // 指针事件落到 Border 本身，拖拽/缩放/吸附不受影响
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Tag = kind
            };
            if (kind == "trigger")
            {
                cv.Children.Add(new Rectangle());   // 从底向上的填充
                cv.Children.Add(new TextBlock
                {
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }
            else
            {
                cv.Children.Add(new Ellipse());     // 摇杆点 / 中心点
            }
            b.Child = cv;
        }

        // 文字类部件：确保有 TextBlock 子元素（AddCustomKey 已建，此处兜底复制副本/异常情况）
        private static void GpEnsureTextChild(Border b)
        {
            if (b == null) return;
            if (b.Child is TextBlock) return;
            b.Child = new TextBlock
            {
                Text = "",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
        }

        // 按部件类型渲染一个 Gp_ 自定义键（全部走"值未变不写依赖属性"的幂等写入，与既有渲染循环一致，可每帧调用）。
        // 形状全部按 Border 当前尺寸换算 → 用户拖动/四角缩放后自动跟随；配色只用既有语义画刷方法。
        private void GpRenderPart(Border b, string name, InputSnapshot snap, Windows.Foundation.Collections.IPropertySet vals)
        {
            if (b == null) return;
            string kind = GpPartKind(name);
            double w = b.Width; if (double.IsNaN(w) || w <= 0) w = b.ActualWidth;
            double h = b.Height; if (double.IsNaN(h) || h <= 0) h = b.ActualHeight;
            if (w <= 0) w = 40;
            if (h <= 0) h = 40;
            double iw = w - 2.0, ih = h - 2.0;   // BorderThickness = 1：内容区
            if (iw < 4.0) iw = w;
            if (ih < 4.0) ih = h;
            uint btn = snap != null ? snap.GamepadButtons : 0;
            string disp = GpDisplayNameOrNull(name, vals);
            if (disp != null && GpIsLiveLabel(name, kind, disp)) disp = null;   // 预设回写的"当时的可见文本"不算改名

            switch (kind)
            {
                case "face":
                    {
                        bool down = (btn & GpFaceMask(GpBaseName(name))) != 0;
                        GpSetCornerRadius(b, Math.Min(w, h) / 2.0);   // 圆角取半边长 = 圆（非正方时是胶囊）
                        var tb = b.Child as TextBlock;
                        if (tb != null)
                        {
                            GpSetText(tb, disp != null ? disp : GpPartDefaultLabel(name));
                            GpSetPartFont(tb, GpFitFont(w, h, 0.5));
                        }
                        GpSetFaceKey(b, down);   // 既有面位配色：底/字对调 + 品牌色（GamepadBrand_ 开启且未按下）
                        return;
                    }
                case "shoulder":
                    {
                        bool down = (btn & (GpBaseName(name) == "Gp_LB" ? GP_LEFT_SHOULDER : GP_RIGHT_SHOULDER)) != 0;
                        GpSetCornerRadius(b, Math.Min(w, h) / 2.0);
                        var tb = b.Child as TextBlock;
                        if (tb != null)
                        {
                            GpSetText(tb, disp != null ? disp : GpPartDefaultLabel(name));
                            GpSetPartFont(tb, GpFitFont(w, h, 0.45));
                        }
                        SetKey(b, down);
                        return;
                    }
                case "menu":
                    {
                        bool down = (btn & (GpBaseName(name) == "Gp_View" ? GP_BACK : GP_START)) != 0;
                        GpSetCornerRadius(b, Math.Min(w, h) / 2.0);
                        var tb = b.Child as TextBlock;
                        if (tb != null)
                        {
                            GpSetText(tb, disp != null ? disp : GpPartDefaultLabel(name));
                            GpSetPartFont(tb, GpFitFont(w, h, 0.42));
                        }
                        SetKey(b, down);
                        return;
                    }
                case "dpad":
                    {
                        uint mask = GpDpadMask(GpBaseName(name));
                        GpSetCornerRadius(b, Math.Min(w, h) / 4.0);   // 小圆角方块（不做十字合体）
                        SetKey(b, (btn & mask) != 0);
                        return;
                    }
                case "guide":
                    {
                        bool down = (btn & GP_GUIDE) != 0;
                        GpSetCornerRadius(b, Math.Min(w, h) / 2.0);
                        var bg = down ? PressBgB() : _transparent;   // 按下时圆环用按下底色填充（同组合模式 GpSetGuide）
                        if (!ReferenceEquals(b.Background, bg)) b.Background = bg;
                        GpSetPartBorder(b, BorderB());
                        return;
                    }
                case "battery":
                    {
                        int lv = snap != null ? snap.GamepadBattery : 0;
                        GpEnsureTextChild(b);
                        var tb = b.Child as TextBlock;
                        if (tb != null)
                        {
                            GpSetText(tb, disp != null ? disp
                                : lv == 1 ? "\u7535\u91cf \u4f4e"      // 电量 低
                                : lv == 2 ? "\u7535\u91cf \u4e2d"      // 电量 中
                                : lv == 3 ? "\u7535\u91cf \u6ee1"      // 电量 满
                                : "");
                            GpSetPartFont(tb, GpFitFont(w, h, 0.5));
                            var fg = KeyFgB();
                            if (!ReferenceEquals(tb.Foreground, fg)) tb.Foreground = fg;
                        }
                        if (!ReferenceEquals(b.Background, _transparent)) b.Background = _transparent;
                        GpSetPartBorder(b, _transparent);   // 只显示文字，不画键帽底/描边
                        return;
                    }
                case "trigger":
                    {
                        bool left = GpBaseName(name) == "Gp_LT";
                        int val = snap != null ? (left ? snap.GamepadLT : snap.GamepadRT) : 0;
                        if (val < 0) val = 0;
                        if (val > 255) val = 255;
                        bool highlight = _gpTrigger == "highlight";
                        bool lit = val > GpTriggerHighlightThreshold;
                        GpSetCornerRadius(b, Math.Min(w, h) / 3.0);   // 竖向圆角条（角半径与组合模式同比例）
                        GpSetPartBorder(b, BorderB());
                        var barBg = highlight && lit ? PressBgB() : KeyBgB();
                        if (!ReferenceEquals(b.Background, barBg)) b.Background = barBg;
                        GpBuildPartContent(b, name);
                        var cv = b.Child as Canvas;
                        if (cv == null) return;
                        GpSetSize(cv, iw, ih);
                        var rect = cv.Children.Count > 0 ? cv.Children[0] as Rectangle : null;
                        var tb = cv.Children.Count > 1 ? cv.Children[1] as TextBlock : null;
                        double fillH = 0;
                        if (highlight)
                        {
                            if (rect != null)   // 仅高亮：不填充（整条换色，同组合模式）
                            {
                                GpSetSize(rect, iw, 0);
                                GpSetPos(rect, 0, ih);
                            }
                        }
                        else
                        {
                            fillH = ih * (val / 255.0);
                            if (rect != null)
                            {
                                var accent = AccentB();
                                if (!ReferenceEquals(rect.Fill, accent)) rect.Fill = accent;
                                GpSetSize(rect, iw, fillH);
                                GpSetPos(rect, 0, ih - fillH);
                            }
                        }
                        if (tb != null)
                        {
                            double fs = GpFitFont(w, h, 0.5);
                            GpSetPartFont(tb, fs);
                            string tn = disp != null ? disp : GpTriggerName(left);
                            GpSetText(tb, _gpTrigger == "value" ? tn + " " + (int)(val / 2.55) + "%" : tn);
                            GpSetWidth(tb, iw);
                            GpSetPos(tb, 0, 1);
                            // 文字在条内顶部：填充涨到文字后面时改用按下字色，保证任何主题下都读得清
                            bool covered = fillH > ih - fs * 1.4 - 1.0;
                            var fg = highlight ? (lit ? PressFgB() : KeyFgB()) : (covered ? PressFgB() : KeyFgB());
                            if (!ReferenceEquals(tb.Foreground, fg)) tb.Foreground = fg;
                        }
                        return;
                    }
                case "stick":
                case "thumb":
                    {
                        bool left = GpBaseName(name) == "Gp_LS" || GpBaseName(name) == "Gp_L3";
                        bool down = (btn & (left ? GP_LEFT_THUMB : GP_RIGHT_THUMB)) != 0;
                        double nx = 0.0, ny = 0.0;
                        if (kind == "stick" && snap != null)
                        {
                            // 死区 + 径向钳制：复用组合模式同一套算法
                            GpStickNormalize(left ? snap.GamepadLX : snap.GamepadRX,
                                             left ? snap.GamepadLY : snap.GamepadRY,
                                             _gpDeadzone, out nx, out ny);
                        }
                        GpSetCornerRadius(b, Math.Min(w, h) / 2.0);   // 圆环（Border 自身描边）
                        var ringBg = (kind == "thumb" && down) ? PressBgB() : _transparent;
                        if (!ReferenceEquals(b.Background, ringBg)) b.Background = ringBg;
                        GpSetPartBorder(b, BorderB());
                        GpBuildPartContent(b, name);
                        var cv = b.Child as Canvas;
                        if (cv == null) return;
                        GpSetSize(cv, iw, ih);
                        var dot = cv.Children.Count > 0 ? cv.Children[0] as Ellipse : null;
                        if (dot == null) return;
                        double dotR = Math.Min(iw, ih) / 6.0;   // 与组合模式同比例：点半径 = 环半径 / 3
                        if (dotR < 2.5) dotR = 2.5;
                        double travelX = iw / 2.0 - dotR;
                        double travelY = ih / 2.0 - dotR;
                        if (travelX < 0) travelX = 0;
                        if (travelY < 0) travelY = 0;
                        GpSetWidth(dot, dotR * 2.0);
                        GpSetHeight(dot, dotR * 2.0);
                        // 点圆心 = 环心 + (nx, -ny) × 可移动半径（协议 Y 轴向上为正，画布 Y 向下）
                        GpSetPos(dot, iw / 2.0 - dotR + nx * travelX, ih / 2.0 - dotR - ny * travelY);
                        // L3/R3 按下时用"鼠标点按下色"（与组合模式摇杆点的处理一致）
                        var df = (kind == "thumb" && down) ? DotPressedB() : DotB();
                        if (!ReferenceEquals(dot.Fill, df)) dot.Fill = df;
                        return;
                    }
                default:
                    {
                        // 非清单内的 Gp_ 名（例如用户自建）：按普通自定义键渲染，绝不抛异常
                        SetKey(b, false);
                        var tb = b.Child as TextBlock;
                        if (tb != null)
                        {
                            GpSetText(tb, disp != null ? disp : GpBaseName(name));
                            GpSetPartFont(tb, GpFitFont(w, h, 0.42));
                        }
                        return;
                    }
            }
        }

        // ABXY → XInput 掩码（下/右/左/上 = A/B/X/Y）
        private static uint GpFaceMask(string baseName)
        {
            switch (baseName)
            {
                case "Gp_A": return GP_A;
                case "Gp_B": return GP_B;
                case "Gp_X": return GP_X;
                case "Gp_Y": return GP_Y;
            }
            return 0;
        }

        // 十字键方向 → XInput 掩码
        private static uint GpDpadMask(string baseName)
        {
            switch (baseName)
            {
                case "Gp_DpadUp": return GP_DPAD_UP;
                case "Gp_DpadDown": return GP_DPAD_DOWN;
                case "Gp_DpadLeft": return GP_DPAD_LEFT;
                case "Gp_DpadRight": return GP_DPAD_RIGHT;
            }
            return 0;
        }

        // 部件标签字号：随部件尺寸缩放（夹在 6~30，避免拖到极小/极大时字不可读或撑破）
        private static double GpFitFont(double w, double h, double ratio)
        {
            double s = Math.Min(w, h) * ratio;
            if (s < 6.0) s = 6.0;
            if (s > 30.0) s = 30.0;
            return s;
        }

        // 部件标签字号/字重（字重跟随"字体粗细"设置；字体族在创建时已定，不在此每帧重建，避免无谓分配）
        private void GpSetPartFont(TextBlock tb, double size)
        {
            if (tb == null) return;
            if (double.IsNaN(tb.FontSize) || Math.Abs(tb.FontSize - size) > 0.01) tb.FontSize = size;
            if (tb.FontWeight.Weight != _keyFontWeight.Weight) tb.FontWeight = _keyFontWeight;
        }

        // 幂等圆角（值未变不写依赖属性）
        private static void GpSetCornerRadius(Border b, double r)
        {
            if (b == null) return;
            if (r < 0.0) r = 0.0;
            var c = b.CornerRadius;
            if (Math.Abs(c.TopLeft - r) > 0.01 || Math.Abs(c.TopRight - r) > 0.01 ||
                Math.Abs(c.BottomRight - r) > 0.01 || Math.Abs(c.BottomLeft - r) > 0.01)
                b.CornerRadius = new CornerRadius(r, r, r, r);
        }

        // 幂等宽度（值未变不写依赖属性；NaN 视为需要写入）
        private static void GpSetWidth(FrameworkElement el, double w)
        {
            if (el == null) return;
            double cur = el.Width;
            if (double.IsNaN(cur) || Math.Abs(cur - w) > 0.01) el.Width = w;
        }

        // 幂等宽高
        private static void GpSetSize(FrameworkElement el, double w, double h)
        {
            GpSetWidth(el, w);
            GpSetHeight(el, h);
        }

        // 幂等 Canvas 坐标（值未变不写依赖属性）
        private static void GpSetPos(FrameworkElement el, double x, double y)
        {
            if (el == null) return;
            double cur = Canvas.GetLeft(el);
            if (double.IsNaN(cur) || Math.Abs(cur - x) > 0.01) Canvas.SetLeft(el, x);
            cur = Canvas.GetTop(el);
            if (double.IsNaN(cur) || Math.Abs(cur - y) > 0.01) Canvas.SetTop(el, y);
        }

        // 部件边框色（多选选中态保持固定红框，与 SetKey 的处理一致）
        private void GpSetPartBorder(Border b, Brush br)
        {
            if (b == null) return;
            if (IsKeySelected(b)) return;
            if (!ReferenceEquals(b.BorderBrush, br)) b.BorderBrush = br;
        }

        // ===================== 1.4：键位风格自动检测（GamepadStyle_ = "auto"）=====================
        // 向管道发 CMD|PAD，应答 RESP|DATA|{"brand":"xbox","vid":"0x045E","pid":"0x0B13"}；只取 brand。
        // 限频（已应答 10s / 无应答 60s）+ 单飞（_gpAutoBusy）避免频繁发命令；超时/解析失败/unknown
        // 一律静默按 xbox 回落，整段 try/catch：手柄侧任何异常都只记 DiagLog，绝不影响键鼠渲染。
        private async void TryStartGamepadBrandDetect()
        {
            try
            {
                if (_gpStyleRaw != "auto") return;     // 只有 auto 才查询
                if (_gpAutoBusy) return;               // 单飞：上一轮还没回来就不重复发
                var reader = _reader;
                if (reader == null || !reader.Connected) return;   // 还没连上：不记时刻，下一轮再试（连上即检测）
                long now = DateTime.UtcNow.Ticks;
                // 限频：接收器已应答（含 unknown）→ 10s；一直没应答（旧接收器/未实现 PAD）→ 60s，
                // 免得隔一会儿就往管道塞一条没人接的命令。
                long wait = _gpAutoAnswered ? TimeSpan.FromSeconds(10).Ticks : TimeSpan.FromSeconds(60).Ticks;
                if (_gpAutoLastTicks != 0 && now - _gpAutoLastTicks < wait) return;
                _gpAutoLastTicks = now;
                string resp = null;   // 显式初始化：即使 await 抛异常也不会走到下面用到未赋值变量
                _gpAutoBusy = true;
                try { resp = await reader.RequestPresetAsync("PAD", null, 800); }
                finally { _gpAutoBusy = false; }
                if (resp != null) _gpAutoAnswered = true;
                string brand = GpParseBrand(resp);
                if (brand == null)
                {
                    // 超时 / ERR| / unknown / JSON 不合法：保持"按 xbox"的回落（默认风格就是 xbox），不动界面
                    DiagLog("gamepad brand detect: fallback xbox");
                    return;
                }
                if (_gpAutoBrand == brand) return;     // 结果没变：不动界面
                _gpAutoBrand = brand;
                DiagLog("gamepad brand detect: " + brand);
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        if (_gpStyleRaw != "auto") return;   // 等待期间用户可能已改成固定风格
                        if (_gpStyle == brand) return;
                        _gpStyle = brand;
                        ApplyGamepadSettings();   // 字形 + 配色 + 部件显隐 + 模板一并重刷
                    }
                    catch (Exception ex) { DiagLog("gamepad brand apply fail: " + ex.Message); }
                });
            }
            catch (Exception ex) { DiagLog("gamepad brand detect fail: " + ex.Message); }
        }

        // 解析 CMD|PAD 的应答：只认 xbox / ps（playstation）/ switch（nintendo）；
        // "unknown"、字段缺失、JSON 不合法、应答是 OK|ERR|、超时（null）一律返回 null（调用方按 xbox 处理）。
        private static string GpParseBrand(string resp)
        {
            try
            {
                if (string.IsNullOrEmpty(resp)) return null;
                string body = resp.StartsWith("DATA|", StringComparison.Ordinal) ? resp.Substring(5) : resp;
                Windows.Data.Json.JsonObject o;   // 全限定名：与设置窗口里同一套解析写法，避免类型名歧义
                if (!Windows.Data.Json.JsonObject.TryParse(body, out o) || o == null) return null;
                if (!o.ContainsKey("brand")) return null;
                string b = (o.GetNamedString("brand", "") ?? "").Trim().ToLowerInvariant();
                if (b == "xbox") return "xbox";
                if (b == "ps" || b == "playstation") return "ps";
                if (b == "switch" || b == "nintendo") return "switch";
                return null;
            }
            catch { return null; }
        }

        // ===================== 1.4：组合模式的手柄轮廓模板（纯描边背景）=====================
        // 画在 GamepadTemplatePanel 里（GamepadPanel 的第一个子元素 → 位于所有部件之下），坐标与组合模式
        // 部件同一套设计单位 → 按键正好落在模板上对应的位置。只用 BorderB() 描边、透明填充，不新增颜色常量。
        // custom 布局不画任何模板（整块 Collapsed）；模板与 GamepadParts_ 部件位掩码无关（那是部件的显隐）。
        private void ApplyGamepadTemplate()
        {
            try
            {
                if (GamepadTemplatePanel == null) return;
                if (_gpLayout == "custom")
                {
                    GpSetVisible(GamepadTemplatePanel, false);   // 自定义模式：没有任何背景模板
                    return;
                }
                GpSetVisible(GamepadTemplatePanel, true);
                string tpl = GpTemplateStyle();
                if (_gpTemplateStyle != tpl)
                {
                    BuildGamepadTemplate(tpl);
                    _gpTemplateStyle = tpl;
                }
                // 配色随主题刷新（值未变不写依赖属性）
                var bd = BorderB();
                for (int i = 0; i < GamepadTemplatePanel.Children.Count; i++)
                {
                    var sh = GamepadTemplatePanel.Children[i] as Shape;
                    if (sh == null) continue;
                    if (!ReferenceEquals(sh.Stroke, bd)) sh.Stroke = bd;
                    if (!ReferenceEquals(sh.Fill, _transparent)) sh.Fill = _transparent;
                    if (Math.Abs(sh.StrokeThickness - 1.0) > 0.001) sh.StrokeThickness = 1.0;
                }
            }
            catch (Exception ex) { DiagLog("gamepad template fail: " + ex.Message); }
        }

        // 模板风格 = 当前生效的键位风格（"auto" 已在 _gpStyle 里落成 xbox/ps/switch）
        private string GpTemplateStyle()
        {
            return (_gpStyle == "ps" || _gpStyle == "switch") ? _gpStyle : "xbox";
        }

        // 建立模板轮廓（只在风格变化时重建一次；元素全部纯描边）
        private void BuildGamepadTemplate(string tpl)
        {
            var cv = GamepadTemplatePanel;
            if (cv == null) return;
            cv.Children.Clear();
            bool ps = tpl == "ps", sw = tpl == "switch";
            // ---- 机身 + 左右握把（只需近似：一眼能看出是手柄，不做写实）----
            if (ps)
            {
                AddTplRound(cv, 14, 4, 140, 96, 34);        // PlayStation：机身较窄长
                AddTplEllipse(cv, 28, 80, 24, 30);
                AddTplEllipse(cv, 140, 80, 24, 30);
            }
            else if (sw)
            {
                AddTplRound(cv, 10, 6, 148, 88, 26);        // Switch：机身偏方
                AddTplEllipse(cv, 24, 78, 22, 28);
                AddTplEllipse(cv, 144, 78, 22, 28);
            }
            else
            {
                AddTplRound(cv, 4, 4, 164, 92, 28);         // Xbox：机身较宽
                AddTplEllipse(cv, 26, 78, 26, 30);
                AddTplEllipse(cv, 142, 78, 26, 30);
            }
            // ---- 顶部：肩键 / 扳机的小方块轮廓（与 LB/RB、LT/RT 部件同位）----
            AddTplRound(cv, 22, 6, 30, 18, ps ? 6 : 5);     // LB / L1 / L
            AddTplRound(cv, 116, 6, 30, 18, ps ? 6 : 5);    // RB / R1 / R
            AddTplRound(cv, 4, 0, 16, 34, ps ? 8 : 5);      // LT / L2 / ZL 竖条
            AddTplRound(cv, 148, 0, 16, 34, ps ? 8 : 5);    // RT / R2 / ZR 竖条
            // ---- 两个摇杆圆环（与 LS/RS 同心，环半径略大于部件环 → 把按键圈在里面）----
            AddTplEllipse(cv, 34, 44, 18, 18);              // 左摇杆
            AddTplEllipse(cv, 126, 88, 18, 18);             // 右摇杆
            // ---- 十字键：Xbox 一体十字 / PlayStation 四个小方块 / Switch 四个圆点 ----
            if (ps)
            {
                // 四个小方块（各自框住一个方向键，块与块只在内角轻触，不互相压线）
                AddTplRound(cv, 27, 63, 14, 14, 4);
                AddTplRound(cv, 27, 90, 14, 14, 4);
                AddTplRound(cv, 13, 76, 14, 14, 4);
                AddTplRound(cv, 40, 76, 14, 14, 4);
            }
            else if (sw)
            {
                AddTplEllipse(cv, 34, 71, 7, 7);
                AddTplEllipse(cv, 34, 97, 7, 7);
                AddTplEllipse(cv, 21, 84, 7, 7);
                AddTplEllipse(cv, 47, 84, 7, 7);
            }
            else
            {
                AddTplRound(cv, 26, 63, 16, 40, 4);         // 十字：竖
                AddTplRound(cv, 14, 75, 40, 16, 4);         // 十字：横
            }
            // ---- ABXY：Xbox 菱形 / PlayStation、Switch 四个圆 ----
            if (ps || sw)
            {
                AddTplEllipse(cv, 126, 35, 12, 12);
                AddTplEllipse(cv, 113, 48, 12, 12);
                AddTplEllipse(cv, 139, 48, 12, 12);
                AddTplEllipse(cv, 126, 61, 12, 12);
            }
            else
            {
                AddTplDiamond(cv, 126, 48, 26, 26);
            }
            // ---- View / Menu / Guide ----
            AddTplRound(cv, 54, 76, 22, 17, 5);
            AddTplRound(cv, 76, 76, 22, 17, 5);
            AddTplEllipse(cv, 84, 56, 13, 13);
        }

        // 模板圆角矩形（左上角 x/y + 宽高 + 圆角）
        private void AddTplRound(Canvas cv, double x, double y, double w, double h, double r)
        {
            try
            {
                var rc = new Rectangle
                {
                    Width = w,
                    Height = h,
                    RadiusX = r,
                    RadiusY = r,
                    Stroke = BorderB(),
                    StrokeThickness = 1,
                    Fill = _transparent
                };
                Canvas.SetLeft(rc, x);
                Canvas.SetTop(rc, y);
                cv.Children.Add(rc);
            }
            catch (Exception ex) { DiagLog("gamepad template round fail: " + ex.Message); }
        }

        // 模板椭圆/圆（圆心 + 半径）
        private void AddTplEllipse(Canvas cv, double cx, double cy, double rx, double ry)
        {
            try
            {
                var el = new Ellipse
                {
                    Width = rx * 2.0,
                    Height = ry * 2.0,
                    Stroke = BorderB(),
                    StrokeThickness = 1,
                    Fill = _transparent
                };
                Canvas.SetLeft(el, cx - rx);
                Canvas.SetTop(el, cy - ry);
                cv.Children.Add(el);
            }
            catch (Exception ex) { DiagLog("gamepad template ellipse fail: " + ex.Message); }
        }

        // 模板菱形（Xbox 的 ABXY 菱形轮廓）：用 4 条 Line 拼（Line 在本文件已用于吸附参考线，
        // 不依赖旋转 RenderTransform，也不引入 Polygon/PointCollection 这类元数据不确定的类型）
        private void AddTplDiamond(Canvas cv, double cx, double cy, double rx, double ry)
        {
            try
            {
                AddTplLine(cv, cx, cy - ry, cx + rx, cy);
                AddTplLine(cv, cx + rx, cy, cx, cy + ry);
                AddTplLine(cv, cx, cy + ry, cx - rx, cy);
                AddTplLine(cv, cx - rx, cy, cx, cy - ry);
            }
            catch (Exception ex) { DiagLog("gamepad template diamond fail: " + ex.Message); }
        }

        private void AddTplLine(Canvas cv, double x1, double y1, double x2, double y2)
        {
            var ln = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = BorderB(), StrokeThickness = 1 };
            cv.Children.Add(ln);
        }

        // 幂等显隐（值未变不写依赖属性）
        private static void GpSetVisible(UIElement el, bool visible)
        {
            if (el == null) return;
            var want = visible ? Visibility.Visible : Visibility.Collapsed;
            if (el.Visibility != want) el.Visibility = want;
        }

        // 幂等文字（值未变不写依赖属性）
        private static void GpSetText(TextBlock tb, string text)
        {
            if (tb == null) return;
            if (tb.Text != text) tb.Text = text;
        }

        // 幂等高度（值未变不写依赖属性；NaN 视为需要写入）
        private static void GpSetHeight(FrameworkElement el, double h)
        {
            if (el == null) return;
            double cur = el.Height;
            if (double.IsNaN(cur) || Math.Abs(cur - h) > 0.01) el.Height = h;
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
