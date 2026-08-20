using System;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace KeyDisplay
{
    /// <summary>
    /// 按键显示名改名（0.8.1）：Widget1 的 partial 补充文件。
    /// 持久化键：DisplayName_&lt;内部名&gt; = 显示文本（字符串）；移除该键即恢复默认显示名。
    /// 显示名只改按键文本，不影响 VK 映射 / 内部名（内部名仍是字典键，映射不变）。
    /// 改名面板 XAML（RenamePanel / RenameInput / RenameConfirm / RenameCancel / RenameTitle）由并行子代理添加进 Widget1.xaml。
    /// </summary>
    public sealed partial class Widget1 : Page
    {
        // 当前正在改名的按键（改名面板打开期间有效）
        private Border _renameKey;

        // 按键显示名：DisplayName_<名> 有值用值，否则默认（Space 显示「空格」，其余显示内部名）。
        // 共享 helper：AddCustomKey 创建自定义键文本时也调用它；必须稳健——任何异常回退默认名。
        private static string KeyDisplayName(string internalName)
        {
            // 0.8.2：粘贴副本 "Space(2)"/"Q(3)" 等默认显示基名的默认名（空格副本显示「空格」）
            string baseName = internalName;
            int lp = internalName.LastIndexOf('(');
            if (lp > 0 && internalName[internalName.Length - 1] == ')')
            {
                bool digits = true;
                for (int i = lp + 1; i < internalName.Length - 1; i++)
                    if (internalName[i] < '0' || internalName[i] > '9') { digits = false; break; }
                if (digits) baseName = internalName.Substring(0, lp);
            }
            string fallback = (baseName == "Space") ? "\u7a7a\u683c" : baseName;
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values["DisplayName_" + internalName] as string;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch { }
            return fallback;
        }

        // 打开改名面板：key 为 null、鼠标垫（Pad）、未知（?）、空名均不可改名
        private void OpenKeyRename(Border key)
        {
            if (key == null) return;
            string nm = NameOf(key);
            if (string.IsNullOrEmpty(nm) || nm == "Pad" || nm == "?") return;
            _renameKey = key;
            // 0.8.2：打开前刷新配色（改名面板背板/边框/文字随主题；此前遗漏导致透明无背板）
            try { ApplySettingsColors(); } catch { }
            FadeIn(RenamePanel);   // 0.8.2 弹层淡入
            // 预填当前可见文本（默认键 XAML 文本如「左Shift」与内部名 Shift 不同；改名框应显示用户看到的字）
            var tb0 = key.Child as TextBlock;
            RenameInput.Text = (tb0 != null && !string.IsNullOrEmpty(tb0.Text)) ? tb0.Text : KeyDisplayName(nm);
            RenamePanel.Visibility = Visibility.Visible;
            try { RenameInput.Focus(FocusState.Programmatic); }
            catch { }   // Game Bar 合成环境可能抛异常，必须吞掉
            DiagLog("rename open: " + nm);
        }

        // 确认改名：空输入视为取消；超长截断到 12 字符；写 DisplayName_ 持久化 + 更新文本（0.8.2：不再做宽度自适应，不改尺寸与布局持久化）
        private void RenameConfirm_Click(object sender, TappedRoutedEventArgs e)
        {
            try
            {
                string newText = RenameInput.Text;
                if (newText == null) newText = string.Empty;
                newText = newText.Trim();
                if (newText.Length == 0) { RenameCancel_Click(sender, e); return; }   // 空输入 → 直接关闭（视为取消）
                if (newText.Length > 12) newText = newText.Substring(0, 12);          // 防御：截断为前 12 字符

                Border b = _renameKey;
                string nm = (b != null) ? NameOf(b) : null;
                if (b == null || string.IsNullOrEmpty(nm) || nm == "Pad" || nm == "?")
                {
                    // 目标键无效：直接关闭返回
                    _renameKey = null;
                    RenamePanel.Visibility = Visibility.Collapsed;
                    return;
                }

                // 持久化：新文本 == 默认显示名（Space→「空格」，其余=内部名）→ 移除条目恢复默认；否则写新值
                string defaultName = (nm == "Space") ? "\u7a7a\u683c" : nm;
                if (newText == defaultName)
                    ApplicationData.Current.LocalSettings.Values.Remove("DisplayName_" + nm);
                else
                    ApplicationData.Current.LocalSettings.Values["DisplayName_" + nm] = newText;

                // 更新文本（统一入口：默认键的 TextBlock 是 XAML 静态定义的，自定义键在 AddCustomKey 中创建）
                var tb = b.Child as TextBlock;
                if (tb != null) tb.Text = newText;

                // 0.8.2 改名只改显示文本，不改按键尺寸与布局持久化（用户明确要求）

                _renameKey = null;
                RenamePanel.Visibility = Visibility.Collapsed;
                DiagLog("key renamed: " + nm + " -> " + newText);
            }
            catch (Exception ex)
            {
                DiagLog("rename confirm fail: " + ex.GetType().Name + " " + ex.Message);
                _renameKey = null;
                try { RenamePanel.Visibility = Visibility.Collapsed; } catch { }
            }
        }

        // 取消改名
        private void RenameCancel_Click(object sender, TappedRoutedEventArgs e)
        {
            _renameKey = null;
            RenamePanel.Visibility = Visibility.Collapsed;
            DiagLog("rename cancel");
        }

        // 点击改名面板遮罩（菜单外）：关闭（与取消同）
        private void RenamePanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            _renameKey = null;
            RenamePanel.Visibility = Visibility.Collapsed;
            DiagLog("rename closed by mask");
        }

        // OnLoaded（RestoreCustomKeys 之后）由父代理调用：恢复默认键（_keys/_mouse）的自定义显示名。
        // 自定义键的显示名已在 AddCustomKey 内经 KeyDisplayName 处理，这里只处理默认键。
        // 宽度不改：改名时已写入 Layout_ 持久化，RestoreLayout 启动时已应用尺寸；
        // 该键无 Layout_（用户从没移动过）则保持 XAML 默认宽度即可。幂等，可重复调用。
        private void ApplyDisplayNamesToDefaults()
        {
            int applied = 0;
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                foreach (var kv in _keys)
                {
                    string nm = kv.Key;
                    var v = values["DisplayName_" + nm] as string;
                    if (!string.IsNullOrEmpty(v))
                    {
                        var tb = kv.Value.Child as TextBlock;
                        if (tb != null) { tb.Text = v; applied++; }
                    }
                }
                foreach (var kv in _mouse)
                {
                    string nm = kv.Key;
                    var v = values["DisplayName_" + nm] as string;
                    if (!string.IsNullOrEmpty(v))
                    {
                        var tb = kv.Value.Child as TextBlock;
                        if (tb != null) { tb.Text = v; applied++; }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog("display names apply fail: " + ex.GetType().Name + " " + ex.Message);
            }
            DiagLog("display names applied: n=" + applied);
        }
    }
}