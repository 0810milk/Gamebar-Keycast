using System;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

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

        // 动态构建一个菜单项：30 高、圆角 6、1px 边框；第一个项顶部 Margin 0，其余顶部 4。
        // 子元素为居中 TextBlock；点击执行传入动作并标记 Handled 阻止冒泡到遮罩。
        private void AddMenuItem(string text, Action action)
        {
            var b = new Border
            {
                Height = 30,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 0)
            };
            if (KeyMenuItems.Children.Count == 0) b.Margin = new Thickness(0);
            b.Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            b.Tapped += (s, e) => { action(); e.Handled = true; };
            ApplyMenuBorder(b);
            KeyMenuItems.Children.Add(b);
        }

        // 统一显示逻辑：把 KeyMenu 定位到鼠标附近并显示覆盖层。
        // layerPos 为 KeyLayer 坐标；KeyMenuPanel 覆盖层 Grid 与 KeyLayer 左上角对齐（同为根 Grid 的直接子元素、Row0 起点），
        // 所以直接用 KeyMenu.Margin 的 Left/Top 即可完成定位。
        private void ShowMenu(Point layerPos)
        {
            // 定位：下界保 4px，上界按窗口可视区钳制（菜单 130 宽 × 约 120 高，防右下角右键时溢出被裁剪）
            double mx = Math.Max(4, layerPos.X + 8);
            double my = Math.Max(4, layerPos.Y + 8);
            double winW = KeyMenuPanel.ActualWidth > 0 ? KeyMenuPanel.ActualWidth : 340;
            double winH = KeyMenuPanel.ActualHeight > 0 ? KeyMenuPanel.ActualHeight : 240;
            mx = Math.Min(mx, Math.Max(4, winW - 134));
            my = Math.Min(my, Math.Max(4, winH - 120));
            KeyMenu.Margin = new Thickness(mx, my, 0, 0);
            KeyMenu.HorizontalAlignment = HorizontalAlignment.Left;
            KeyMenu.VerticalAlignment = VerticalAlignment.Top;
            KeyMenuPanel.Visibility = Visibility.Visible;
            FadeIn(KeyMenuPanel);   // 0.8.2 弹层淡入
            ApplyKeyMenuTheme();
        }

        // 右键按键菜单：记录目标键，清空并重建「删除/复制/修改显示名」三项。
        // 动作闭包在菜单项点击时才执行，故先取 _ctxKey 快照再关菜单。
        private void ShowKeyContextMenu(Border key, Point layerPos)
        {
            _ctxKey = key;
            CancelLongPress();   // 打开菜单时指针仍按在键上，先取消长按计时避免残留
            KeyMenuItems.Children.Clear();

            // 删除：复用 DeleteConfirmPanel 三段式确认框（与 0.8.1 前行为一致），仅自定义键可删
            AddMenuItem("删除", () =>
            {
                var k = _ctxKey;
                CloseKeyContextMenu();
                if (k != null)
                {
                    string nm = NameOf(k);
                    if (!string.IsNullOrEmpty(nm) && nm != "?" && nm != "Pad")
                    {
                        _deleteConfirmKey = k;
                        DeleteConfirmText.Text = "删除控件 " + nm + " ？";
                        DeleteConfirmPanel.Visibility = Visibility.Visible;
                        FadeIn(DeleteConfirmPanel);   // 0.8.2 弹层淡入
                        DiagLog("delete confirm: " + nm);
                    }
                }
            });

            // 复制：复制按键布局（实现位于 Widget1.KeyCopyPaste.cs，签名 private void CopySelectedKey(Border key)）
            AddMenuItem("复制", () =>
            {
                var k = _ctxKey;
                CloseKeyContextMenu();
                if (k != null) CopySelectedKey(k);
            });

            // 修改显示名：打开改名输入框（实现位于 Widget1.KeyRename.cs，签名 private void OpenKeyRename(Border key)）
            AddMenuItem("修改显示名", () =>
            {
                var k = _ctxKey;
                CloseKeyContextMenu();
                if (k != null) OpenKeyRename(k);
            });

            ShowMenu(layerPos);
            DiagLog("key menu open: " + NameOf(key));
        }

        // 右键鼠标垫菜单：清空并重建单项「隐藏鼠标垫」
        private void ShowPadContextMenu(Point layerPos)
        {
            CancelLongPress();
            KeyMenuItems.Children.Clear();
            AddMenuItem("隐藏鼠标垫", () =>
            {
                CloseKeyContextMenu();
                HidePad();
            });
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
            bool paste = _keyClipboard != null;
            bool showpad = !_padVisible;
            if (!paste && !showpad) return;

            // 粘贴：位置用右键时的 _blankPos（而非菜单点击位置）
            if (paste) AddMenuItem("粘贴", () =>
            {
                CloseKeyContextMenu();
                PasteKeyAt(_blankPos);
            });

            // 显示鼠标垫（实现位于 Widget1.xaml.cs，签名 private void ShowPad()）
            if (showpad) AddMenuItem("显示鼠标垫", () =>
            {
                CloseKeyContextMenu();
                ShowPad();
            });

            ShowMenu(layerPos);
            DiagLog("blank menu open: paste=" + paste + " showpad=" + showpad);
        }

        // 关闭右键菜单：隐藏覆盖层并清空当前按键
        private void CloseKeyContextMenu()
        {
            KeyMenuPanel.Visibility = Visibility.Collapsed;
            _ctxKey = null;
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

        // 刷新右键菜单主题配色：菜单框应用浮层派生色（0.8.2 起，避免与面板/按键同色融为一体）；
        // 菜单项在 AddMenuItem 内各自 ApplyMenuBorder
        private void ApplyKeyMenuTheme()
        {
            KeyMenu.Background = FloatPanelB();
            KeyMenu.BorderBrush = FloatBorderB();
        }

        // 单个菜单项：底/边框沿用浮层派生色（与菜单框同一色系，0.8.2）；子元素是 TextBlock 才设前景（非 TextBlock 跳过）
        private void ApplyMenuBorder(Border b)
        {
            b.Background = FloatPanelB();
            b.BorderBrush = FloatBorderB();
            if (b.Child is TextBlock tb) tb.Foreground = KeyFgB();
        }
    }
}