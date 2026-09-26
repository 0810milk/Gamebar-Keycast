using System;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace KeyDisplay
{
    /// <summary>
    /// 动态右键菜单系统：三种菜单形态均由代码动态构建——右键按键（删除/复制/修改显示名）、
    /// 右键鼠标垫（隐藏鼠标垫）、右键键区空白（粘贴 / 显示鼠标垫，动态项按状态出现）。
    /// partial 文件，与 Widget1.xaml.cs 共享同一命名空间与类声明（public sealed partial class Widget1 : Page）。
    /// 菜单项容器为 XAML 的 KeyMenuItems（空 StackPanel），每次打开前 Clear 后按需重建。
    /// 删除/复制/改名的具体流程分别由 Widget1.xaml.cs 的 ConfirmDeleteKey、
    /// Widget1.KeyCopyPaste.cs 的 CopySelectedKey/PasteKeyAt、Widget1.KeyRename.cs 的 OpenKeyRename 提供；
    /// 隐藏/显示鼠标垫由 Widget1.xaml.cs 的 HidePad/ShowPad 提供。
    /// </summary>
    public sealed partial class Widget1 : Page
    {
        // 当前右键的按键（仅键菜单使用；菜单关闭后清空）
        private Border _ctxKey;

        // 空白右键位置（KeyLayer 坐标），供「粘贴」项闭包捕获——菜单项点击发生在菜单上，
        // 必须用右键时的位置而不是点击位置来定位新键。
        private Point _blankPos;

        // 动态构建一个菜单项（0.9.5 Windows 11 风格）：
        //   高度 32、圆角 4、无边框、左对齐 13px 文字、左侧 16px 图标（Segoe Fluent Icons / MDL2）、
        //   悬停为轻微叠加色、按下更明显；danger=true 时图标与文字用柔和红（删除类操作，Win11 同款语义）。
        // 保留 2 参数重载：旧调用点只给文字，自动不带图标。
        private void AddMenuItem(string text, Action action)
        {
            AddMenuItem(text, null, action, false);
        }

        // Win11 菜单常用图标（Segoe Fluent Icons / Segoe MDL2 Assets 同码位）
        private const string GlyphSelectAll = "\uE8B3";
        private const string GlyphDelete = "\uE74D";
        private const string GlyphCopy = "\uE8C8";
        private const string GlyphRename = "\uE70F";
        private const string GlyphPaste = "\uE77F";
        private const string GlyphCancel = "\uE711";

        private void AddMenuItem(string text, string glyph, Action action, bool danger)
        {
            var b = new Border
            {
                Height = 32,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 2, 0, 0),
                Background = TransparentBrush
            };
            if (KeyMenuItems.Children.Count == 0) b.Margin = new Thickness(0);

            var grid = new Grid { Padding = new Thickness(10, 0, 10, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var fg = danger ? MenuDangerB() : KeyFgB();
            var icon = new TextBlock
            {
                Text = string.IsNullOrEmpty(glyph) ? "" : glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Opacity = 0.9,
                Foreground = fg
            };
            var label = new TextBlock
            {
                Text = text,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = fg
            };
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(label, 1);
            grid.Children.Add(icon);
            grid.Children.Add(label);
            b.Child = grid;

            // 悬停/按下反馈（Win11 菜单项的关键手感）
            b.PointerEntered += (s, e) => { try { b.Background = MenuHoverB(); } catch { } };
            b.PointerExited += (s, e) => { try { b.Background = TransparentBrush; } catch { } };
            b.PointerPressed += (s, e) => { try { b.Background = MenuPressedB(); } catch { } };
            b.PointerReleased += (s, e) => { try { b.Background = MenuHoverB(); } catch { } };

            b.Tapped += (s, e) =>
            {
                // 0.8.3：Handled 置位优先于 action——action 抛异常（如 LocalSettings 写满）时
                // 也必须阻止冒泡关菜单，避免遮挡残留；异常只记日志不崩进程
                try { e.Handled = true; action(); }
                catch (Exception ex) { DiagLog("menu action error: " + ex.Message); }
            };
            ApplyMenuBorder(b);
            KeyMenuItems.Children.Add(b);
        }

        // Win11 菜单的分组分隔线（1px，左右留白）
        private void AddMenuSeparator()
        {
            var line = new Border
            {
                Height = 1,
                Margin = new Thickness(10, 4, 10, 4),
                Background = FloatBorderB()
            };
            KeyMenuItems.Children.Add(line);
        }

        // 统一显示逻辑：把 KeyMenu 定位到鼠标附近并显示覆盖层。
        // layerPos 为 KeyLayer **本地（未缩放）**坐标；覆盖层 KeyMenuPanel 与 KeyLayer 左上角对齐，
        // 但它不参与 KeyLayer 的缩放，所以定位时必须乘上当前 _keyScale，否则窗口缩放后菜单位置会偏。
        private void ShowMenu(Point layerPos)
        {
            _menuAnchorLocal = layerPos;
            _menuAnchorValid = true;
            PositionMenu();
            KeyMenuPanel.Visibility = Visibility.Visible;
            FadeIn(KeyMenuPanel);   // 0.8.2 弹层淡入
            ApplyKeyMenuTheme();
        }

        // 菜单锚点（KeyLayer 本地坐标）与位置重算：窗口尺寸/键区缩放变化时由 FitLayoutToWindow 调用，
        // 保证菜单始终贴在右键的位置（原来只在打开时算一次，缩放窗口后就不跟随了）。
        private Point _menuAnchorLocal;
        private bool _menuAnchorValid;

        private void PositionMenu()
        {
            try
            {
                if (!_menuAnchorValid || KeyMenu == null) return;
                double s = (_keyScale != null && _keyScale.ScaleX > 0.05) ? _keyScale.ScaleX : 1.0;
                double mx = Math.Max(4, _menuAnchorLocal.X * s + 8 + KeyLayer.Margin.Left);
                double my = Math.Max(4, _menuAnchorLocal.Y * s + 8);

                double winW = KeyMenuPanel.ActualWidth > 0 ? KeyMenuPanel.ActualWidth : 340;
                double winH = KeyMenuPanel.ActualHeight > 0 ? KeyMenuPanel.ActualHeight : 240;
                double menuW = KeyMenu.ActualWidth > 0 ? KeyMenu.ActualWidth : KeyMenu.Width;
                double menuH = KeyMenu.ActualHeight > 0 ? KeyMenu.ActualHeight : 200;
                mx = Math.Min(mx, Math.Max(4, winW - menuW - 8));
                my = Math.Min(my, Math.Max(4, winH - menuH - 8));

                KeyMenu.Margin = new Thickness(mx, my, 0, 0);
                KeyMenu.HorizontalAlignment = HorizontalAlignment.Left;
                KeyMenu.VerticalAlignment = VerticalAlignment.Top;
            }
            catch { }
        }

        // 右键按键菜单：记录目标键，清空并重建「删除/复制/修改显示名」三项。
        // 动作闭包在菜单项点击时才执行，故先取 _ctxKey 快照再关菜单。
        private void ShowKeyContextMenu(Border key, Point layerPos)
        {
            // 0.9.4 多选模式：右键**只负责弹出批量菜单**，不改变选择——
            // 选择的唯一方式是左键单击（用户明确要求：进入多选后到其他按键应该是左键）
            if (_multiSelectMode)
            {
                ShowMultiSelectMenu(layerPos);
                return;
            }
            _ctxKey = key;
            CancelLongPress();   // 打开菜单时指针仍按在键上，先取消长按计时避免残留
            KeyMenuItems.Children.Clear();

            // 0.9.4 多选入口：进入多选模式，并把当前右键的这个键直接选中（第一个选中项）
            AddMenuItem("多选", GlyphSelectAll, () =>
            {
                var k = _ctxKey;
                CloseKeyContextMenu();
                EnterMultiSelectMode(k);
            }, false);

            // 删除：复用 DeleteConfirmPanel 三段式确认框（与 0.8.1 前行为一致），仅自定义键可删
            AddMenuItem("删除", GlyphDelete, () =>
            {
                var k = _ctxKey;
                CloseKeyContextMenu();
                if (k != null)
                {
                    string nm = NameOf(k);
                    if (!string.IsNullOrEmpty(nm) && nm != "?" && nm != "Pad")
                    {
                        _deleteConfirmKey = k;
                        _multiDeletePending = false;
                        DeleteConfirmText.Text = "删除控件 " + nm + " ？";
                        DeleteConfirmPanel.Visibility = Visibility.Visible;
                        FadeIn(DeleteConfirmPanel);   // 0.8.2 弹层淡入
                        DiagLog("delete confirm: " + nm);
                    }
                }
            }, false);

            // 复制：复制按键布局（实现位于 Widget1.KeyCopyPaste.cs，签名 private void CopySelectedKey(Border key)）
            AddMenuItem("复制", GlyphCopy, () =>
            {
                var k = _ctxKey;
                CloseKeyContextMenu();
                if (k != null) CopySelectedKey(k);
            }, false);

            // 修改显示名：打开改名输入框（实现位于 Widget1.KeyRename.cs，签名 private void OpenKeyRename(Border key)）
            AddMenuItem("修改显示名", GlyphRename, () =>
            {
                var k = _ctxKey;
                CloseKeyContextMenu();
                if (k != null) OpenKeyRename(k);
            }, false);

            ShowMenu(layerPos);
            DiagLog("key menu open: " + NameOf(key));
        }

        // 右键鼠标垫菜单：清空并重建单项「隐藏鼠标垫」
        private void ShowPadContextMenu(Point layerPos)
        {
            CancelLongPress();
            KeyMenuItems.Children.Clear();
            AddMenuItem("隐藏鼠标垫", null, () =>
            {
                CloseKeyContextMenu();
                HidePad();
            }, false);
            ShowMenu(layerPos);
            DiagLog("pad menu open");
        }

        // 右键键区空白菜单：动态项——剪贴板非空显示「粘贴」，Pad 隐藏时显示「显示鼠标垫」；
        // 两项都没有（剪贴板空且 Pad 可见）则不显示菜单。
        private void ShowBlankContextMenu(Point layerPos)
        {
            CancelLongPress();
            _blankPos = layerPos;
            KeyMenuItems.Children.Clear();
            // 0.9.4 粘贴来源优先级（用户用法：多选后直接在空白处粘贴，无需先点"复制"）：
            //   ① 当前多选集合（多选模式且已选 ≥1）→ 粘贴这些按键（保留相对布局）
            //   ② 组剪贴板（此前"复制（N 个）"的结果）
            //   ③ 单键剪贴板（单个"复制"的结果）
            int selCount = _multiSelectMode ? _selectedKeys.Count : 0;
            bool hasGroup = _keyGroupClipboard != null && _keyGroupClipboard.Count > 0;
            bool hasSingle = _keyClipboard != null;
            bool paste = selCount > 0 || hasGroup || hasSingle;
            bool showpad = !_padVisible;
            if (!paste && !showpad) return;

            if (paste)
            {
                string label = selCount > 0 ? ("粘贴选中的 " + selCount + " 个按键")
                    : hasGroup ? ("粘贴（" + _keyGroupClipboard.Count + " 个）")
                    : "粘贴";
                AddMenuItem(label, GlyphPaste, () =>
                {
                    CloseKeyContextMenu();
                    if (selCount > 0) PasteSelectionAt(_blankPos);
                    else if (hasGroup) PasteGroupAt(_blankPos);
                    else PasteKeyAt(_blankPos);
                }, false);
            }

            // 显示鼠标垫（实现位于 Widget1.xaml.cs，签名 private void ShowPad()）
            if (showpad) AddMenuItem("显示鼠标垫", null, () =>
            {
                CloseKeyContextMenu();
                ShowPad();
            }, false);

            ShowMenu(layerPos);
            DiagLog("blank menu open: paste=" + paste + " showpad=" + showpad);
        }

        // 关闭右键菜单：隐藏覆盖层并清空当前按键（记录关闭时刻供多选防误触）
        private void CloseKeyContextMenu()
        {
            KeyMenuPanel.Visibility = Visibility.Collapsed;
            _ctxKey = null;
            _lastKeyMenuCloseTicks = DateTime.UtcNow.Ticks;
        }

        // 点遮罩（菜单框外）：关闭菜单
        private void KeyMenuPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            CloseKeyContextMenu();
        }

        // 点菜单框内部：标记已处理，阻止冒泡到遮罩触发关闭（与 LockMenu_Tapped 同模式）
        private void KeyMenu_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        // 刷新右键菜单主题配色（0.9.5 Win11 风格）：菜单框用浮层派生色 + 圆角 + 柔和投影；
        // 菜单项在 AddMenuItem 内各自着色，悬停/按下色由 MenuHoverB/MenuPressedB 现算
        private void ApplyKeyMenuTheme()
        {
            KeyMenu.Background = FloatPanelB();
            KeyMenu.BorderBrush = FloatBorderB();
            KeyMenu.CornerRadius = new CornerRadius(8);
            try
            {
                // Win11 菜单有柔和外投影：UWP 1903+ 支持 ThemeShadow（需要 Translation.Z > 0），失败就退化
                KeyMenu.Shadow = new Windows.UI.Xaml.Media.ThemeShadow();
                KeyMenu.Translation = new System.Numerics.Vector3(0, 0, 32);
            }
            catch { }
        }

        // 菜单项底色：默认透明（Win11 无边框无填充），悬停/按下用轻微叠加
        private static readonly Windows.UI.Xaml.Media.SolidColorBrush TransparentBrush =
            new Windows.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);

        private Windows.UI.Xaml.Media.Brush MenuHoverB()
        {
            var c = PanelColorOf(PanelB());
            bool dark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 < 0.5;
            return new Windows.UI.Xaml.Media.SolidColorBrush(dark
                ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        }

        private Windows.UI.Xaml.Media.Brush MenuPressedB()
        {
            var c = PanelColorOf(PanelB());
            bool dark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 < 0.5;
            return new Windows.UI.Xaml.Media.SolidColorBrush(dark
                ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x22, 0x00, 0x00, 0x00));
        }

        // 危险项（删除类）的红色：深色底用柔和亮红、浅色底用 Win11 的 #C42B1C
        private Windows.UI.Xaml.Media.Brush MenuDangerB()
        {
            var c = PanelColorOf(PanelB());
            bool dark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 < 0.5;
            return new Windows.UI.Xaml.Media.SolidColorBrush(dark
                ? Color.FromArgb(0xFF, 0xFF, 0x99, 0xA4)
                : Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
        }

        // 从 Brush 取 Color（非 SolidColorBrush 时回落深色面板）
        private static Color PanelColorOf(Windows.UI.Xaml.Media.Brush b)
        {
            var s = b as Windows.UI.Xaml.Media.SolidColorBrush;
            return s != null ? s.Color : Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F);
        }

        // 主题/配色变化时刷新已打开的右键菜单（0.9.5 修复：原来只有打开菜单时才上色，
        // 菜单开着改主题不会跟着变，必须重新打开才看到）
        private void RefreshKeyMenuColors()
        {
            try
            {
                ApplyKeyMenuTheme();
                foreach (var ch in KeyMenuItems.Children)
                {
                    var b = ch as Border;
                    if (b == null) continue;
                    if (b.Height == 1) { b.Background = FloatBorderB(); continue; }   // 分隔线
                    b.Background = TransparentBrush;                                   // 复位悬停/按下残留
                    var grid = b.Child as Grid;
                    if (grid == null) continue;
                    foreach (var c2 in grid.Children)
                    {
                        var tb = c2 as TextBlock;
                        if (tb != null && !IsDangerForeground(tb.Foreground)) tb.Foreground = KeyFgB();
                    }
                }
            }
            catch { }
        }

        // 单个菜单项：底色/边框沿用浮层派生色系（0.8.2）；0.9.5 起项内是 Grid（图标+文字），
        // 需递归给其中的 TextBlock 上色（危险项保持自己的红色，不覆盖）
        private void ApplyMenuBorder(Border b)
        {
            if (b.BorderThickness.Top > 0)
            {
                b.Background = FloatPanelB();
                b.BorderBrush = FloatBorderB();
            }
            var fg = KeyFgB();
            var grid = b.Child as Grid;
            if (grid == null) return;
            foreach (var ch in grid.Children)
            {
                var tb = ch as TextBlock;
                if (tb != null && !IsDangerForeground(tb.Foreground)) tb.Foreground = fg;
            }
        }

        // 危险项判定：红的 G/B 明显低于 R，且不是主题前景色 → 视为危险色，主题刷新时不覆盖
        private static bool IsDangerForeground(Windows.UI.Xaml.Media.Brush brush)
        {
            var s = brush as Windows.UI.Xaml.Media.SolidColorBrush;
            if (s == null) return false;
            var c = s.Color;
            return c.R > 0x80 && c.G < 0xC0 && c.B < 0xC0 && (c.R - c.G) > 0x40;
        }
    }
}