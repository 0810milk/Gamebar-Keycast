// SettingsPage.xaml.cs —— 0.9.5 独立的设置子窗口（Game Bar 官方 settings widget）
//
// 数据模型：与主小组件共享同一个 ApplicationData.LocalSettings（同一 msix 包 → 同一容器）：
//   主题        Theme          = dark / gray / light / pink / blue / custom
//   自定义色     CustomPanel_ / CustomBorder_ / CustomKeyBg_ / CustomKeyFg_ /
//                CustomPressedBg_ / CustomPressedFg_ / CustomPad_ / CustomDot_（#AARRGGBB）
//   字体        KeyFontTag_（"system" 或字体名） / KeyFontSize_（8..30，界面号数 = 实际 − 1） / KeyFontWeight_（1..10，越右越粗）
//   透明度      KeyOpacity_   (10..100)
//   鼠标垫      PadVisible_   (1/0)
//   锁定        LayoutLocked  (bool)
//
// 写入后调用 ApplicationData.Current.SignalDataChanged()，主小组件订阅 DataChanged 后实时重载，
// 因此无需关闭窗口即可看到按键区变化。

using System;
using System.Globalization;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace KeyDisplay
{
    public sealed partial class SettingsPage : Page
    {
        private static readonly string[] CustomKeys = {
            "CustomPanel_", "CustomBorder_", "CustomKeyBg_", "CustomKeyFg_",
            "CustomPressedBg_", "CustomPressedFg_", "CustomPad_", "CustomDot_",
            "CustomAccent_", "CustomDotPressed_" };   // 0.9.5：第 9 槽强调色、第 10 槽「鼠标点·按下色」

        // 颜色页的显示顺序：面板 → 强调色 → 边框 → …（强调色在数组里是索引 8）
        private static readonly int[] SlotOrder = { 0, 8, 1, 2, 3, 4, 5, 6, 7, 9 };   // 0.9.5：末尾追加「鼠标点按下」

        private sealed class Palette
        {
            public bool Dark;
            public Color Bg;         // 窗口背景
            public Color Card;       // 卡片背景
            public Color Card2;      // 按钮 / 输入框背景
            public Color Border;     // 分隔线/边框
            public Color Text;       // 主文字
            public Color Subtle;     // 次要文字
            public Color Accent;     // 选中强调
            public Color AccentFg;
        }

        // 五个内置主题的 9 槽预设色（与主小组件 Widget1.xaml.cs 的预设画刷逐位一致：
        // 面板/边框/按键底/按键字/按下底/按下字/鼠标垫/鼠标点/强调色）
        private static readonly string[][] ThemeSlotHex = {
            new[] { "#E8121212", "#52FFFFFF", "#F21A1A1A", "#FFFFFFFF", "#FFFFFFFF", "#FF101010", "#4D000000", "#FFFFFFFF", "#FF4CC2FF", "#FF4CC2FF" }, // dark
            new[] { "#E0CFCFCF", "#5C5A5A5A", "#FFEAEAEA", "#FF1A1A1A", "#FF4A4A4A", "#FFFFFFFF", "#47000000", "#FF1A1A1A", "#FF0067C0", "#FF0067C0" }, // gray
            new[] { "#E0F5F5F5", "#59333333", "#F2FFFFFF", "#FF000000", "#FF000000", "#FFFFFFFF", "#42000000", "#F21A1A1A", "#FF0067C0", "#FF0067C0" }, // light
            new[] { "#E0FFB3C6", "#CCB0577E", "#FFFFB3C6", "#FFFFFFFF", "#FFFFFFFF", "#FFB0577E", "#4DFFB3C6", "#FFB0577E", "#FFC2185B", "#FFC2185B" }, // pink
            new[] { "#E0C3DCF0", "#663A6EA5", "#FFD2E5F7", "#FF1F4E79", "#FFFFFFFF", "#FF1F4E79", "#59BFD9EE", "#FF1F4E79", "#FF0A64B4", "#FF0A64B4" }, // blue
        };

        private Palette _pal;
        private bool _loaded;

        public SettingsPage()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) =>
            {
                try
                {
                    _lastThemeSeen = CurrentTheme();
                    BuildPalette();
                    LoadFromSettings();
                    ApplyPalette();
                    ShowSection("theme");
                    SetAboutVersion();   // 0.9.5：关于页版本号
                    LoadAvatar();        // 0.9.5：关于页作者头像
                    // 0.9.5：鼠标光标按键的键盘捕获挂在页面级（Border 不能聚焦）：只要处于捕获态，按下的键即被识别
                    try { this.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(DotKeyCapture_KeyDown), true); } catch { }
                    HookStore();         // 0.9.5：主窗口改主题时本窗口同步
                    // 0.9.5：窗口尺寸变化时只重算调色盘的「并排 / 换行」位置（缩放由 Viewbox 自动完成，开销极小）
                    RootGrid.SizeChanged += (s2, e2) => ApplyPickerScale();
                    _ = InitPresetsAsync();   // 0.9.5：读取预设列表 + 首次种入出厂默认预设「默认」（异步，失败静默）
                    _loaded = true;
                    Diag("settings page loaded");
                }
                catch (Exception ex) { Diag("settings load fail: " + ex.Message); }
            };
            this.Unloaded += (s, e) =>
            {
                try { ApplicationData.Current.DataChanged -= Store_Changed; _storeHooked = false; } catch { }
                StopAdvPolling();
            };
        }

        // ===================== 配色 =====================

        private static Color Hex(string s, Color fallback)
        {
            var c = ParseHex(s);
            return c.HasValue ? c.Value : fallback;
        }

        private static Color? ParseHex(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            if (s.Length < 7 || s.Length > 9 || s[0] != '#') return null;
            byte a = 0xFF, r, g, b;
            int off = 0;
            if (s.Length == 9)
            {
                if (!byte.TryParse(s.Substring(1, 2), NumberStyles.HexNumber, null, out a)) return null;
                off = 2;
            }
            if (!byte.TryParse(s.Substring(1 + off, 2), NumberStyles.HexNumber, null, out r)) return null;
            if (!byte.TryParse(s.Substring(3 + off, 2), NumberStyles.HexNumber, null, out g)) return null;
            if (!byte.TryParse(s.Substring(5 + off, 2), NumberStyles.HexNumber, null, out b)) return null;
            return Color.FromArgb(a, r, g, b);
        }

        private string CurrentTheme()
        {
            var v = ApplicationData.Current.LocalSettings.Values;
            return (v["Theme"] as string) ?? "dark";
        }

        private static int ThemeIndex(string theme)
        {
            if (theme == "gray") return 1;
            if (theme == "light") return 2;
            if (theme == "pink") return 3;
            if (theme == "blue") return 4;
            return 0;   // dark / custom 未设值时的回落
        }

        // 0.9.5：这两个槽只作用于「设置窗口自身」的外观，不写小组件的 Custom_ 色槽键。
        // 不设置时自动跟随当前内置主题（黑/灰/白/粉/蓝）的同名默认色，切换主题即同步。
        private const string SettingsPanelKey = "SettingsPanel_";     // 设置窗口：面板底色
        private const string SettingsAccentKey = "SettingsAccent_";   // 设置窗口：强调高亮色

        private static bool IsSettingsScopeSlot(int k) { return k == 0 || k == 8; }

        private static string SettingsScopeKeyOf(int k) { return k == 0 ? SettingsPanelKey : SettingsAccentKey; }

        // 小组件侧该槽的色值（写 Custom_ 键时用）：prests 主题取内置预设，custom 取 Custom_ 键
        private string WidgetSlotHex(int k)
        {
            string theme = CurrentTheme();
            if (theme != "custom") return ThemeSlotHex[ThemeIndex(theme)][k];
            var s = ApplicationData.Current.LocalSettings.Values[CustomKeys[k]] as string;
            return string.IsNullOrEmpty(s) ? ThemeSlotHex[0][k] : s;
        }

        // 色槽当前显示值：
        //   0/8（面板/强调色）→ 设置窗口自己的覆盖值，没设过则跟随当前主题的内置默认色；
        //   其余 → 非 custom 主题取该主题内置预设色，custom 取 Custom_ 键（缺省回落深色预设）
        private string SlotText(int k)
        {
            string theme = CurrentTheme();
            if (IsSettingsScopeSlot(k))
            {
                var ov = ApplicationData.Current.LocalSettings.Values[SettingsScopeKeyOf(k)] as string;
                if (!string.IsNullOrEmpty(ov)) return ov;
                return WidgetSlotHex(k);   // 没设过覆盖值 → 跟随当前主题（custom 主题则取其 Custom_ 值）
            }
            if (theme != "custom")
            {
                var preset = ParseHex(ThemeSlotHex[ThemeIndex(theme)][k]);
                if (preset.HasValue) return ToHex(preset.Value);
            }
            var v = ApplicationData.Current.LocalSettings.Values;
            string s = v[CustomKeys[k]] as string;
            if (!string.IsNullOrEmpty(s)) return s;
            return ThemeSlotHex[0][k];
        }

        private static double Luma(Color c)
        {
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        }

        private static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromArgb(
                (byte)Math.Round(a.A + (b.A - a.A) * t),
                (byte)Math.Round(a.R + (b.R - a.R) * t),
                (byte)Math.Round(a.G + (b.G - a.G) * t),
                (byte)Math.Round(a.B + (b.B - a.B) * t));
        }

        private static Color Opaque(Color c) { return Color.FromArgb(0xFF, c.R, c.G, c.B); }

        // 0.9.5 重写：不再只分"深/浅两套写死的配色"，而是直接从用户当前主题的面板色推导——
        // 用户反馈"只有黑色主题下设置窗口能看，灰/白/粉/蓝都是全白啥也看不到"：
        // 原因是 Game Bar 请求的应用主题是深色（控件默认白字），而浅色面板上白字白底不可见。
        // 现在：①窗口底色/卡片/文字全部按面板色明暗推导；②整棵可视树上色；③同步 RequestedTheme，
        // 让按钮/滑条/输入框等原生控件的模板也切到正确的明暗。
        private void BuildPalette()
        {
            string theme = CurrentTheme();
            var panelHex = ParseHex(SlotText(0));   // 0 槽 = 面板色
            Color basePanel = Opaque(panelHex.HasValue ? panelHex.Value : Color.FromArgb(0xFF, 0x12, 0x12, 0x12));
            bool dark = Luma(basePanel) < 0.5;

            Color white = Colors.White, black = Colors.Black;
            _pal = new Palette
            {
                Dark = dark,
                Bg = dark ? Mix(basePanel, white, 0.06) : Mix(basePanel, black, 0.10),
                Card = dark ? Mix(basePanel, white, 0.13) : Mix(basePanel, white, 0.86),
                Card2 = dark ? Mix(basePanel, white, 0.20) : Mix(basePanel, white, 0.50),
                Border = dark ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00),
                Text = dark ? white : Color.FromArgb(0xFF, 0x15, 0x15, 0x15),
                Subtle = dark ? Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB4, 0x00, 0x00, 0x00)
            };
            // 0.9.5：强调色直接取「强调色」色槽（内置主题各有默认值；自定义主题取 CustomAccent_），
            // 主窗口的「设置」按钮与设置窗口内的选中高亮同时跟随它
            var acc = ParseHex(SlotText(8));
            _pal.Accent = acc.HasValue ? acc.Value : AccentOf(theme, dark);
            _pal.AccentFg = Luma(_pal.Accent) > 0.55 ? Color.FromArgb(0xFF, 0x10, 0x10, 0x10) : Colors.White;
        }

        private static Color AccentOf(string theme, bool dark)
        {
            switch (theme)
            {
                case "pink": return Color.FromArgb(0xFF, 0xC2, 0x18, 0x5B);
                case "blue": return Color.FromArgb(0xFF, 0x0A, 0x64, 0xB4);
                case "gray":
                case "light": return Color.FromArgb(0xFF, 0x00, 0x67, 0xC0);
                default: return dark ? Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF) : Color.FromArgb(0xFF, 0x00, 0x67, 0xC0);
            }
        }

        private static SolidColorBrush B(Color c) { return new SolidColorBrush(c); }

        // 整棵可视树统一上色：文字/按钮/输入框（Border 不碰，避免覆盖色槽预览与色块）
        private void PaintTree(DependencyObject root)
        {
            try
            {
                int n = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < n; i++)
                {
                    var child = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                    var tb = child as TextBlock;
                    if (tb != null)
                    {
                        tb.Foreground = B(_pal.Text);
                    }
                    else
                    {
                        var btn = child as Button;
                        if (btn != null)
                        {
                            btn.Background = B(_pal.Card2);
                            btn.BorderBrush = B(_pal.Border);
                            btn.Foreground = B(_pal.Text);
                            // 不再往按钮模板里递归：否则会把内容 TextBlock 的 Foreground 钉成"本地值"，
                            // 之后 ApplyThemeButtonStyles 设的 AccentFg（选中态文字色）就无法继承生效
                            continue;
                        }
                        var box = child as TextBox;
                        if (box != null)
                        {
                            box.Foreground = B(_pal.Text);
                            box.BorderBrush = B(_pal.Border);
                            box.Background = B(_pal.Card2);
                            continue;   // 同理：输入框内部文字继承外层 Foreground 即可
                        }
                    }
                    PaintTree(child);
                }
            }
            catch { }
        }

        private void ApplyPalette()
        {
            try { this.RequestedTheme = _pal.Dark ? ElementTheme.Dark : ElementTheme.Light; } catch { }

            RootGrid.Background = B(_pal.Bg);
            NavPane.Background = B(_pal.Bg);
            NavPane.BorderBrush = B(_pal.Border);

            PaintTree(RootGrid);

            foreach (var t in new TextBlock[] { PageDesc, ThemeCardDesc, ColorCardDesc, FontCardDesc, PresetCardDesc,
                                                AboutVerText, AboutDesc, AboutRepo, AuthorDescText,
                                                AddKeyHint, AboutTech, AdvSwitchDesc, AdvSpeedDesc })
            { t.Foreground = B(_pal.Subtle); }

            foreach (var card in new Border[] { ThemeCard, AdvSwitchCard, ColorCard, FontCard, LayoutCard, PresetCard, AboutCard })
            {
                card.Background = B(_pal.Card);
                card.BorderBrush = B(_pal.Border);
            }
            AvatarFrame.BorderBrush = B(_pal.Border);
            PresetStatus.Foreground = B(_pal.Accent);   // 预设页状态与反馈用强调色，保证一眼看到
            AddKeyToggle.Background = B(_pal.Card2);
            AddKeyToggle.BorderBrush = B(_pal.Border);

            // 字体项 / 导航选中态 / 色块 / 键盘 / 调色盘浮层由各自刷新方法处理
            ApplyThemeButtonStyles();
            ApplyFontItemStyles();
            ApplyNavSelection();
            BuildSlotRows();
            RefreshSlotRows();
            ApplyDotKeyStyles();
            PaintKeyPicker();
            PaintPickerChrome();
            if (_presetsRaw != null && _presetsRaw.Length > 0) RenderPresets();   // 预设行按新配色重建
            UpdateStateTexts();
        }

        private Button ThBtn(int i)
        {
            switch (i)
            {
                case 0: return ThDark; case 1: return ThGray; case 2: return ThLight;
                case 3: return ThPink; case 4: return ThBlue; default: return ThCustom;
            }
        }

        // 六个主题按钮：当前主题高亮（原来没有任何选中反馈，用户不知道现在用的是哪个）
        private void ApplyThemeButtonStyles()
        {
            try
            {
                string theme = CurrentTheme();
                for (int i = 0; i < 6; i++)
                {
                    var b = ThBtn(i);
                    if (b == null) continue;
                    bool sel = (b.Tag as string) == theme;
                    b.Background = sel ? B(_pal.Accent) : B(_pal.Card2);
                    b.BorderBrush = sel ? B(_pal.Accent) : B(_pal.Border);
                    b.Foreground = sel ? B(_pal.AccentFg) : B(_pal.Text);
                }
            }
            catch { }
        }

        private void PaintKeyPicker()
        {
            try
            {
                AddKeyToggle.Background = B(_pal.Card2);
                AddKeyToggle.BorderBrush = B(_pal.Border);
                AddKeyToggleText.Foreground = B(_pal.Text);
                AddKeyToggleArrow.Foreground = B(_pal.Text);
                if (!_keyPickerBuilt || KeyPickerHost == null) return;
                foreach (var rowObj in KeyPickerHost.Children)
                {
                    var sp = rowObj as StackPanel;
                    if (sp == null) continue;
                    foreach (var bObj in sp.Children)
                    {
                        var b = bObj as Border;
                        if (b == null) continue;
                        b.Background = B(_pal.Card2);
                        b.BorderBrush = B(_pal.Border);
                        var tb = b.Child as TextBlock;
                        if (tb != null) tb.Foreground = B(_pal.Text);
                    }
                }
            }
            catch { }
        }

        // ===================== 数据读写 =====================

        private void LoadFromSettings()
        {
            var v = ApplicationData.Current.LocalSettings.Values;

            // 字体
            _keyFontTag = (v["KeyFontTag_"] as string) ?? "system";
            _keyFontSize = ReadDouble(v["KeyFontSize_"], 11, 8, 30);
            // 0.9.5：字重刻度从 1..5 扩到 1..10。老值（没写过刻度标记）按 +2 迁移，保证视觉不变：
            // 旧 3=SemiBold → 新 6=SemiBold，旧 5=ExtraBold → 新 8=ExtraBold。
            bool newScale = v[FontWeightScaleKey] != null;
            _keyFontWeightLevel = MigrateFontWeightLevel((int)ReadDouble(v["KeyFontWeight_"], 5, 1, 10), newScale);
            if (!newScale)
            {
                v[FontWeightScaleKey] = 1;
                v["KeyFontWeight_"] = _keyFontWeightLevel;
                ApplicationData.Current.SignalDataChanged();   // 让主窗口也按新刻度重算
                Diag("font weight scale migrated -> " + _keyFontWeightLevel);
            }
            FontSizeSlider2.Value = _keyFontSize;
            FontWeightSlider2.Value = _keyFontWeightLevel;
            FontSizeVal.Text = DisplayFontSize(_keyFontSize);
            FontWeightVal.Text = _keyFontWeightLevel + " / 10";

            // 布局
            _opacity = ReadDouble(v["KeyOpacity_"], 100, 10, 100);
            OpacitySlider2.Value = _opacity;
            OpacityVal.Text = ((int)_opacity).ToString();
            _padVisible = !(v["PadVisible_"] != null && v["PadVisible_"].ToString() == "0");
            _mouseSpeed = ReadDouble(v["MouseSpeed_"], 1.0, 0.5, 4.0);
            MouseSpeedSlider.Value = _mouseSpeed;
            MouseSpeedVal.Text = FormatSpeed(_mouseSpeed);
            _dotSize = ReadDouble(v["DotSize_"], 10, 4, 30);
            DotSizeSlider.Value = _dotSize;
            DotSizeVal.Text = ((int)_dotSize) + " px";
            _keyScaleUser = (int)ReadDouble(v["KeyScale_"], 0, -100, 100);
            KeyScaleSlider.Value = _keyScaleUser;
            KeyScaleVal.Text = (_keyScaleUser > 0 ? "+" : "") + _keyScaleUser;
            _dotKeyVk = (int)ReadDouble(v["MouseDotKeyVk_"], 0, 0, 255);
            _dotKeyName = (v["MouseDotKeyName_"] as string) ?? "";
            _dotKeyOn = !(v["MouseDotKeyOn_"] != null && v["MouseDotKeyOn_"].ToString() == "0") && _dotKeyVk != 0;
            _panelBgTransparent = !(v["PanelTransparent_"] != null && v["PanelTransparent_"].ToString() == "0");
            _locked = !(v["LayoutLocked"] is bool lb && !lb);
        }

        private static double ReadDouble(object o, double def, double min, double max)
        {
            if (o == null) return def;
            double d;
            if (!double.TryParse(o.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return def;
            if (d < min) d = min;
            if (d > max) d = max;
            return d;
        }

        private string _keyFontTag = "system";
        private double _keyFontSize = 11;   // 0.9.5：界面显示 = 实际 − 1（所以默认显示 10）
        private int _keyFontWeightLevel = 5;   // 0.9.5：1..10，默认 5
        private double _opacity = 100;
        private bool _padVisible = true;
        private double _mouseSpeed = 1.0;   // 0.9.5：鼠标点移动倍率（0.5..4.0）
        private double _dotSize = 10;       // 0.9.5：光标点大小（4..30 px）
        private int _keyScaleUser;          // 0.9.5：整体按键大小（-20..+20 %）
        private bool _panelBgTransparent = true;   // 0.9.5：按键区背景全透明（默认透明）
        private bool _locked = true;

        // 0.9.5：字号「号数」按用户要求整体下移一格（实际 11 → 显示 10），仅影响显示，存储仍是实际 FontSize
        private static string DisplayFontSize(double actual)
        {
            int n = (int)Math.Round(actual) - 1;
            if (n < 1) n = 1;
            return n.ToString();
        }

        // 0.9.5：字重刻度 1..10 的标记键（旧版本是 1..5）
        private const string FontWeightScaleKey = "KeyFontWeightScale2_";

        private static int MigrateFontWeightLevel(int oldLevel, bool alreadyNewScale)
        {
            int lv = alreadyNewScale ? oldLevel : oldLevel + 2;
            if (lv < 1) lv = 1;
            if (lv > 10) lv = 10;
            return lv;
        }

        private void Save(string key, object value)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
                ApplicationData.Current.SignalDataChanged();   // 通知主小组件实时重载
            }
            catch (Exception ex) { Diag("save fail " + key + ": " + ex.Message); }
        }

        // 颜色写盘（0.9.5 分两个作用域）：
        //   ① k=0（面板）/ k=8（强调色）→ 只写设置窗口自己的覆盖键，只影响本设置窗口外观；
        //      不写小组件色槽、不切主题、不通知小组件重载（小组件面板不受影响）。
        //   ② 其余色槽 → 与主窗口一致的"固化"：先把当前主题显示中的全部色槽值写入 Custom_ 键
        //      （否则小组件切到自定义主题后其余槽会掉回深色默认值），再写新值，最后切主题为 custom。
        private void CommitSlotColor(int k, Color c)
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                if (IsSettingsScopeSlot(k))
                {
                    v[SettingsScopeKeyOf(k)] = ToHex(c);
                    BuildPalette();      // 设置窗口自身的面板/强调色立即生效
                    ApplyPalette();
                    Diag("settings scope slot " + k + " -> " + ToHex(c));
                    return;
                }
                for (int i = 0; i < CustomKeys.Length; i++)
                {
                    if (IsSettingsScopeSlot(i)) continue;   // 0/8 归设置窗口，不参与小组件固化
                    var cur = ParseHex(WidgetSlotHex(i));
                    if (cur.HasValue) v[CustomKeys[i]] = ToHex(cur.Value);
                }
                // 小组件自定义主题下的面板/强调色：仍按主题默认值写入，保证小组件配色完整（面板由主题决定）
                var panelHex = ParseHex(WidgetSlotHex(0));
                if (panelHex.HasValue) v[CustomKeys[0]] = ToHex(panelHex.Value);
                var accHex = ParseHex(WidgetSlotHex(8));
                if (accHex.HasValue) v[CustomKeys[8]] = ToHex(accHex.Value);
                v[CustomKeys[k]] = ToHex(c);
                v["Theme"] = "custom";
                _lastThemeSeen = "custom";
                ApplicationData.Current.SignalDataChanged();
            }
            catch (Exception ex) { Diag("commit slot fail: " + ex.Message); }
        }

        // ===================== 0.9.5：颜色页行列表（名称 + 色块 + 透明度百分比）=====================
        // 每行只显示：名称、一个色块（棋盘格垫底，能直接看出透明度）、透明度的百分比。
        // 点色块即在右侧展开该槽的调色盘面板。

        private bool _slotRowsBuilt;
        private readonly System.Collections.Generic.List<Border> _slotSwatches = new System.Collections.Generic.List<Border>();
        private readonly System.Collections.Generic.List<Border> _slotFills = new System.Collections.Generic.List<Border>();
        private readonly System.Collections.Generic.List<TextBlock> _slotPercents = new System.Collections.Generic.List<TextBlock>();

        private void BuildSlotRows()
        {
            if (_slotRowsBuilt || SlotRowHost == null) return;
            _slotRowsBuilt = true;
            try
            {
                foreach (int k in SlotOrder)
                {
                    var row = new Grid { Height = 36 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

                    var lbl = new TextBlock
                    {
                        Text = IsSettingsScopeSlot(k) ? SlotNames[k] + "（设置窗口）" : SlotNames[k],
                        FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = B(_pal.Text),
                        Tag = k.ToString(CultureInfo.InvariantCulture)
                    };
                    lbl.Tapped += SlotSwatch_Tapped;   // 点名称也能展开，点击目标更宽容
                    Grid.SetColumn(lbl, 0);

                    // 色块：外层带描边（打开中的槽用强调色描边），内层棋盘格 + 实际颜色（保留透明度观感）
                    var swatch = new Border
                    {
                        Width = 40,
                        Height = 22,
                        CornerRadius = new CornerRadius(5),
                        BorderThickness = new Thickness(1),
                        BorderBrush = B(_pal.Border),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                        Tag = k.ToString(CultureInfo.InvariantCulture)
                    };
                    var inner = new Grid();
                    inner.Children.Add(new Border { Background = CheckerBrush() });
                    var fill = new Border();
                    inner.Children.Add(fill);
                    swatch.Child = inner;
                    swatch.Tapped += SlotSwatch_Tapped;
                    _slotSwatches.Add(swatch);
                    _slotFills.Add(fill);

                    var pct = new TextBlock
                    {
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Right,
                        Margin = new Thickness(8, 0, 0, 0),
                        Foreground = B(_pal.Subtle)
                    };
                    _slotPercents.Add(pct);

                    Grid.SetColumn(swatch, 1);
                    Grid.SetColumn(pct, 2);
                    row.Children.Add(lbl);
                    row.Children.Add(swatch);
                    row.Children.Add(pct);
                    SlotRowHost.Children.Add(row);
                }
                Diag("slot rows built");
            }
            catch (Exception ex) { Diag("build slot rows fail: " + ex.Message); }
        }

        private static Brush CheckerBrush()
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            for (int i = 0; i < 8; i++)
            {
                var cc = (i % 2 == 0) ? Color.FromArgb(0xFF, 0xE6, 0xE6, 0xE6) : Color.FromArgb(0xFF, 0xAE, 0xAE, 0xAE);
                g.GradientStops.Add(new GradientStop { Color = cc, Offset = i / 8.0 });
                g.GradientStops.Add(new GradientStop { Color = cc, Offset = (i + 1.0) / 8.0 - 0.0001 });
            }
            return g;
        }

        // 刷新所有行的色块、透明度百分比与描边（打开中的那一行用强调色描边）
        private void RefreshSlotRows()
        {
            if (!_slotRowsBuilt) return;
            try
            {
                for (int i = 0; i < _slotSwatches.Count && i < SlotOrder.Length; i++)
                {
                    int k = SlotOrder[i];
                    var c = ParseHex(SlotText(k));
                    if (i < _slotFills.Count)
                        _slotFills[i].Background = B(c.HasValue ? c.Value : Colors.Transparent);
                    if (i < _slotPercents.Count)
                        _slotPercents[i].Text = PercentText(c);
                    bool open = (_pickerSlot == k) && PickerPanel != null && PickerPanel.Visibility == Visibility.Visible;
                    _slotSwatches[i].BorderBrush = open ? B(_pal.Accent) : B(_pal.Border);
                    _slotSwatches[i].BorderThickness = new Thickness(open ? 2 : 1);
                    if (i < _slotPercents.Count) _slotPercents[i].Foreground = B(open ? _pal.Text : _pal.Subtle);
                }
            }
            catch { }
        }

        // 透明度百分比文本（0~100%）
        private static string PercentText(Color? c)
        {
            byte a = c.HasValue ? c.Value.A : (byte)0xFF;
            int pct = (int)Math.Round(a / 255.0 * 100.0);
            return pct >= 100 ? "100%" : pct + "%";
        }

        private void SlotSwatch_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            if (fe == null) return;
            int k;
            if (!TagToSlot(fe.Tag, out k)) { Diag("swatch tap ignored (bad tag)"); return; }
            if (_pickerSlot == k && PickerPanel.Visibility == Visibility.Visible) { HidePicker(); e.Handled = true; return; }   // 再点一次收起
            ShowPicker(k);
            e.Handled = true;
        }

        // Tag 兼容 int 与 string 两种写法（0.9.5 修复：Tag 里塞过 int，
        // 原来只按 string 解析导致 int.TryParse(null) 直接返回——这就是"点色块打不开调色盘"的原因）
        private static bool TagToSlot(object tag, out int slot)
        {
            slot = -1;
            if (tag == null) return false;
            var s = tag as string;
            if (s != null) return int.TryParse(s, out slot) && slot >= 0 && slot < 10;
            try
            {
                slot = Convert.ToInt32(tag, CultureInfo.InvariantCulture);
                return slot >= 0 && slot < 10;
            }
            catch { return false; }
        }        private static string ToHex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", c.A, c.R, c.G, c.B);
        }

        // ===================== 主题 =====================

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            var b = sender as Button;
            if (b == null) return;
            string tag = b.Tag as string;
            if (string.IsNullOrEmpty(tag)) return;
            Save("Theme", tag);
            _lastThemeSeen = tag;
            // 0.9.5：切主题时清掉设置窗口的面板/强调色覆盖值 → 设置窗口同步跟随该主题的默认色
            try
            {
                var vv = ApplicationData.Current.LocalSettings.Values;
                vv.Remove(SettingsPanelKey);
                vv.Remove(SettingsAccentKey);
            }
            catch { }
            if (tag == "custom")
            {
                // 首次进入自定义：以「深色预设」填充（主窗口有同样的逻辑），避免 8 槽为空
                var v = ApplicationData.Current.LocalSettings.Values;
                bool any = false;
                for (int k = 0; k < 8; k++) if (!string.IsNullOrEmpty(v[CustomKeys[k]] as string)) { any = true; break; }
                if (!any)
                {
                    for (int k = 0; k < 8; k++) Save(CustomKeys[k], ThemeSlotHex[0][k]);
                }
            }
            RefreshSlotRows();   // 切主题后各槽显示色随之更新
            BuildPalette();
            ApplyPalette();
            Diag("theme -> " + tag);
        }

        private void ApplyNavSelection()
        {
            var v = ApplicationData.Current.LocalSettings.Values;
            string theme = (v["Theme"] as string) ?? "dark";
            foreach (var b in new Border[] { NavTheme, NavAdv, NavColor, NavFont, NavLayout, NavPreset, NavAbout })
            {
                bool sel = (b.Tag as string) == _section;
                b.Background = sel ? B(_pal.Accent) : B(Colors.Transparent);
                var tb = b.Child as TextBlock;
                if (tb != null) tb.Foreground = sel ? B(_pal.AccentFg) : B(_pal.Text);
            }
        }

        private string _section = "theme";

        private void Nav_Click(object sender, TappedRoutedEventArgs e)
        {
            var b = sender as Border;
            if (b == null) return;
            ShowSection(b.Tag as string);
            e.Handled = true;
        }

        private void ShowSection(string section)
        {
            if (string.IsNullOrEmpty(section)) return;
            _section = section;
            SecTheme.Visibility = section == "theme" ? Visibility.Visible : Visibility.Collapsed;
            SecAdv.Visibility = section == "adv" ? Visibility.Visible : Visibility.Collapsed;
            SecColor.Visibility = section == "color" ? Visibility.Visible : Visibility.Collapsed;
            SecFont.Visibility = section == "font" ? Visibility.Visible : Visibility.Collapsed;
            SecLayout.Visibility = section == "layout" ? Visibility.Visible : Visibility.Collapsed;
            SecPreset.Visibility = section == "preset" ? Visibility.Visible : Visibility.Collapsed;
            SecAbout.Visibility = section == "about" ? Visibility.Visible : Visibility.Collapsed;

            switch (section)
            {
                case "theme": PageTitle.Text = "主题"; PageDesc.Text = "选择配色方案"; break;
                case "adv": PageTitle.Text = "参数"; PageDesc.Text = "底层实测数据与无害开关（不改动游戏设置）"; break;
                case "color": PageTitle.Text = "颜色"; PageDesc.Text = "自定义颜色（9 个色槽，含强调色）"; break;
                case "font": PageTitle.Text = "字体"; PageDesc.Text = "按键显示名的字体、字号与粗细"; break;
                case "layout": PageTitle.Text = "布局"; PageDesc.Text = "透明度、鼠标垫与布局锁定"; break;
                case "preset": PageTitle.Text = "预设"; PageDesc.Text = "主题预设与布局预设"; break;
                default: PageTitle.Text = "关于"; PageDesc.Text = "版本与项目信息"; break;
            }
            ApplyNavSelection();
            if (section == "color") ApplyPickerScale();   // 切回颜色页时按当前窗口尺寸重算调色盘布局
            if (section == "adv") StartAdvPolling(); else StopAdvPolling();
        }

        // ===================== 字体 =====================

        private Border FontItemOf(int i)
        {
            switch (i)
            {
                case 0: return F0; case 1: return F1; case 2: return F2; case 3: return F3;
                case 4: return F4; case 5: return F5; case 6: return F6; case 7: return F7;
                case 8: return F8; case 9: return F9; case 10: return F10; case 11: return F11;
                case 12: return F12; case 13: return F13; case 14: return F14; case 15: return F15;
                case 16: return F16; default: return F17;
            }
        }

        private void ApplyFontItemStyles()
        {
            for (int i = 0; i <= 17; i++)
            {
                var b = FontItemOf(i);
                if (b == null) continue;
                bool sel = (b.Tag as string) == _keyFontTag;
                b.Background = sel ? B(_pal.Accent) : B(_pal.Card);
                b.BorderBrush = sel ? B(_pal.Accent) : B(_pal.Border);
                b.BorderThickness = new Thickness(sel ? 2 : 1);
                var tb = b.Child as TextBlock;
                if (tb != null) tb.Foreground = sel ? B(_pal.AccentFg) : B(_pal.Text);
            }
        }

        private void Font_Click(object sender, TappedRoutedEventArgs e)
        {
            var b = sender as Border;
            if (b == null) return;
            string tag = b.Tag as string;
            if (string.IsNullOrEmpty(tag)) return;
            _keyFontTag = tag;
            Save("KeyFontTag_", tag);
            ApplyFontItemStyles();
            Diag("font -> " + tag);
            e.Handled = true;
        }

        private void FontSize_Changed(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (!_loaded) return;
            _keyFontSize = e.NewValue;
            FontSizeVal.Text = DisplayFontSize(e.NewValue);
            Save("KeyFontSize_", (int)e.NewValue);
        }

        private void FontWeight_Changed(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (!_loaded) return;
            _keyFontWeightLevel = (int)Math.Round(e.NewValue);
            FontWeightVal.Text = _keyFontWeightLevel + " / 10";
            Save(FontWeightScaleKey, 1);                       // 标记为已使用新刻度
            Save("KeyFontWeight_", _keyFontWeightLevel);
        }

        // ===================== 0.9.5：鼠标光标按键（开关 + 按键捕获）=====================
        // 开关 MouseDotKeyOn_（1/0）；映射按键 MouseDotKeyVk_ + 显示名 MouseDotKeyName_。
        // 捕获方式：点一下捕获框 → 框进入"请按任意键"状态 → 下一次按键/鼠标键即被识别为映射。
        // VK 约定与 companion 一致：1/2/4=左/右/中键，5/6=侧下/侧上，7/8=滚轮上/下，其余为键盘 VK。

        private bool _dotKeyOn;
        private int _dotKeyVk;
        private string _dotKeyName = "";
        private bool _dotCapturing;

        private void DotKeyToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _dotKeyOn = DotKeyToggle.IsOn;
            Save("MouseDotKeyOn_", _dotKeyOn ? 1 : 0);
            ApplyDotKeyStyles();
        }

        // 点击捕获框：进入捕获态（再点一次取消）
        private void DotKeyCapture_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (_dotCapturing) { EndDotCapture(false); return; }
            _dotCapturing = true;

            ApplyDotKeyStyles();
        }

        private void DotKeyCapture_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!_dotCapturing) return;
            e.Handled = true;
            int vk = (int)e.Key;
            if (vk == 0x1B) { EndDotCapture(false); return; }             // Esc 取消
            BindDotKey(vk, ((Windows.System.VirtualKey)vk).ToString());
        }

        // 捕获鼠标按键（第一次点击只是进入捕获态，之后按下才算绑定）
        private void DotKeyCapture_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_dotCapturing) return;
            e.Handled = true;
            var p = e.GetCurrentPoint(DotKeyCapture).Properties;
            if (p.IsRightButtonPressed) { BindDotKey(2, "鼠标右键"); return; }
            if (p.IsMiddleButtonPressed) { BindDotKey(4, "鼠标中键"); return; }
            if (p.IsXButton1Pressed) { BindDotKey(6, "侧上键"); return; }
            if (p.IsXButton2Pressed) { BindDotKey(5, "侧下键"); return; }
            if (p.IsLeftButtonPressed) { BindDotKey(1, "鼠标左键"); return; }
        }

        // 捕获滚轮
        private void DotKeyCapture_Wheel(object sender, PointerRoutedEventArgs e)
        {
            if (!_dotCapturing) return;
            e.Handled = true;
            BindDotKey(e.GetCurrentPoint(DotKeyCapture).Properties.MouseWheelDelta > 0 ? 7 : 8,
                       e.GetCurrentPoint(DotKeyCapture).Properties.MouseWheelDelta > 0 ? "滚轮上" : "滚轮下");
        }

        private void DotKeyCapture_Enter(object sender, PointerRoutedEventArgs e)
        {
            if (_dotCapturing) return;
            try { DotKeyCapture.Background = B(_pal.Card); } catch { }
        }

        private void DotKeyCapture_Exit(object sender, PointerRoutedEventArgs e)
        {
            if (_dotCapturing) return;
            ApplyDotKeyStyles();
        }

        private void BindDotKey(int vk, string name)
        {
            _dotKeyVk = vk;
            _dotKeyName = string.IsNullOrEmpty(name) ? ("VK " + vk) : name;
            _dotKeyOn = true;                 // 绑定即视为启用（用户按了键就是要用它）
            EndDotCapture(true);
            Save("MouseDotKeyVk_", _dotKeyVk);
            Save("MouseDotKeyName_", _dotKeyName);
            Save("MouseDotKeyOn_", 1);
            try { DotKeyToggle.IsOn = true; } catch { }
            ApplyDotKeyStyles();
            DotKeyStatusSet("已识别并绑定：" + _dotKeyName + "　（该键按下时光标显示按下色）");
        }

        private void EndDotCapture(bool keepText)
        {
            _dotCapturing = false;
            ApplyDotKeyStyles();
        }

        private void DotKeyStatusSet(string s)
        {
            try { DotKeyStatus.Text = s; DotKeyStatus.Foreground = B(_pal.Accent); } catch { }
        }

        private void ApplyDotKeyStyles()
        {
            try
            {
                if (DotKeyStatus != null && !_dotCapturing && string.IsNullOrEmpty(_dotKeyName) && _dotKeyVk == 0)
                    DotKeyStatus.Text = "尚未设置映射按键：点一下右边方框，然后按你想映射的键。";

                if (DotKeyCaptureText != null)
                {
                    DotKeyCaptureText.Text = _dotCapturing
                        ? "请按任意键…（键盘键或鼠标键，Esc 取消）"
                        : (string.IsNullOrEmpty(_dotKeyName) ? "未设置（点击后按任意键）" : _dotKeyName + "（点击可改）");
                    DotKeyCaptureText.Foreground = _dotCapturing ? B(_pal.Accent) : B(_pal.Text);
                }
                if (DotKeyCapture != null)
                {
                    DotKeyCapture.BorderBrush = B(_dotCapturing ? _pal.Accent : _pal.Border);
                    DotKeyCapture.BorderThickness = new Thickness(_dotCapturing ? 2 : 1);
                    DotKeyCapture.Background = B(_dotCapturing ? _pal.Card : _pal.Card2);
                }
                if (DotKeyToggle != null) DotKeyToggle.IsOn = _dotKeyOn;
            }
            catch { }
        }
        // ===================== 0.9.5：鼠标速度（鼠标点移动倍率）=====================
        // 鼠标点原来与屏幕 1:1 映射：走完整个屏幕才碰到垫面边缘。倍率放大后只需更少鼠标位移就能到边，
        // 游戏内隐藏/锁定光标时用起来更顺手。倍率由小组件侧应用（LocalSettings 键 MouseSpeed_）。

        private static string FormatSpeed(double v)
        {
            return v.ToString("0.0", CultureInfo.InvariantCulture) + "×";
        }

        private void MouseSpeed_Changed(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (!_loaded) return;
            _mouseSpeed = e.NewValue;
            MouseSpeedVal.Text = FormatSpeed(_mouseSpeed);
            Save("MouseSpeed_", Math.Round(_mouseSpeed, 2));
        }

        private void KeyScale_Changed(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (!_loaded) return;
            _keyScaleUser = (int)Math.Round(e.NewValue);
            KeyScaleVal.Text = (_keyScaleUser > 0 ? "+" : "") + _keyScaleUser;
            Save("KeyScale_", _keyScaleUser);
        }

        private void DotSize_Changed(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (!_loaded) return;
            _dotSize = e.NewValue;
            DotSizeVal.Text = ((int)_dotSize) + " px";
            Save("DotSize_", (int)_dotSize);
        }

        private void MouseSpeedReset_Click(object sender, RoutedEventArgs e)
        {
            _mouseSpeed = 1.0;
            MouseSpeedSlider.Value = 1.0;
            MouseSpeedVal.Text = FormatSpeed(1.0);
            Save("MouseSpeed_", 1.0);
        }
        // ===================== 布局 =====================

        private void Opacity_Changed(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (!_loaded) return;
            _opacity = e.NewValue;
            OpacityVal.Text = ((int)e.NewValue).ToString();
            Save("KeyOpacity_", (int)e.NewValue);
        }

        private void PadToggle_Click(object sender, RoutedEventArgs e)
        {
            _padVisible = !_padVisible;
            Save("PadVisible_", _padVisible ? 1 : 0);
            UpdateStateTexts();
        }

        // 0.9.5：按键区背景 全透明 / 不透明 切换（小组件侧应用，默认全透明）
        private void PanelBgToggle_Click(object sender, RoutedEventArgs e)
        {
            _panelBgTransparent = !_panelBgTransparent;
            Save("PanelTransparent_", _panelBgTransparent ? 1 : 0);
            PanelBgBtn.Content = _panelBgTransparent ? "全透明" : "不透明";
        }

        private void LockToggle_Click(object sender, RoutedEventArgs e)
        {
            _locked = !_locked;
            Save("LayoutLocked", _locked);
            UpdateStateTexts();
        }

        private void ResetLayout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                // 0.9.5：改为「写标记键 + 通知」由小组件进程执行重置。
                // 原实现在设置窗口里直接枚举并删除这些键，但跨进程视图可能滞后（实测出现
                // "removed 0 keys"：一个键都没枚举到 → 重置毫无反应）；而且旧版重置还会把内置默认
                // 布局（含 Tab 自定义键与鼠标垫默认尺寸/位置）重新套用，只删键是恢复不出来的。
                long stamp = DateTime.UtcNow.Ticks;
                v["LayoutResetRequest_"] = stamp;
                ApplicationData.Current.SignalDataChanged();

                // 兜底：若本进程视图里确实能看到这些键，顺手也清掉（小组件那边还会再清一次，幂等）
                int local = 0;
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
                    local = rm.Count;
                }
                catch { }

                LayoutStatusSet("已请求重置按键布局：小组件会恢复内置默认布局与按键名（含 Tab 键与鼠标垫默认尺寸）");
                Diag("reset layout requested: marker=" + stamp + " localCleared=" + local);
            }
            catch (Exception ex) { Diag("reset layout fail: " + ex.Message); }
        }

        private void ResetCustomColor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                for (int k = 0; k < 8; k++) v.Remove(CustomKeys[k]);
                v.Remove(CustomKeys[8]);
                v.Remove(SettingsPanelKey);     // 设置窗口的面板/强调色覆盖值一并清掉，回到跟随主题
                v.Remove(SettingsAccentKey);
                v["Theme"] = "dark";
                _lastThemeSeen = "dark";
                ApplicationData.Current.SignalDataChanged();
                PickerPanel.Visibility = Visibility.Collapsed;
                RefreshSlotRows();
                BuildPalette();
                ApplyPalette();
                Diag("custom colors reset");
            }
            catch (Exception ex) { Diag("reset colors fail: " + ex.Message); }
        }

        private void UpdateStateTexts()
        {
            try
            {
                PadBtn.Content = _padVisible ? "显示" : "隐藏";
            PanelBgBtn.Content = _panelBgTransparent ? "全透明" : "不透明";
                LockBtn.Content = _locked ? "开" : "关";
            }
            catch { }
        }

        // ===================== 0.9.5：添加按键（87 配列键盘，代码生成）=====================

        private bool _keyPickerBuilt;

        private void AddKeyToggle_Click(object sender, TappedRoutedEventArgs e)
        {
            bool show = KeyPickerScroll2.Visibility != Visibility.Visible;
            KeyPickerScroll2.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            AddKeyToggleArrow.Text = show ? "\u25B2" : "\u25BC";
            if (show && !_keyPickerBuilt) BuildKeyPicker();
            e.Handled = true;
        }

        // 87 配列（TKL）键位行：每行一组键名，生成后点击即添加为自定义按键
        private static readonly string[][] KeyRows = new string[][]
        {
            new[] { "Esc", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "PrtSc", "ScrLk", "Pause" },
            new[] { "`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=", "Backspace" },
            new[] { "Tab", "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[", "]", "\\" },
            new[] { "CapsLock", "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'", "Enter" },
            new[] { "Shift", "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/", "右Shift" },
            new[] { "Ctrl", "Win", "Alt", "Space", "右Alt", "右Ctrl" },
            new[] { "Insert", "Home", "PgUp", "Delete", "End", "PgDn" },
            new[] { "↑", "←", "↓", "→" },
            new[] { "左键", "中键", "右键", "侧下", "侧上", "滚轮上", "滚轮下" }
        };

        private void BuildKeyPicker()
        {
            if (_keyPickerBuilt || KeyPickerHost == null) return;
            _keyPickerBuilt = true;
            try
            {
                foreach (var row in KeyRows)
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
                    foreach (var key in row)
                    {
                        double w = key.Length <= 2 ? 34 : (key.Length <= 4 ? 46 : 58);
                        var b = new Border
                        {
                            Width = w,
                            Height = 28,
                            CornerRadius = new CornerRadius(4),
                            BorderThickness = new Thickness(1),
                            Margin = new Thickness(0, 0, 4, 0),
                            Tag = key,
                            Background = B(_pal.Card),
                            BorderBrush = B(_pal.Border)
                        };
                        b.Child = new TextBlock
                        {
                            Text = key,
                            FontSize = key.Length <= 3 ? 11 : 10,
                            Foreground = B(_pal.Text),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        b.Tapped += KeyPick_Click;
                        sp.Children.Add(b);
                    }
                    KeyPickerHost.Children.Add(sp);
                }
                Diag("key picker built");
            }
            catch (Exception ex) { Diag("build key picker fail: " + ex.Message); }
        }

        // 点击键盘上的键 → 添加为自定义按键（写 Custom_/CustomPos_/CustomSize_，主窗口收到通知后重建）
        private void KeyPick_Click(object sender, TappedRoutedEventArgs e)
        {
            var b = sender as Border;
            if (b == null) return;
            string name = b.Tag as string;
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                if (v["Custom_" + name] as string == "1")
                {
                    PresetStatus.Text = "「" + name + "」已经添加过了";
                    e.Handled = true;
                    return;
                }
                // 位置：按已有自定义键数量错开排布（避免全部叠在一起）
                int n = 0;
                foreach (var kv in v) if (kv.Key.StartsWith("Custom_", StringComparison.Ordinal)) n++;
                double tx = 10 + (n % 10) * 46;
                double ty = 10 + (n / 10) * 46;
                v["Custom_" + name] = "1";
                v["CustomPos_" + name] = tx.ToString(CultureInfo.InvariantCulture) + ";" + ty.ToString(CultureInfo.InvariantCulture);
                v["CustomSize_" + name] = "52;48";
                ApplicationData.Current.SignalDataChanged();
                PresetStatus.Text = "已添加按键：「" + name + "」（主窗口已同步）";
                Diag("add custom key: " + name);
            }
            catch (Exception ex) { Diag("add key fail: " + ex.Message); }
            e.Handled = true;
        }

        // ===================== 0.9.5：色槽名称与常用色 =====================

        // 0.9.5：各主题第 10 槽（鼠标点按下）默认色 —— 取该主题强调色，深色底也清晰可见
        private static readonly string[] SwatchHex = {
            "#FFFFFF", "#000000", "#FF0000", "#FF8000", "#FFFF00", "#80FF00", "#00FF00", "#00FF80",
            "#00FFFF", "#0080FF", "#0000FF", "#8000FF", "#FF00FF", "#FF0080", "#808080", "#404040" };
        private static readonly string[] SlotNames = { "面板", "边框", "按键底", "文字", "按下底", "按下字", "鼠标垫", "鼠标点", "强调色", "鼠标点按下" };
        // ===================== 0.9.5：颜色页右侧的调色盘面板 =====================
        // 点击某行的色块后，在同一张卡片内、颜色列表右侧展开该槽对应的调色盘面板
        // （明度饱和度方块 / 色相条 / 透明度条 / 常用色），拖动即时预览并生效。

        private bool _pkBuilt;
        private int _pickerSlot = -1;
        private double _pkHue;
        private double _pkSat = 1.0;
        private double _pkVal = 1.0;
        private byte _pkAlpha = 0xFF;
        private Color _pkLastColor = Colors.White;
        private Color _pkOpenColor = Colors.White;
        private DateTime _pkLastSaveUtc = DateTime.MinValue;
        private TextBlock _pkTitle, _pkHexText, _pkAlphaText;
        private Border _pkPreview, _pkSvBase, _pkSvWhite, _pkSvBlack, _pkSvMarker;
        private Border _pkHueBar, _pkHueMarker, _pkAlphaBase, _pkAlphaFill, _pkAlphaMarker;
        private Grid _pkSvArea, _pkHueArea, _pkAlphaArea, _pkSwatchHost;
        private Button _pkCloseBtn;

        private static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

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

        private static void RgbToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 0) { h = 0; return; }
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        private Border PkMarker()
        {
            return new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(2),
                BorderBrush = B(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false
            };
        }

        private TextBlock PkLabel(string s)
        {
            return new TextBlock { Text = s, FontSize = 12, Margin = new Thickness(0, 14, 0, 0), Foreground = B(_pal.Subtle) };
        }

        private void EnsurePickerBuilt()
        {
            if (_pkBuilt || PickerBody == null) return;
            _pkBuilt = true;
            try
            {
                _pkTitle = new TextBlock { Text = "调色盘", FontSize = 14, FontWeight = Windows.UI.Text.FontWeights.SemiBold, Foreground = B(_pal.Text) };
                PickerBody.Children.Add(_pkTitle);

                // 预览 + 当前色值
                var head = new Grid { Margin = new Thickness(0, 12, 0, 0) };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                _pkPreview = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = B(_pal.Border) };
                _pkHexText = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Foreground = B(_pal.Text) };
                Grid.SetColumn(_pkHexText, 1);
                head.Children.Add(_pkPreview);
                head.Children.Add(_pkHexText);
                PickerBody.Children.Add(head);

                // 明度 / 饱和度方块（底色 = 当前色相，上叠 白→透明、透明→黑）
                _pkSvArea = new Grid { Width = 240, Height = 132, Background = B(Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), ManipulationMode = ManipulationModes.None };
                _pkSvBase = new Border { CornerRadius = new CornerRadius(6) };
                _pkSvWhite = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0.5),
                        EndPoint = new Point(1, 0.5),
                        GradientStops = {
                            new GradientStop { Color = Colors.White, Offset = 0 },
                            new GradientStop { Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 1 } }
                    }
                };
                _pkSvBlack = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new Point(0.5, 0),
                        EndPoint = new Point(0.5, 1),
                        GradientStops = {
                            new GradientStop { Color = Color.FromArgb(0x00, 0x00, 0x00, 0x00), Offset = 0 },
                            new GradientStop { Color = Colors.Black, Offset = 1 } }
                    }
                };
                _pkSvMarker = PkMarker();
                _pkSvArea.Children.Add(_pkSvBase);
                _pkSvArea.Children.Add(_pkSvWhite);
                _pkSvArea.Children.Add(_pkSvBlack);
                _pkSvArea.Children.Add(_pkSvMarker);
                _pkSvArea.PointerPressed += PkSv_Pressed;
                _pkSvArea.PointerMoved += PkSv_Moved;
                _pkSvArea.PointerReleased += PkDrag_Released;
                _pkSvArea.PointerCaptureLost += PkDrag_Released;
                _pkSvArea.SizeChanged += (s, e) => UpdatePickerMarkers();
                PickerBody.Children.Add(_pkSvArea);

                // 色相条
                PickerBody.Children.Add(PkLabel("色相"));
                _pkHueArea = new Grid { Width = 240, Height = 16, Background = B(Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), ManipulationMode = ManipulationModes.None };
                _pkHueBar = new Border { CornerRadius = new CornerRadius(8) };
                var hueGrad = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                for (int d = 0; d <= 360; d += 60)
                {
                    hueGrad.GradientStops.Add(new GradientStop { Color = HsvToRgb(d == 360 ? 0 : d, 1, 1), Offset = d / 360.0 });
                }
                _pkHueBar.Background = hueGrad;
                _pkHueMarker = PkMarker();
                _pkHueArea.Children.Add(_pkHueBar);
                _pkHueArea.Children.Add(_pkHueMarker);
                _pkHueArea.PointerPressed += PkHue_Pressed;
                _pkHueArea.PointerMoved += PkHue_Moved;
                _pkHueArea.PointerReleased += PkDrag_Released;
                _pkHueArea.PointerCaptureLost += PkDrag_Released;
                _pkHueArea.SizeChanged += (s, e) => UpdatePickerMarkers();
                PickerBody.Children.Add(_pkHueArea);

                // 透明度条（棋盘格底 + 0→255 渐变）；标签右侧只标一个百分比
                var alphaHead = new Grid { Margin = new Thickness(0, 14, 0, 0) };
                alphaHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                alphaHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var alphaLbl = PkLabel("透明度");
                alphaLbl.Margin = new Thickness(0);
                _pkAlphaText = new TextBlock { FontSize = 12, TextAlignment = TextAlignment.Right, Foreground = B(_pal.Subtle) };
                Grid.SetColumn(alphaLbl, 0);
                Grid.SetColumn(_pkAlphaText, 1);
                alphaHead.Children.Add(alphaLbl);
                alphaHead.Children.Add(_pkAlphaText);
                PickerBody.Children.Add(alphaHead);
                _pkAlphaArea = new Grid { Width = 240, Height = 16, Background = B(Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), ManipulationMode = ManipulationModes.None };
                var checker = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                for (int i = 0; i < 10; i++)
                {
                    var cc = (i % 2 == 0) ? Color.FromArgb(0xFF, 0xE6, 0xE6, 0xE6) : Color.FromArgb(0xFF, 0xAE, 0xAE, 0xAE);
                    checker.GradientStops.Add(new GradientStop { Color = cc, Offset = i / 10.0 });
                    checker.GradientStops.Add(new GradientStop { Color = cc, Offset = (i + 1.0) / 10.0 - 0.0001 });
                }
                _pkAlphaBase = new Border { CornerRadius = new CornerRadius(8), Background = checker };
                _pkAlphaFill = new Border { CornerRadius = new CornerRadius(8) };
                _pkAlphaMarker = PkMarker();
                _pkAlphaArea.Children.Add(_pkAlphaBase);
                _pkAlphaArea.Children.Add(_pkAlphaFill);
                _pkAlphaArea.Children.Add(_pkAlphaMarker);
                _pkAlphaArea.PointerPressed += PkAlpha_Pressed;
                _pkAlphaArea.PointerMoved += PkAlpha_Moved;
                _pkAlphaArea.PointerReleased += PkDrag_Released;
                _pkAlphaArea.PointerCaptureLost += PkDrag_Released;
                _pkAlphaArea.SizeChanged += (s, e) => UpdatePickerMarkers();
                PickerBody.Children.Add(_pkAlphaArea);

                // 常用色
                PickerBody.Children.Add(PkLabel("常用色"));
                _pkSwatchHost = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                for (int r = 0; r < 2; r++)
                {
                    _pkSwatchHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
                    var row = new Grid();
                    for (int c = 0; c < 8; c++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    for (int c = 0; c < 8; c++)
                    {
                        string hex = SwatchHex[r * 8 + c];
                        var sw = new Border
                        {
                            CornerRadius = new CornerRadius(4),
                            BorderThickness = new Thickness(1),
                            Margin = new Thickness(2),
                            Tag = hex,
                            Background = B(Hex(hex, Colors.Gray)),
                            BorderBrush = B(_pal.Border)
                        };
                        sw.Tapped += PkSwatch_Click;
                        Grid.SetColumn(sw, c);
                        row.Children.Add(sw);
                    }
                    Grid.SetRow(row, r);
                    _pkSwatchHost.Children.Add(row);
                }
                PickerBody.Children.Add(_pkSwatchHost);

                _pkCloseBtn = new Button { Content = "关闭", Height = 32, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 14, 0, 0) };
                _pkCloseBtn.Click += (s, e) => { HidePicker(); };
                PickerBody.Children.Add(_pkCloseBtn);
                Diag("picker built");
            }
            catch (Exception ex) { Diag("build picker fail: " + ex.Message); }
        }

        private void ShowPicker(int slot)
        {
            try
            {
                EnsurePickerBuilt();
                _pickerSlot = slot;
                var cur = ParseHex(SlotText(slot));
                var openColor = cur.HasValue ? cur.Value : Colors.White;
                _pkOpenColor = openColor;
                SyncPickerToColor(openColor);
                _pkTitle.Text = "调色盘 · " + SlotNames[slot];
                PickerPanel.Visibility = Visibility.Visible;   // 0.9.5：面板就展开在颜色列表右侧（不再浮动定位/向左堆叠）
                ApplyPickerScale();                            // 0.9.5：按当前窗口尺寸算好缩放，保证看全不被裁切
                UpdatePickerMarkers();
                RefreshSlotRows();                             // 该行按钮高亮
                Diag("picker open slot=" + slot);
            }
            catch (Exception ex) { Diag("show picker fail: " + ex.Message); }
        }

        // ===================== 0.9.5：调色盘布局自适应 =====================
        // 用户反馈：窗口拉到最小时调色盘「缩成一坨」，而且之后怎么调窗口都不再出现。
        // 根因是之前用代码手算比例并给宿主显式设宽高：高度被压缩后又被 SizeChanged 回读成「原始高度」，
        // 形成自反馈把面板越缩越小，并留下卡住的状态。
        // 现在改为：位置（并排 / 换行）由代码决定且只在模式变化时改一次；缩放完全交给 Viewbox 自动完成
        // （StretchDirection=DownOnly：只在放不下时按比例缩小，永不放大），无自反馈、不会卡住、也不卡顿。

        private bool _pickerSideMode = true;      // true=并排在颜色列表右侧；false=排在列表下方
        private bool _pickerModeApplied;

        private void ApplyPickerScale()
        {
            try
            {
                if (PickerViewbox == null) return;
                if (PickerPanel == null || PickerPanel.Visibility != Visibility.Visible) return;

                double cardW = RootGrid.ActualWidth - 276;   // 卡片内容宽 = 窗口宽 − 导航(196) − 页面内边距(48) − 卡片内边距(32)
                if (cardW <= 1) return;
                // 并排需要：面板 292 + 间距 16 + 给色槽列表留 150
                bool side = cardW >= 292 + 16 + 150;
                if (_pickerModeApplied && side == _pickerSideMode) return;   // 模式没变就不动布局（拖动窗口时不反复触发布局）
                _pickerSideMode = side;
                _pickerModeApplied = true;

                if (side)
                {
                    Grid.SetRow(PickerViewbox, 0);
                    Grid.SetColumn(PickerViewbox, 1);
                    Grid.SetColumnSpan(PickerViewbox, 1);
                    PickerViewbox.Margin = new Thickness(16, 0, 0, 0);
                }
                else
                {
                    Grid.SetRow(PickerViewbox, 1);
                    Grid.SetColumn(PickerViewbox, 0);
                    Grid.SetColumnSpan(PickerViewbox, 2);
                    PickerViewbox.Margin = new Thickness(0, 14, 0, 0);
                }
            }
            catch { }
        }

        private void ResetPickerScale()
        {
            _pickerModeApplied = false;   // 下次展开时按当前窗口尺寸重新决定位置
        }
        private void HidePicker()
        {
            try
            {
                // 只在颜色真的变过时才固化（避免"打开又关掉"就把主题切成自定义）
                if (_pickerSlot >= 0 && _pkBuilt && ToHex(_pkLastColor) != ToHex(_pkOpenColor))
                    CommitSlotColor(_pickerSlot, _pkLastColor);
                PickerPanel.Visibility = Visibility.Collapsed;
                ResetPickerScale();
                RefreshSlotRows();
                Diag("picker closed slot=" + _pickerSlot);
            }
            catch (Exception ex) { Diag("hide picker fail: " + ex.Message); }
        }

        // 色相变化后：方块底色 + 透明度条底色跟着换
        private void UpdatePickerHueVisuals()
        {
            try
            {
                _pkSvBase.Background = B(HsvToRgb(_pkHue, 1.0, 1.0));
                var hue = HsvToRgb(_pkHue, Math.Max(0.15, _pkSat), Math.Max(0.15, _pkVal));
                _pkAlphaFill.Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5),
                    GradientStops = {
                        new GradientStop { Color = Color.FromArgb(0x00, hue.R, hue.G, hue.B), Offset = 0 },
                        new GradientStop { Color = Color.FromArgb(0xFF, hue.R, hue.G, hue.B), Offset = 1 } }
                };
            }
            catch { }
        }

        private void UpdatePickerMarkers()
        {
            try
            {
                if (!_pkBuilt) return;
                double w = _pkSvArea.ActualWidth > 1 ? _pkSvArea.ActualWidth : 240;
                double h = _pkSvArea.ActualHeight > 1 ? _pkSvArea.ActualHeight : 132;
                _pkSvMarker.Margin = new Thickness(_pkSat * w - 7, (1 - _pkVal) * h - 7, 0, 0);
                double hw = _pkHueArea.ActualWidth > 1 ? _pkHueArea.ActualWidth : 240;
                _pkHueMarker.Margin = new Thickness(_pkHue / 360.0 * hw - 7, 1, 0, 0);
                double aw = _pkAlphaArea.ActualWidth > 1 ? _pkAlphaArea.ActualWidth : 240;
                _pkAlphaMarker.Margin = new Thickness(_pkAlpha / 255.0 * aw - 7, 1, 0, 0);
            }
            catch { }
        }

        private void SyncPickerToColor(Color c)
        {
            try
            {
                double h, s, v;
                RgbToHsv(c, out h, out s, out v);
                if (s < 0.02) h = _pkHue;    // 白/黑/灰（无色相）：保留当前色相，方块与色相条不跳回红
                _pkHue = h;
                _pkSat = s;
                _pkVal = v;
                _pkAlpha = c.A;
                _pkLastColor = c;
                if (!_pkBuilt) return;
                _pkPreview.Background = B(c);
                _pkHexText.Text = ToHex(c);
                UpdateAlphaText();
                UpdatePickerHueVisuals();
                UpdatePickerMarkers();
            }
            catch { }
        }

        // 拖动/点击取色：live=true 时写入节流（拖动中），false 时立即落盘
        private void ApplyPickerColor(Color c, bool live)
        {
            try
            {
                _pkLastColor = c;
                if (_pkBuilt)
                {
                    _pkPreview.Background = B(c);
                    _pkHexText.Text = ToHex(c);
                    UpdateAlphaText();
                    UpdatePickerHueVisuals();
                    UpdatePickerMarkers();
                }
                RefreshSlotRows();   // 行内色块实时跟随
                bool due = (DateTime.UtcNow - _pkLastSaveUtc).TotalMilliseconds >= 80;
                if (!live || due)
                {
                    _pkLastSaveUtc = DateTime.UtcNow;
                    CommitSlotColor(_pickerSlot, c);
                }
            }
            catch (Exception ex) { Diag("apply picker fail: " + ex.Message); }
        }

        private void UpdateAlphaText()
        {
            try { if (_pkAlphaText != null) _pkAlphaText.Text = PercentText(Color.FromArgb(_pkAlpha, 0, 0, 0)); }
            catch { }
        }

        private Color CurrentPickerColor()
        {
            var rgb = HsvToRgb(_pkHue, _pkSat, _pkVal);
            return Color.FromArgb(_pkAlpha, rgb.R, rgb.G, rgb.B);
        }

        private void PkSv_Pressed(object sender, PointerRoutedEventArgs e)
        {
            try { _pkSvArea.CapturePointer(e.Pointer); PkSvUpdate(e); e.Handled = true; } catch { }
        }

        private void PkSv_Moved(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (_pkSvArea.PointerCaptures != null && _pkSvArea.PointerCaptures.Count > 0) { PkSvUpdate(e); e.Handled = true; }
            }
            catch { }
        }

        private void PkSvUpdate(PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(_pkSvArea).Position;
            double w = _pkSvArea.ActualWidth > 1 ? _pkSvArea.ActualWidth : 240;
            double h = _pkSvArea.ActualHeight > 1 ? _pkSvArea.ActualHeight : 132;
            _pkSat = Clamp01(p.X / w);
            _pkVal = 1.0 - Clamp01(p.Y / h);
            ApplyPickerColor(CurrentPickerColor(), true);
        }

        private void PkHue_Pressed(object sender, PointerRoutedEventArgs e)
        {
            try { _pkHueArea.CapturePointer(e.Pointer); PkHueUpdate(e); e.Handled = true; } catch { }
        }

        private void PkHue_Moved(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (_pkHueArea.PointerCaptures != null && _pkHueArea.PointerCaptures.Count > 0) { PkHueUpdate(e); e.Handled = true; }
            }
            catch { }
        }

        private void PkHueUpdate(PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(_pkHueArea).Position;
            double w = _pkHueArea.ActualWidth > 1 ? _pkHueArea.ActualWidth : 240;
            _pkHue = Math.Max(0.0, Math.Min(360.0, p.X / w * 360.0));
            // 灰阶状态下拖色相：先把饱和度提起来，否则怎么拖都是灰的（与主窗口同一处理）
            if (_pkSat < 0.05) _pkSat = 1.0;
            if (_pkVal < 0.05) _pkVal = 1.0;
            ApplyPickerColor(CurrentPickerColor(), true);
        }

        private void PkAlpha_Pressed(object sender, PointerRoutedEventArgs e)
        {
            try { _pkAlphaArea.CapturePointer(e.Pointer); PkAlphaUpdate(e); e.Handled = true; } catch { }
        }

        private void PkAlpha_Moved(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (_pkAlphaArea.PointerCaptures != null && _pkAlphaArea.PointerCaptures.Count > 0) { PkAlphaUpdate(e); e.Handled = true; }
            }
            catch { }
        }

        private void PkAlphaUpdate(PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(_pkAlphaArea).Position;
            double w = _pkAlphaArea.ActualWidth > 1 ? _pkAlphaArea.ActualWidth : 240;
            _pkAlpha = (byte)Math.Round(Clamp01(p.X / w) * 255);
            ApplyPickerColor(CurrentPickerColor(), true);
        }

        // 拖动结束：释放捕获并立即落盘最终颜色
        private bool _pkFinalizing;

        private void PkDrag_Released(object sender, PointerRoutedEventArgs e)
        {
            // PointerReleased 里 ReleasePointerCapture 会同步再触发一次 PointerCaptureLost，
            // 本函数同时挂了这两个事件 → 会被重入、重复落盘（叠加缺陷后就是多次整层重建），故加一次性守卫
            if (_pkFinalizing) return;
            _pkFinalizing = true;
            try
            {
                var el = sender as UIElement;
                if (el != null && e.Pointer != null) el.ReleasePointerCapture(e.Pointer);
            }
            catch { }
            try
            {
                if (_pickerSlot >= 0) { _pkLastSaveUtc = DateTime.UtcNow; CommitSlotColor(_pickerSlot, _pkLastColor); }
            }
            catch { }
            _pkFinalizing = false;
        }

        private void PkSwatch_Click(object sender, TappedRoutedEventArgs e)
        {
            var b = sender as Border;
            if (b == null) return;
            var picked = ParseHex(b.Tag as string);
            if (!picked.HasValue) return;
            var applied = Color.FromArgb(_pkAlpha, picked.Value.R, picked.Value.G, picked.Value.B);   // 保留当前透明度
            double h, s, v;
            RgbToHsv(applied, out h, out s, out v);
            if (s >= 0.02) _pkHue = h;
            _pkSat = s;
            _pkVal = v;
            ApplyPickerColor(applied, false);
            e.Handled = true;
        }

        // 调色盘浮层跟着主题换配色（面板/标题/方块边框/色块描边）
        private void PaintPickerChrome()
        {
            try
            {
                PickerPanel.Background = B(_pal.Card);
                PickerPanel.BorderBrush = B(_pal.Border);
                if (!_pkBuilt) return;
                _pkTitle.Foreground = B(_pal.Text);
                _pkHexText.Foreground = B(_pal.Text);
                if (_pkAlphaText != null) _pkAlphaText.Foreground = B(_pal.Subtle);
                _pkPreview.BorderBrush = B(_pal.Border);
                _pkCloseBtn.Background = B(_pal.Card2);
                _pkCloseBtn.BorderBrush = B(_pal.Border);
                _pkCloseBtn.Foreground = B(_pal.Text);
                foreach (var rowObj in _pkSwatchHost.Children)
                {
                    var row = rowObj as Grid;
                    if (row == null) continue;
                    foreach (var swObj in row.Children)
                    {
                        var sw = swObj as Border;
                        if (sw != null) sw.BorderBrush = B(_pal.Border);
                    }
                }
                RefreshSlotRows();
            }
            catch { }
        }

        // ===================== 0.9.5：主窗口改主题时本窗口同步 =====================

        private string _lastThemeSeen = "";
        private bool _storeHooked;

        private void HookStore()
        {
            if (_storeHooked) return;
            _storeHooked = true;
            try { ApplicationData.Current.DataChanged += Store_Changed; } catch { }
        }

        private void Store_Changed(ApplicationData sender, object args)
        {
            try
            {
                var d = this.Dispatcher;
                if (d == null) return;
                var act = d.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        string theme = CurrentTheme();
                        if (theme == _lastThemeSeen) return;
                        _lastThemeSeen = theme;
                        LoadFromSettings();
                        BuildPalette();
                        ApplyPalette();
                        if (_pickerSlot >= 0 && PickerPanel.Visibility == Visibility.Visible)
                        {
                            var c = ParseHex(SlotText(_pickerSlot));
                            if (c.HasValue) SyncPickerToColor(c.Value);
                        }
                        Diag("external theme change -> " + theme);
                    }
                    catch (Exception ex) { Diag("store change fail: " + ex.Message); }
                });
            }
            catch { }
        }

        // ===================== 0.9.5：关于页 =====================

        private void CopyRepo_Click(object sender, RoutedEventArgs e)
        {
            CopyText("https://github.com/0810milk/Gamebar-Keycast", "仓库链接已复制");
        }

        private void CopyQq_Click(object sender, RoutedEventArgs e)
        {
            CopyText("https://qun.qq.com/universal-share/share?ac=1&authKey=O", "QQ 群链接已复制");
        }

        private async void CopyText(string text, string tip)
        {
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                AboutTip.Text = tip + "，可粘贴到浏览器打开";
            }
            catch (Exception ex) { AboutTip.Text = "复制失败：" + ex.Message; }
        }

        // 预设导出的剪贴板写入：写完后回读一次校验，并把结果提示写在预设页顶部状态行（不写关于页的提示）
        private async void CopyJsonToClipboard(string text, string tip)
        {
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                int chars = 0;
                try
                {
                    var back = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                    if (back != null && back.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                        chars = (await back.GetTextAsync()).Length;
                }
                catch { }
                PresetStatusSet(tip + "（已复制 " + chars + " 个字符，去别处粘贴即可）");
            }
            catch (Exception ex) { PresetStatusSet("复制到剪贴板失败：" + ex.Message); }
        }

        // 按钮即时反馈：短暂把按钮文字换成提示（导出这类没有明显结果的操作，必须有可见反馈，否则用户以为没生效）
        private void FlashButton(Button b, string text)
        {
            try
            {
                if (b == null) return;
                string old = b.Content as string;
                b.Content = text;
                var timer = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    try { b.Content = old; } catch { }
                };
                timer.Start();
            }
            catch { }
        }

        // 0.9.5：产品版本（与 VERSION.md 一致）；UWP 包版本另行显示，便于排障
        internal const string ProductVersion = "1.1.1 中秋版本";

        private void SetAboutVersion()
        {
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                AboutVerText.Text = "版本 " + ProductVersion +
                                    string.Format("　（UWP 包 {0}.{1}.{2}.{3}）", v.Major, v.Minor, v.Build, v.Revision);
            }
            catch { }
        }

        // 0.9.5：关于页作者头像（Assets\Avatar.jpg，圆形 + 作者名）
        private void LoadAvatar()
        {
            try
            {
                AuthorNameText.Text = "作者：恐龙milk";
                AuthorNameText.Foreground = B(_pal.Text);
                var bmp = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Avatar.jpg"));
                // 圆形头像：椭圆 + 图片画刷填充（UWP 的 UIElement.Clip 只支持矩形，只能用画刷方案）
                AvatarEllipse.Fill = new ImageBrush { ImageSource = bmp, Stretch = Stretch.UniformToFill };
            }
            catch (Exception ex) { Diag("load avatar fail: " + ex.Message); }
        }

        // ===================== 0.9.5：预设管理（读写 %LOCALAPPDATA%\KeyDisplay\presets.json）=====================
        // 通过伴生进程管道：GET_PRESETS / PUT_PRESETS；JSON 结构与主窗口完全一致。

        private InputStateReader _reader;
        private string _presetsRaw = "";

        private async System.Threading.Tasks.Task<string> PresetRequestAsync(string cmd, string payload)
        {
            try
            {
                // 0.9.5 性能：原来固定等 600ms + 最多 8×(2500ms 超时 + 700ms 间隔)，最坏要十几秒。
                // 现在：已连接就直接发；未连接只按 30ms 轮询等最多 ~600ms；请求 1.2s 超时、最多试 2 次。
                if (_reader == null)
                {
                    _reader = new InputStateReader();
                    _reader.Start();
                }
                if (!_reader.Connected)
                {
                    for (int w = 0; w < 20 && !_reader.Connected; w++)
                        await System.Threading.Tasks.Task.Delay(30);
                }
                for (int i = 0; i < 2; i++)
                {
                    var resp = await _reader.RequestPresetAsync(cmd, payload, 1200);
                    if (resp != null) return resp;
                    if (i == 0) await System.Threading.Tasks.Task.Delay(120);
                }
            }
            catch (Exception ex) { Diag("preset request fail: " + ex.Message); }
            return null;
        }

        private async System.Threading.Tasks.Task LoadPresetsAsync()
        {
            var resp = await PresetRequestAsync("GET_PRESETS", "");
            if (resp == null) { PresetStatus.Text = "未能连接伴生进程，预设暂不可用"; return; }
            if (resp.StartsWith("DATA|")) { _presetsRaw = resp.Substring(5); RenderPresets(); }
            else PresetStatus.Text = "读取预设失败：" + resp;
        }

        private Windows.Data.Json.JsonObject ParsePresetsRoot()
        {
            try
            {
                Windows.Data.Json.JsonObject obj;
                if (Windows.Data.Json.JsonObject.TryParse(_presetsRaw, out obj)) return obj;
            }
            catch { }
            return null;
        }

        private void RenderPresets()
        {
            try
            {
                ThemePresetList.Children.Clear();
                LayoutPresetList.Children.Clear();
                var root = ParsePresetsRoot();
                int nt = RenderPresetGroup(root, "themePresets", ThemePresetList, "theme");
                int nl = RenderPresetGroup(root, "layoutPresets", LayoutPresetList, "layout");
                ThemePresetLbl.Text = "主题预设（" + nt + "）";
                LayoutPresetLbl.Text = "布局预设（" + nl + "）";
                if (nt == 0) ThemePresetList.Children.Add(HintText("暂无主题预设"));
                if (nl == 0) LayoutPresetList.Children.Add(HintText("暂无布局预设"));
            }
            catch (Exception ex) { Diag("render presets fail: " + ex.Message); }
        }

        private TextBlock HintText(string s)
        {
            return new TextBlock { Text = s, FontSize = 12, Opacity = 0.6, Foreground = B(_pal.Subtle), Margin = new Thickness(0, 4, 0, 0) };
        }

        private int RenderPresetGroup(Windows.Data.Json.JsonObject root, string arrayName, StackPanel host, string type)
        {
            if (root == null) return 0;
            Windows.Data.Json.JsonArray arr;
            if (!root.TryGetValue(arrayName, out var av)) return 0;
            arr = av.GetArray();
            int n = 0;
            foreach (var item in arr)
            {
                var o = item.GetObject();
                string name = o.ContainsKey("name") ? o.GetNamedString("name") : ("预设" + (n + 1));
                var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var lbl = new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = B(_pal.Text), TextTrimming = TextTrimming.CharacterEllipsis };
                var apply = new Button { Content = "应用", MinWidth = 64, Height = 30, Margin = new Thickness(8, 0, 0, 0), Tag = name, Background = B(_pal.Card2), BorderBrush = B(_pal.Border), Foreground = B(_pal.Text) };
                apply.Click += (s, e) => ApplyPresetByName(((Button)s).Tag as string, type);
                var del = new Button { Content = "删除", MinWidth = 56, Height = 30, Margin = new Thickness(6, 0, 0, 0), Tag = name, Background = B(_pal.Card2), BorderBrush = B(_pal.Border), Foreground = B(_pal.Text) };
                del.Click += (s, e) => DeletePresetByName(((Button)s).Tag as string, type);
                // 0.9.5：用户要求的按钮顺序 = 应用 | 删除 | 导出至粘贴板 | 导出（文件）
                var clip = new Button { Content = "导出至粘贴板", MinWidth = 100, Height = 30, FontSize = 12, Margin = new Thickness(6, 0, 0, 0), Tag = name, Background = B(_pal.Card2), BorderBrush = B(_pal.Border), Foreground = B(_pal.Text) };
                clip.Click += (s, e) => ExportPresetToClipboard(((Button)s).Tag as string, type, (Button)s);
                var exp = new Button { Content = "导出", MinWidth = 56, Height = 30, Margin = new Thickness(6, 0, 0, 0), Tag = name, Background = B(_pal.Card2), BorderBrush = B(_pal.Border), Foreground = B(_pal.Text) };
                exp.Click += (s, e) => ExportPresetToFile(((Button)s).Tag as string, type, (Button)s);
                Grid.SetColumn(lbl, 0); Grid.SetColumn(apply, 1); Grid.SetColumn(del, 2); Grid.SetColumn(clip, 3); Grid.SetColumn(exp, 4);
                row.Children.Add(lbl); row.Children.Add(apply); row.Children.Add(del); row.Children.Add(clip); row.Children.Add(exp);
                host.Children.Add(row);
                n++;
            }
            return n;
        }

        private void LayoutStatusSet(string s)
        {
            try { LayoutStatus.Text = s; LayoutStatus.Foreground = B(_pal.Accent); Diag(s); } catch { }
        }

        private void PresetStatusSet(string s)
        {
            try { PresetStatus.Text = s; Diag(s); } catch { }
        }

        // ===================== 0.9.5：参数页（无害开关 + 鼠标速度）=====================
        // 开关状态来自桌面伴生进程（命名管道命令 GET_STATS / SET_OPT），每秒轮询一次回显真实状态；
        // 实测数据（回报率/刷新率/延迟）按用户要求已不在此页展示，只保留开关状态与反馈。
        // 鼠标速度是小组件侧渲染参数（LocalSettings 键 MouseSpeed_），不经过伴生进程。

        private Windows.UI.Xaml.DispatcherTimer _advTimer;
        private bool _advBusy;                 // 单飞：同一时刻只允许一个 stats 请求，避免堆积
        private string _advLastStatsJson = "";
        private bool _advReaderHooked;        // 只挂一次读取器

        private void StartAdvPolling()
        {
            try
            {
                EnsureAdvReaderHooked();
                if (_advTimer == null)
                {
                    _advTimer = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                    _advTimer.Tick += (s, e) => { _ = AdvRefreshStatsAsync(); };
                }
                _advTimer.Start();
                _ = AdvRefreshStatsAsync();
            }
            catch (Exception ex) { Diag("adv polling start fail: " + ex.Message); }
        }

        private void StopAdvPolling()
        {
            try { if (_advTimer != null) _advTimer.Stop(); } catch { }
        }

        private void EnsureAdvReaderHooked()
        {
            if (_advReaderHooked) return;
            _advReaderHooked = true;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    if (_reader == null)
                    {
                        _reader = new InputStateReader();
                        _reader.Start();
                        await System.Threading.Tasks.Task.Delay(400);
                    }
                }
                catch (Exception ex) { Diag("adv reader hook fail: " + ex.Message); }
            });
        }

        private void AdvRefresh_Click(object sender, RoutedEventArgs e) { _ = AdvRefreshStatsAsync(); }

        private void AdvSetStatus(string s)
        {
            try { AdvIoStatus.Text = s; AdvIoStatus.Foreground = B(_pal.Accent); } catch { }
        }

        // 取一次实测数据（单飞，不重试：自动刷新每秒一次，堆积没有意义）
        private async System.Threading.Tasks.Task AdvRefreshStatsAsync()
        {
            if (_advBusy) return;
            _advBusy = true;
            try
            {
                EnsureAdvReaderHooked();
                if (_reader == null || !_reader.Connected)
                {
                    AdvSetStatus("未连接伴生进程（请确认 KeyDisplayCompanion.exe 正在运行）");
                    return;
                }
                string resp = await _reader.RequestPresetAsync("GET_STATS", "", 1200);
                if (resp == null) { AdvSetStatus("取数据超时（伴生进程可能还是旧版本，请运行新版安装包更新它）"); return; }
                if (!resp.StartsWith("DATA|")) { AdvSetStatus("返回异常：" + resp); return; }

                _advLastStatsJson = resp.Substring(5);
                Windows.Data.Json.JsonObject o;
                if (!Windows.Data.Json.JsonObject.TryParse(_advLastStatsJson, out o)) { AdvSetStatus("数据解析失败"); return; }

                int proto = (int)GetNum(o, "proto");
                bool mouseAccel = GetBool(o, "mouseAccel", false);
                bool lowLat = GetBool(o, "lowLatency", true);
                bool follow = GetBool(o, "followRefresh", false);
                int pushHz = (int)GetNum(o, "pushHz");

                // 0.9.5：只保留开关状态回显（用户要求去掉回报率/刷新率/延迟等实测展示）
                AdvSetStatus("已连接伴生进程（协议 v" + proto + "）");
                AdvAccelBtn.Content = mouseAccel ? "开" : "关";   // 读不到时按「关」显示，用户可直接点开
                AdvLowLatBtn.Content = lowLat ? "开" : "关";
                AdvFollowBtn.Content = follow ? "开" : "关";
                ApplyAdvHzStyles(pushHz);
                AdvSetStatus("");
            }
            catch (Exception ex) { AdvSetStatus("取数据异常：" + ex.Message); }
            finally { _advBusy = false; }
        }

        private static double GetNum(Windows.Data.Json.JsonObject o, string key)
        {
            try { return o.ContainsKey(key) ? o.GetNamedNumber(key) : 0; } catch { return 0; }
        }

        private static bool GetBool(Windows.Data.Json.JsonObject o, string key, bool def)
        {
            try { return o.ContainsKey(key) ? o.GetNamedBoolean(key) : def; } catch { return def; }
        }

        private void SetAdvText(TextBlock tb, string s)
        {
            try { if (tb != null) tb.Text = s; } catch { }
        }

        // 推送频率按钮组的选中态（用强调色）
        private Button AdvHzBtnOf(int hz)
        {
            switch (hz)
            {
                case 60: return AdvHz60; case 120: return AdvHz120; case 144: return AdvHz144;
                case 165: return AdvHz165; case 240: return AdvHz240; case 360: return AdvHz360;
                default: return AdvHz480;   // 480（以及其它值都会落到这里）
            }
        }

        private void ApplyAdvHzStyles(int current)
        {
            try
            {
                int[] all = { 60, 120, 144, 165, 240, 360, 480 };
                foreach (int hz in all)
                {
                    var b = AdvHzBtnOf(hz);
                    if (b == null) continue;
                    bool sel = hz == current;
                    b.Background = sel ? B(_pal.Accent) : B(_pal.Card2);
                    b.BorderBrush = sel ? B(_pal.Accent) : B(_pal.Border);
                    b.Foreground = sel ? B(_pal.AccentFg) : B(_pal.Text);
                }
            }
            catch { }
        }

        // 写开关：SET_OPT（伴生进程会立即生效并持久化到 %LOCALAPPDATA%\KeyDisplay\options.json）
        private async System.Threading.Tasks.Task SendAdvOptAsync(string json, Button flash)
        {
            try
            {
                EnsureAdvReaderHooked();
                if (_reader == null || !_reader.Connected) { AdvSetStatus("未连接伴生进程，无法修改"); return; }
                string resp = await _reader.RequestPresetAsync("SET_OPT", json, 1500);
                if (resp == null) { AdvSetStatus("修改超时（伴生进程可能还是旧版本，请运行新版安装包更新它）"); return; }
                if (!resp.StartsWith("OK")) { AdvSetStatus("修改失败：" + resp); return; }
                AdvSetStatus("已应用：" + json);
                FlashButton(flash, "已应用 ✓");
                await System.Threading.Tasks.Task.Delay(180);
                await AdvRefreshStatsAsync();
            }
            catch (Exception ex) { AdvSetStatus("修改异常：" + ex.Message); }
        }

        private void AdvHz_Click(object sender, RoutedEventArgs e)
        {
            var b = sender as Button; if (b == null) return;
            string tag = b.Tag as string;
            if (string.IsNullOrEmpty(tag)) return;
            _ = SendAdvOptAsync("{\"pushHz\":" + tag + "}", b);
        }

        private void AdvFollow_Click(object sender, RoutedEventArgs e)
        {
            bool now = (AdvFollowBtn.Content as string) == "开";
            _ = SendAdvOptAsync("{\"followRefresh\":" + (now ? "false" : "true") + "}", AdvFollowBtn);
        }

        private void AdvLowLat_Click(object sender, RoutedEventArgs e)
        {
            bool now = (AdvLowLatBtn.Content as string) == "开";
            _ = SendAdvOptAsync("{\"lowLatency\":" + (now ? "false" : "true") + "}", AdvLowLatBtn);
        }

        private void AdvAccel_Click(object sender, RoutedEventArgs e)
        {
            string cur = AdvAccelBtn.Content as string;
            if (cur != "开" && cur != "关") { AdvSetStatus("读不到当前鼠标加速状态（需要新版伴生进程）"); return; }
            _ = SendAdvOptAsync("{\"mouseAccel\":" + (cur == "开" ? "false" : "true") + "}", AdvAccelBtn);
        }

        // ===================== 0.9.5：出厂默认预设 =====================
        // 用户拍板：下面这套配色就是新的「默认」主题预设。首次加载设置窗口时把它种进 presets.json：
        // 同名「默认」存在则替换成这套内容，不存在则追加；然后应用一次并落盘。
        // 用一个标记键保证只种一次，之后用户自己改「默认」不会被覆盖。

        private const string FactoryDefaultThemePresetJson =
            "{\"name\":\"默认\",\"type\":\"theme\",\"savedAt\":\"2026-09-26T06:32:03\",\"data\":{\"theme\":\"dark\",\"colors\":{\"panel\":\"#E8121212\",\"border\":\"#52FFFFFF\",\"keyBg\":\"#F21A1A1A\",\"keyFg\":\"#FFFFFFFF\",\"pressedBg\":\"#FFFFFFFF\",\"pressedFg\":\"#FF101010\",\"pad\":\"#4D000000\",\"dot\":\"#FFFFFFFF\",\"accent\":\"#FF4CC2FF\"}}}";
        private const string FactoryDefaultPresetName = "默认";
        private const string FactoryDefaultSeededKey = "PresetDefaultSeeded2_";

        private async System.Threading.Tasks.Task InitPresetsAsync()
        {
            await LoadPresetsAsync();
            await EnsureFactoryDefaultPresetAsync();
        }
        private async System.Threading.Tasks.Task EnsureFactoryDefaultPresetAsync()
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                if (v[FactoryDefaultSeededKey] != null) return;   // 只种一次
                var root = ParsePresetsRoot();
                if (root == null) { Diag("factory preset seed skipped: presets unavailable"); return; }

                Windows.Data.Json.JsonObject def;
                if (!Windows.Data.Json.JsonObject.TryParse(FactoryDefaultThemePresetJson, out def))
                { Diag("factory preset json parse fail"); return; }

                if (!root.ContainsKey("version")) root.SetNamedValue("version", Windows.Data.Json.JsonValue.CreateNumberValue(1));
                if (!root.ContainsKey("themePresets")) root.SetNamedValue("themePresets", new Windows.Data.Json.JsonArray());
                if (!root.ContainsKey("layoutPresets")) root.SetNamedValue("layoutPresets", new Windows.Data.Json.JsonArray());

                int replaced = 0;
                var kept = new Windows.Data.Json.JsonArray();
                foreach (var item in root.GetNamedArray("themePresets"))
                {
                    if (item.ValueType != Windows.Data.Json.JsonValueType.Object) continue;
                    var o = item.GetObject();
                    string nm = o.ContainsKey("name") ? o.GetNamedString("name") : "";
                    if (nm == FactoryDefaultPresetName) { kept.Add(def); replaced++; }
                    else kept.Add(o);
                }
                if (replaced == 0) kept.Add(def);
                root.SetNamedValue("themePresets", kept);

                var resp = await PresetRequestAsync("PUT_PRESETS", root.Stringify());
                v[FactoryDefaultSeededKey] = 1;
                Diag("factory default preset seeded (replaced=" + replaced + ")");

                if (resp != null && resp.StartsWith("OK"))
                {
                    await LoadPresetsAsync();
                    ApplyPresetByName(FactoryDefaultPresetName, "theme");   // 应用一次，立即生效
                    Diag("factory default preset applied");
                }
            }
            catch (Exception ex) { Diag("seed factory preset fail: " + ex.Message); }
        }

        // ===================== 0.9.5：预设导出 / 导入 =====================
        // 导出：每条预设右侧两个按钮 —— 「导出至粘贴板」（复制 JSON，便于直接发给别人）与「导出」（存成 .json 文件）。
        // 导入：预设页顶部「文件导入」，选择 .json 文件（完整 presets 文件或单条预设对象都认）。
        // 文件选择器在 Game Bar 里理论上可用；万一被覆盖层挡住/不可用，会自动回退到应用本地文件夹并给出完整路径，
        // 保证功能永远可用（不会出现"点了没反应"）。

        // 取单条预设的导出 JSON（含 version，结构与 presets.json 一致，可直接分享给别人导入）
        private string BuildSinglePresetJson(string name, string type, out string err)
        {
            err = null;
            var root = ParsePresetsRoot();
            if (root == null) { err = "预设数据不可用，请到预设页重新加载"; return null; }
            string arrName = type == "layout" ? "layoutPresets" : "themePresets";
            Windows.Data.Json.IJsonValue av;
            if (!root.TryGetValue(arrName, out av) || av.ValueType != Windows.Data.Json.JsonValueType.Array) { err = "找不到该预设"; return null; }
            foreach (var item in av.GetArray())
            {
                if (item.ValueType != Windows.Data.Json.JsonValueType.Object) continue;
                var o = item.GetObject();
                string nm = o.ContainsKey("name") ? o.GetNamedString("name") : "";
                if (nm != name) continue;
                var file = new Windows.Data.Json.JsonObject();
                file.SetNamedValue("version", Windows.Data.Json.JsonValue.CreateNumberValue(1));
                var one = new Windows.Data.Json.JsonArray();
                one.Add(o);
                file.SetNamedValue(arrName, one);
                return file.Stringify();
            }
            err = "找不到该预设：" + name;
            return null;
        }

        // 导出至粘贴板（保留）
        private void ExportPresetToClipboard(string name, string type, Button src)
        {
            string err;
            string json = BuildSinglePresetJson(name, type, out err);
            if (json == null) { PresetStatusSet(err); return; }
            CopyJsonToClipboard(json, "已导出预设「" + name + "」至粘贴板（" + type + "）");
            FlashButton(src, "已复制 ✓");
        }

        // 导出成文件（文件选择器；不可用时回退到应用本地文件夹）
        private async void ExportPresetToFile(string name, string type, Button src)
        {
            string err;
            string json = BuildSinglePresetJson(name, type, out err);
            if (json == null) { PresetStatusSet(err); return; }
            string baseName = SanitizeFileName(name) + (type == "layout" ? "-布局" : "-主题");
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                picker.FileTypeChoices.Add("KeyDisplay 预设", new System.Collections.Generic.List<string> { ".json" });
                picker.SuggestedFileName = baseName;
                var target = await picker.PickSaveFileAsync();
                if (target == null) { PresetStatusSet("已取消导出"); return; }
                await Windows.Storage.FileIO.WriteTextAsync(target, json);
                PresetStatusSet("已导出到文件：" + target.Path);
                FlashButton(src, "已导出 ✓");
            }
            catch (Exception ex)
            {
                await ExportFallbackToLocalFolder(json, baseName, ex, src);
            }
        }

        // 文件选择器不可用时的兜底：写到应用本地文件夹，并把完整路径显示给用户（可直接复制路径取文件）
        private async System.Threading.Tasks.Task ExportFallbackToLocalFolder(string json, string baseName, Exception cause, Button src)
        {
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("preset-export", CreationCollisionOption.OpenIfExists);
                var file = await folder.CreateFileAsync(baseName + ".json", CreationCollisionOption.GenerateUniqueName);
                await Windows.Storage.FileIO.WriteTextAsync(file, json);
                PresetStatusSet("文件选择器不可用（" + (cause != null ? cause.Message : "未知原因") + "），已改存到：" + file.Path);
                FlashButton(src, "已导出 ✓");
            }
            catch (Exception ex2) { PresetStatusSet("导出失败：" + ex2.Message); }
        }

        // 文件导入：选一个 .json（完整 presets 文件或单条预设对象都支持）
        private async void ImportPresetFromFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add(".json");
                var file = await picker.PickSingleFileAsync();
                if (file == null) { PresetStatusSet("已取消导入"); return; }
                string text = await Windows.Storage.FileIO.ReadTextAsync(file);
                await ApplyImportedJson(text, file.Path);
            }
            catch (Exception ex)
            {
                PresetStatusSet("文件选择器不可用或读取失败（" + ex.Message + "）。可改用「导出至粘贴板」分享的 JSON，粘贴后由剪贴板导入。");
            }
        }

        // 兼容旧入口：从剪贴板导入（保留了这条路径，方便直接粘贴别人发来的 JSON）
        private async void ImportPreset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (content == null || !content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                { PresetStatusSet("剪贴板里没有文本"); return; }
                string text = await content.GetTextAsync();
                await ApplyImportedJson(text, "剪贴板");
            }
            catch (Exception ex) { PresetStatusSet("导入异常：" + ex.Message); }
        }

        // 导入主体：解析 + 合并（重名自动加序号）+ 写回 presets
        private async System.Threading.Tasks.Task ApplyImportedJson(string text, string sourceName)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) { PresetStatusSet("导入内容为空"); return; }
                text = text.Trim();
                int brace = text.IndexOf('{');
                if (brace > 0) text = text.Substring(brace);   // 容忍前面带了说明文字

                Windows.Data.Json.JsonObject obj;
                if (!Windows.Data.Json.JsonObject.TryParse(text, out obj)) { PresetStatusSet("不是有效的预设 JSON（" + sourceName + "）"); return; }

                var root = ParsePresetsRoot();
                if (root == null) root = new Windows.Data.Json.JsonObject();
                if (!root.ContainsKey("version")) root.SetNamedValue("version", Windows.Data.Json.JsonValue.CreateNumberValue(1));
                if (!root.ContainsKey("themePresets")) root.SetNamedValue("themePresets", new Windows.Data.Json.JsonArray());
                if (!root.ContainsKey("layoutPresets")) root.SetNamedValue("layoutPresets", new Windows.Data.Json.JsonArray());

                int added = 0;
                added += MergeImported(root, obj, "themePresets");
                added += MergeImported(root, obj, "layoutPresets");
                if (added == 0 && obj.ContainsKey("data"))
                {
                    // 单条预设对象：按它自己的 type 归组
                    string t = obj.ContainsKey("type") ? obj.GetNamedString("type") : "theme";
                    string arrName = t == "layout" ? "layoutPresets" : "themePresets";
                    string nm = obj.ContainsKey("name") ? obj.GetNamedString("name") : ("导入的预设 " + DateTime.Now.ToString("HHmmss"));
                    var entry = new Windows.Data.Json.JsonObject();
                    entry.SetNamedValue("name", Windows.Data.Json.JsonValue.CreateStringValue(UniqueNameIn(root, arrName, nm)));
                    entry.SetNamedValue("type", Windows.Data.Json.JsonValue.CreateStringValue(t == "layout" ? "layout" : "theme"));
                    entry.SetNamedValue("savedAt", Windows.Data.Json.JsonValue.CreateStringValue(DateTime.Now.ToString("s")));
                    entry.SetNamedValue("data", obj.GetNamedObject("data"));
                    root.GetNamedArray(arrName).Add(entry);
                    added = 1;
                }
                if (added == 0) { PresetStatusSet("JSON 里没有可识别的预设（" + sourceName + "）"); return; }

                var resp = await PresetRequestAsync("PUT_PRESETS", root.Stringify());
                if (resp != null && resp.StartsWith("OK")) { PresetStatusSet("已从 " + sourceName + " 导入 " + added + " 个预设"); await LoadPresetsAsync(); }
                else PresetStatusSet("导入失败：" + (resp ?? "伴生进程不可用"));
            }
            catch (Exception ex) { PresetStatusSet("导入异常：" + ex.Message); }
        }

        // 文件名安全化（去掉路径非法字符）
        private static string SanitizeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "预设";
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                if (c < 32 || c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') sb.Append('_');
                else sb.Append(c);
            }
            string r = sb.ToString().Trim().TrimEnd('.');
            if (r.Length == 0) r = "预设";
            if (r.Length > 40) r = r.Substring(0, 40);
            return r;
        }

        // 把 obj 里 arrName 数组的条目合并进 root（重名自动加序号），返回新增条数
        private int MergeImported(Windows.Data.Json.JsonObject root, Windows.Data.Json.JsonObject obj, string arrName)
        {
            int added = 0;
            try
            {
                Windows.Data.Json.IJsonValue av;
                if (!obj.TryGetValue(arrName, out av)) return 0;
                if (av.ValueType != Windows.Data.Json.JsonValueType.Array) return 0;
                string entryType = arrName == "layoutPresets" ? "layout" : "theme";
                var target = root.GetNamedArray(arrName);
                foreach (var item in av.GetArray())
                {
                    if (item.ValueType != Windows.Data.Json.JsonValueType.Object) continue;
                    var o = item.GetObject();
                    if (!o.ContainsKey("data")) continue;
                    string nm = o.ContainsKey("name") ? o.GetNamedString("name") : ("导入的预设 " + (added + 1));
                    var entry = new Windows.Data.Json.JsonObject();
                    entry.SetNamedValue("name", Windows.Data.Json.JsonValue.CreateStringValue(UniqueNameIn(root, arrName, nm)));
                    entry.SetNamedValue("type", Windows.Data.Json.JsonValue.CreateStringValue(
                        o.ContainsKey("type") ? (o.GetNamedString("type") == "layout" ? "layout" : "theme") : entryType));
                    entry.SetNamedValue("savedAt", Windows.Data.Json.JsonValue.CreateStringValue(DateTime.Now.ToString("s")));
                    entry.SetNamedValue("data", o.GetNamedObject("data"));
                    target.Add(entry);
                    added++;
                }
            }
            catch (Exception ex) { Diag("merge import fail: " + ex.Message); }
            return added;
        }

        // 与已有预设重名时自动追加「(2)(3)…」
        private static string UniqueNameIn(Windows.Data.Json.JsonObject root, string arrName, string want)
        {
            try
            {
                var names = new System.Collections.Generic.HashSet<string>();
                Windows.Data.Json.IJsonValue av;
                if (root.TryGetValue(arrName, out av) && av.ValueType == Windows.Data.Json.JsonValueType.Array)
                    foreach (var it in av.GetArray())
                        if (it.ValueType == Windows.Data.Json.JsonValueType.Object)
                        {
                            var o = it.GetObject();
                            if (o.ContainsKey("name")) names.Add(o.GetNamedString("name"));
                        }
                if (!names.Contains(want)) return want;
                for (int i = 2; i < 999; i++)
                {
                    string cand = want + "(" + i + ")";
                    if (!names.Contains(cand)) return cand;
                }
            }
            catch { }
            return want + "(" + DateTime.Now.ToString("HHmmss") + ")";
        }

        // 应用预设：把预设内容写回 LocalSettings，再通知主窗口重载（主题则立即变；布局会重建按键）
        private void ApplyPresetByName(string name, string type)
        {
            try
            {
                var root = ParsePresetsRoot();
                if (root == null) { PresetStatusSet("预设数据不可用"); return; }
                string arrName = type == "layout" ? "layoutPresets" : "themePresets";
                if (!root.TryGetValue(arrName, out var av)) return;
                foreach (var item in av.GetArray())
                {
                    var o = item.GetObject();
                    string nm = o.ContainsKey("name") ? o.GetNamedString("name") : "";
                    if (nm != name) continue;
                    var data = o.GetNamedObject("data");
                    var v = ApplicationData.Current.LocalSettings.Values;

                    if (type == "theme")
                    {
                        string theme = data.ContainsKey("theme") ? data.GetNamedString("theme") : "dark";
                        v["Theme"] = theme;
                        if (data.ContainsKey("colors"))
                        {
                            var colors = data.GetNamedObject("colors");
                            // 字段顺序 = CustomKeys 顺序；0.9.5 新增第 9 项 "accent"。
                            // 旧预设（只有 8 项、没有 accent）靠 ContainsKey 判断跳过，向后兼容。
                            string[] fields = { "panel", "border", "keyBg", "keyFg", "pressedBg", "pressedFg", "pad", "dot", "accent", "dotPressed" };
                            for (int k = 0; k < fields.Length && k < CustomKeys.Length; k++)
                            {
                                if (colors.ContainsKey(fields[k]))
                                    v[CustomKeys[k]] = colors.GetNamedString(fields[k]);
                            }
                        }
                    }
                    else
                    {
                        if (data.ContainsKey("keyOpacity")) v["KeyOpacity_"] = (int)data.GetNamedNumber("keyOpacity");
                        if (data.ContainsKey("padVisible")) v["PadVisible_"] = data.GetNamedBoolean("padVisible") ? 1 : 0;
                        if (data.ContainsKey("padW") && data.ContainsKey("padH"))
                        {
                            double pw = data.GetNamedNumber("padW");
                            double ph = data.GetNamedNumber("padH");
                            if (pw > 0 && ph > 0) { v["PadCustom_"] = 1; v["PadW"] = pw.ToString(CultureInfo.InvariantCulture); v["PadH"] = ph.ToString(CultureInfo.InvariantCulture); }
                        }
                        if (data.ContainsKey("keys"))
                        {
                            var keys = data.GetNamedObject("keys");
                            foreach (var kv in keys) v[kv.Key] = kv.Value.GetString();
                        }
                        if (data.ContainsKey("customKeys"))
                        {
                            var cks = data.GetNamedObject("customKeys");
                            // 先清掉现有自定义键，再按预设重建
                            var rm = new System.Collections.Generic.List<string>();
                            foreach (var kv in v)
                                if (kv.Key.StartsWith("Custom_", StringComparison.Ordinal) || kv.Key.StartsWith("CustomPos_", StringComparison.Ordinal) ||
                                    kv.Key.StartsWith("CustomSize_", StringComparison.Ordinal) || kv.Key.StartsWith("DisplayName_", StringComparison.Ordinal))
                                    rm.Add(kv.Key);
                            foreach (var k in rm) v.Remove(k);
                            foreach (var kv in cks)
                            {
                                var e2 = kv.Value.GetObject();
                                v["Custom_" + kv.Key] = "1";
                                v["CustomPos_" + kv.Key] = e2.ContainsKey("pos") ? e2.GetNamedString("pos") : "0;0";
                                if (e2.ContainsKey("size")) v["CustomSize_" + kv.Key] = e2.GetNamedString("size");
                                if (e2.ContainsKey("displayName") && !string.IsNullOrEmpty(e2.GetNamedString("displayName")))
                                    v["DisplayName_" + kv.Key] = e2.GetNamedString("displayName");
                            }
                        }
                        if (data.ContainsKey("deletedKeys"))
                        {
                            var rm2 = new System.Collections.Generic.List<string>();
                            foreach (var kv in v) if (kv.Key.StartsWith("Deleted_", StringComparison.Ordinal)) rm2.Add(kv.Key);
                            foreach (var k in rm2) v.Remove(k);
                            foreach (var dk in data.GetNamedArray("deletedKeys")) v["Deleted_" + dk.GetString()] = 1;
                        }
                    }

                    ApplicationData.Current.SignalDataChanged();
                    PresetStatusSet("已应用" + (type == "layout" ? "布局" : "主题") + "预设：" + name);
                    if (type == "theme") { BuildPalette(); ApplyPalette(); RefreshSlotRows(); }
                    return;
                }
                PresetStatusSet("未找到预设：" + name);
            }
            catch (Exception ex) { PresetStatusSet("应用预设失败：" + ex.Message); }
        }

        // 保存当前状态为预设（名称取自输入框）
        private void SaveThemePreset_Click(object sender, RoutedEventArgs e) { SaveCurrentPreset("theme"); }
        private void SaveLayoutPreset_Click(object sender, RoutedEventArgs e) { SaveCurrentPreset("layout"); }

        // ===================== 0.9.5：向小组件索取"真实布局快照" =====================
        // 设置窗口自己的 LocalSettings 视图可能滞后（实测过 "removed 0 keys"），直接枚举会保存出旧值。
        // 这里写请求标记 → 等小组件把真实布局写进 LocalFolder\layout-snapshot.json → 读该文件（文件不走缓存）。
        private async System.Threading.Tasks.Task<Windows.Data.Json.JsonObject> RequestLayoutSnapshotAsync()
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                long stamp = DateTime.UtcNow.Ticks;
                v["LayoutSnapshotRequest_"] = stamp;
                ApplicationData.Current.SignalDataChanged();
                string path = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "layout-snapshot.json");
                for (int i = 0; i < 25; i++)   // 最多约 0.5 秒（原来是 2 秒，保存太慢）
                {
                    await System.Threading.Tasks.Task.Delay(20);
                    try
                    {
                        if (!System.IO.File.Exists(path)) continue;
                        var o = Windows.Data.Json.JsonObject.Parse(System.IO.File.ReadAllText(path));
                        if (o.ContainsKey("stamp") && (long)o.GetNamedNumber("stamp") == stamp) return o;
                    }
                    catch { }
                }
                Diag("snapshot timeout (widget not running?)");
            }
            catch (Exception ex) { Diag("snapshot request fail: " + ex.Message); }
            return null;
        }

        // 把快照里的字段搬进预设 data（键/自定义键/隐藏键/透明度/鼠标垫全部取小组件真实值）
        private static Windows.Data.Json.JsonObject BuildLayoutDataFromSnapshot(Windows.Data.Json.JsonObject snap)
        {
            var data = new Windows.Data.Json.JsonObject();
            if (snap.ContainsKey("keyOpacity")) data.SetNamedValue("keyOpacity", Windows.Data.Json.JsonValue.CreateNumberValue(snap.GetNamedNumber("keyOpacity")));
            if (snap.ContainsKey("padVisible")) data.SetNamedValue("padVisible", Windows.Data.Json.JsonValue.CreateBooleanValue(snap.GetNamedBoolean("padVisible")));
            string[] padNum = { "padW", "padH", "padPosX", "padPosY" };
            foreach (var f in padNum)
                if (snap.ContainsKey(f)) data.SetNamedValue(f, Windows.Data.Json.JsonValue.CreateNumberValue(snap.GetNamedNumber(f)));
            string[] objs = { "keys", "customKeys" };
            foreach (var f in objs)
                if (snap.ContainsKey(f)) data.SetNamedValue(f, snap.GetNamedObject(f));
            if (snap.ContainsKey("deletedKeys")) data.SetNamedValue("deletedKeys", snap.GetNamedArray("deletedKeys"));
            return data;
        }
        private async void SaveCurrentPreset(string type)
        {
            try
            {
                string name = (PresetNameBox.Text ?? "").Trim();
                if (name.Length == 0) { PresetStatusSet("请先填写预设名称"); return; }
                var root = ParsePresetsRoot() ?? new Windows.Data.Json.JsonObject();
                if (!root.ContainsKey("version")) root.SetNamedValue("version", Windows.Data.Json.JsonValue.CreateNumberValue(1));
                string arrName = type == "layout" ? "layoutPresets" : "themePresets";
                Windows.Data.Json.JsonArray arr;
                if (root.ContainsKey(arrName)) arr = root.GetNamedArray(arrName);
                else { arr = new Windows.Data.Json.JsonArray(); root.SetNamedValue(arrName, arr); }

                var v = ApplicationData.Current.LocalSettings.Values;
                var data = new Windows.Data.Json.JsonObject();
                if (type == "theme")
                {
                    data.SetNamedValue("theme", Windows.Data.Json.JsonValue.CreateStringValue((v["Theme"] as string) ?? "dark"));
                    var colors = new Windows.Data.Json.JsonObject();
                    string[] fields = { "panel", "border", "keyBg", "keyFg", "pressedBg", "pressedFg", "pad", "dot", "accent", "dotPressed" };
                    for (int k = 0; k < fields.Length && k < CustomKeys.Length; k++) colors.SetNamedValue(fields[k], Windows.Data.Json.JsonValue.CreateStringValue(SlotText(k)));
                    data.SetNamedValue("colors", colors);
                }
                else
                {
                    // 0.9.5：优先使用小组件的真实布局快照（避免保存出旧值）
                    var snap = await RequestLayoutSnapshotAsync();
                    if (snap != null)
                    {
                        data = BuildLayoutDataFromSnapshot(snap);
                        PresetStatusSet(snap.ContainsKey("stamp") ? "已从小组件读取真实布局快照" : "已读取布局快照");
                    }
                    else
                    {
                    data.SetNamedValue("keyOpacity", Windows.Data.Json.JsonValue.CreateNumberValue(ReadDouble(v["KeyOpacity_"], 100, 10, 100)));
                    data.SetNamedValue("padVisible", Windows.Data.Json.JsonValue.CreateBooleanValue(!(v["PadVisible_"] != null && v["PadVisible_"].ToString() == "0")));
                    // 0.9.5：布局预设补写鼠标垫尺寸/位置（此前漏了 → 导出的预设不带垫子信息，
                    // 导致无法用导出文件同步默认布局的鼠标垫大小与位置）
                    try
                    {
                        double pw, ph, px, py;
                        if (double.TryParse(v["PadW"] as string, NumberStyles.Float, CultureInfo.InvariantCulture, out pw) &&
                            double.TryParse(v["PadH"] as string, NumberStyles.Float, CultureInfo.InvariantCulture, out ph) && pw > 0 && ph > 0)
                        {
                            data.SetNamedValue("padW", Windows.Data.Json.JsonValue.CreateNumberValue(pw));
                            data.SetNamedValue("padH", Windows.Data.Json.JsonValue.CreateNumberValue(ph));
                            if (double.TryParse(v["PadPos_left"] as string, NumberStyles.Float, CultureInfo.InvariantCulture, out px))
                                data.SetNamedValue("padPosX", Windows.Data.Json.JsonValue.CreateNumberValue(px));
                            if (double.TryParse(v["PadPos_top"] as string, NumberStyles.Float, CultureInfo.InvariantCulture, out py))
                                data.SetNamedValue("padPosY", Windows.Data.Json.JsonValue.CreateNumberValue(py));
                        }
                    }
                    catch { }
                    var keys = new Windows.Data.Json.JsonObject();
                    var ckeys = new Windows.Data.Json.JsonObject();
                    var deleted = new Windows.Data.Json.JsonArray();
                    foreach (var kv in v)
                    {
                        if (kv.Key.StartsWith("Layout_", StringComparison.Ordinal)) keys.SetNamedValue(kv.Key, Windows.Data.Json.JsonValue.CreateStringValue(kv.Value.ToString()));
                        else if (kv.Key.StartsWith("Custom_", StringComparison.Ordinal))
                        {
                            string kn = kv.Key.Substring("Custom_".Length);
                            var ko = new Windows.Data.Json.JsonObject();
                            ko.SetNamedValue("pos", Windows.Data.Json.JsonValue.CreateStringValue((v["CustomPos_" + kn] as string) ?? "0;0"));
                            ko.SetNamedValue("size", Windows.Data.Json.JsonValue.CreateStringValue((v["CustomSize_" + kn] as string) ?? "52;48"));
                            string dn = v["DisplayName_" + kn] as string;
                            if (!string.IsNullOrEmpty(dn)) ko.SetNamedValue("displayName", Windows.Data.Json.JsonValue.CreateStringValue(dn));
                            ckeys.SetNamedValue(kn, ko);
                        }
                        else if (kv.Key.StartsWith("Deleted_", StringComparison.Ordinal))
                            deleted.Add(Windows.Data.Json.JsonValue.CreateStringValue(kv.Key.Substring("Deleted_".Length)));
                    }
                    data.SetNamedValue("keys", keys);
                    data.SetNamedValue("customKeys", ckeys);
                    data.SetNamedValue("deletedKeys", deleted);
                    }
                }

                var entry = new Windows.Data.Json.JsonObject();
                entry.SetNamedValue("name", Windows.Data.Json.JsonValue.CreateStringValue(name));
                entry.SetNamedValue("type", Windows.Data.Json.JsonValue.CreateStringValue(type));
                entry.SetNamedValue("savedAt", Windows.Data.Json.JsonValue.CreateStringValue(DateTime.Now.ToString("s")));
                entry.SetNamedValue("data", data);
                // 0.9.5（用户要求）：重名不再追加 (1)/(2) 副本，直接覆盖同名预设（保留原有顺序）
                bool replaced = false;
                for (int i = 0; i < arr.Count; i++)
                {
                    var o2 = arr[i].GetObject();
                    string nm2 = o2.ContainsKey("name") ? o2.GetNamedString("name") : "";
                    if (nm2 == name) { arr[i] = entry; replaced = true; break; }
                }
                if (!replaced) arr.Add(entry);

                var resp = await PresetRequestAsync("PUT_PRESETS", root.Stringify());
                if (resp != null && resp.StartsWith("OK")) { PresetStatusSet("已保存预设：" + name); await LoadPresetsAsync(); }
                else PresetStatusSet("保存失败：" + (resp ?? "伴生进程不可用"));
            }
            catch (Exception ex) { PresetStatusSet("保存预设异常：" + ex.Message); }
        }

        private async void DeletePresetByName(string name, string type)
        {
            try
            {
                var root = ParsePresetsRoot();
                if (root == null) { PresetStatusSet("预设数据不可用"); return; }
                string arrName = type == "layout" ? "layoutPresets" : "themePresets";
                if (!root.ContainsKey(arrName)) return;
                var old = root.GetNamedArray(arrName);
                var kept = new Windows.Data.Json.JsonArray();
                foreach (var item in old)
                {
                    var o = item.GetObject();
                    string nm = o.ContainsKey("name") ? o.GetNamedString("name") : "";
                    if (nm != name) kept.Add(o);
                }
                root.SetNamedValue(arrName, kept);
                var resp = await PresetRequestAsync("PUT_PRESETS", root.Stringify());
                if (resp != null && resp.StartsWith("OK")) { PresetStatusSet("已删除预设：" + name); await LoadPresetsAsync(); }
                else PresetStatusSet("删除失败：" + (resp ?? "伴生进程不可用"));
            }
            catch (Exception ex) { PresetStatusSet("删除预设异常：" + ex.Message); }
        }

        private static void Diag(string msg)
        {
            try
            {
                var dir = ApplicationData.Current.LocalFolder.Path;
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "diag.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " [settings] " + msg + "\r\n");
            }
            catch { }
        }
    }
}
