// Widget1.KeyCopyPaste.cs —— 0.8.1 按键"复制/粘贴"功能（partial 文件，不改动主文件）
//
// 交互模型：
//   复制：外部入口（父代理将来扩展，如长按菜单/按键选中）调用 CopySelectedKey(border)，
//         把按键的 内部名 + 显示名 + 宽高 存入 _keyClipboard。
//   粘贴：键区空白处右键（KeyLayer.PointerPressed）弹空白菜单（0.8.2：ShowBlankContextMenu，
//         粘贴/显示鼠标垫由菜单项提供，不再直接粘贴）。因为按键自身的 Key_PointerPressed
//         在右键时已 e.Handled=true 阻止冒泡，所以 KeyLayer 的 PointerPressed 只会在
//         空白区域触发，这正是弹出菜单的入口。菜单「粘贴」调用 PasteKeyAt，粘贴点 = 鼠标点击位置，新键中心对准该点。
//
// 位置换算：CustomKeysPanel 是 KeyLayer 的子 Canvas，位于 KeyLayer 的 (0,224)；
//           自定义键定位 = 面板内 Canvas.Left/Top 恒为 0 + TranslateTransform（RenderTransform）。
//           所以 KeyLayer 坐标 (x,y) 对应的面板内位置是 (x, y-224)。

using System;
using System.Globalization;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace KeyDisplay
{
    public sealed partial class Widget1 : Page
    {
        // 剪贴板数据：一次复制快照（内部名用于 VK 映射/持久化键名，显示名用于恢复显示文本）
        private sealed class KeyClipboardData
        {
            public string InternalName;
            public string DisplayName;
            public double Width;
            public double Height;
        }

        private KeyClipboardData _keyClipboard;   // null = 剪贴板空
        private bool _pasteHookReady;             // 防重复订阅 KeyLayer.PointerPressed

        // 复制按键：Pad（鼠标垫）与 "?"（未识别）不可复制；尺寸取显式 Width/Height，NaN 时回退 ActualWidth/ActualHeight
        private void CopySelectedKey(Border key)
        {
            if (key == null) return;
            string nm = NameOf(key);
            if (nm == "Pad" || nm == "?" || string.IsNullOrEmpty(nm))
            {
                DiagLog("copy rejected: " + nm);
                return;
            }
            var tb = key.Child as TextBlock;
            string disp = tb != null ? tb.Text : nm;
            double w = key.Width;
            if (double.IsNaN(w)) w = key.ActualWidth;
            double h = key.Height;
            if (double.IsNaN(h)) h = key.ActualHeight;
            _keyClipboard = new KeyClipboardData { InternalName = nm, DisplayName = disp, Width = w, Height = h };
            DiagLog("key copied: " + nm + " disp=" + disp + " size=" + (int)w + "x" + (int)h);
        }

        // 在键区空白处粘贴：目标名冲突时依次尝试 "名(2)"、"(3)"…（父代理的 VkFromName 会剥离 "(n)" 后缀，
        // 所以重名键的 VK 映射仍生效）；先持久化尺寸再走 AddCustomKey（它读取 CustomSize_<名> 恢复尺寸），
        // 创建后覆盖显示名并设置居中于鼠标点的 transform 位置（含 224 面板偏移），一并持久化 CustomPos_<名>。
        private void PasteKeyAt(Point layerPos)
        {
            if (_keyClipboard == null) return;

            // 重名命名：计数器从 2 起步，生成 "名(2)"、"名(3)"…；上限 100000 纯防御（字典键数远达不到），
            // 正常情况必然在有限步内找到空名，不会死循环。
            string nm = _keyClipboard.InternalName;
            if (_customKeys.ContainsKey(nm))
            {
                string baseNm = nm;
                int i = 2;
                while (_customKeys.ContainsKey(nm))
                {
                    nm = baseNm + "(" + i + ")";
                    i++;
                    if (i > 100000) break;
                }
            }

            // 先写尺寸：AddCustomKey 会读取 CustomSize_<名> 恢复复制时的宽高
            ApplicationData.Current.LocalSettings.Values["CustomSize_" + nm] =
                ((int)_keyClipboard.Width).ToString(CultureInfo.InvariantCulture) + ";"
                + ((int)_keyClipboard.Height).ToString(CultureInfo.InvariantCulture);

            // 名字在调用前已确认不与 _customKeys 冲突，AddCustomKey 必会创建并写入字典；
            // 防御性取回（重名循环上限 break 的极端路径下可能未创建）
            AddCustomKey(nm);
            Border b;
            if (!_customKeys.TryGetValue(nm, out b)) { DiagLog("paste failed: not created " + nm); return; }

            // 恢复显示名（DisplayName 为空则保持 AddCustomKey 的默认文本）
            var tb = b.Child as TextBlock;
            if (tb != null && !string.IsNullOrEmpty(_keyClipboard.DisplayName)) tb.Text = _keyClipboard.DisplayName;

            // 键中心对准鼠标点：面板坐标 = KeyLayer 坐标 - (0,224)；允许负值（现有键也有负 transform）
            double tx = layerPos.X - _keyClipboard.Width / 2;
            double ty = layerPos.Y - 224 - _keyClipboard.Height / 2;
            SetTransformXY(b, tx, ty);
            ApplicationData.Current.LocalSettings.Values["CustomPos_" + nm] =
                tx.ToString(CultureInfo.InvariantCulture) + ";" + ty.ToString(CultureInfo.InvariantCulture);

            DiagLog("key pasted: " + nm + " at " + (int)tx + "," + (int)ty);
        }

        // 订阅键区空白右键粘贴；由主文件 OnLoaded 调用（Widget1.xaml.cs 已挂 HookKeyLayerPaste()）
        private void HookKeyLayerPaste()
        {
            if (_pasteHookReady) return;
            _pasteHookReady = true;
            // 0.8.2：Canvas 无 Background 时不参与指针命中测试，空白区域收不到右键（粘贴菜单永远弹不出）。
            // 设全透明背景让空白区可命中；仅影响命中，不可见。
            KeyLayer.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            KeyLayer.PointerPressed += KeyLayer_PointerPressed;
        }

        // 键区空白右键：弹空白菜单（粘贴/显示鼠标垫由菜单项提供，0.8.2；不再直接粘贴）
        private void KeyLayer_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!e.GetCurrentPoint(KeyLayer).Properties.IsRightButtonPressed) return;   // 只响应右键
            if (_dragKey != null || _moveKey != null) return;                           // 拖拽/移动中不弹
            CancelLongPress();
            ShowBlankContextMenu(e.GetCurrentPoint(KeyLayer).Position);
            e.Handled = true;
        }

        // 清空剪贴板（备用，供未来扩展）
        private void ClearKeyClipboard()
        {
            _keyClipboard = null;
        }
    }
}