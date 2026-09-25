// Widget1.MultiSelect.cs —— 0.9.4 右键菜单多选模式（partial 文件）
//
// 交互模型（与用户确认的方案）：
//   进入：右键某个按键 → 菜单「多选」项 → 进入多选模式（该键为第一个选中项）
//   选中：左键单击其他按键 = 加入/移出选择（切换）；鼠标垫不参与
//   视觉：选中键 = Xbox 绿 2px 描边（与"按下反色"区分）
//   批量：右键任意已选键 → 批量菜单：删除(N个) / 改名(前缀+序号) / 复制(N个) / 取消选择
//   粘贴：右键键区空白 → 「粘贴」(组剪贴板非空时) → 整组按原相对布局粘贴，点击点为组中心
//   退出：左键点空白 / 批量操作完成 / 菜单「取消选择」
//
// 坐标系：使用"逻辑坐标"（Canvas.Left/Top + TranslateTransform）而非视觉坐标，
//         因此不受键区整体缩放（FitLayoutToWindow）与左缘补偿（KeyLayer.Margin）影响。
//         自定义键位于 CustomKeysPanel（KeyLayer 内 y 偏移 224），逻辑坐标需补 224。

using System;
using System.Collections.Generic;
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
    public sealed partial class Widget1 : Page
    {
        // ===== 状态 =====
        private bool _multiSelectMode;                        // 是否处于多选模式
        private readonly List<Border> _selectedKeys = new List<Border>();   // 已选中的键（Border 引用）
        private bool _multiDeletePending;                     // 删除确认框当前是"批量删除"语义
        private bool _multiRenameMode;                        // 改名框当前是"批量改名（前缀+序号）"语义
        private List<KeyGroupEntry> _keyGroupClipboard;        // 组剪贴板（null/空 = 无）

        private const double CustomPanelOffsetY = 224;        // CustomKeysPanel 在 KeyLayer 内的 y 偏移

        // 选中描边色：固定红色（用户指定：多选边框统一红色，不随主题/自定义色变化）
        private static readonly SolidColorBrush MultiSelectBrush =
            new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x00, 0x00));

        private sealed class KeyGroupEntry
        {
            public string InternalName;
            public string DisplayName;
            public double W, H;      // 尺寸
            public double Ox, Oy;    // 键中心相对"组中心"的偏移（逻辑坐标）
        }

        // 供 Key_PointerPressed 调用：多选模式下按下键即记录"点击候补"（**任何位置**，
        // 含边缘——键很小，原来排除边缘 8px 会让大量点击落空）。
        // 抬起时用**几何判定**决定是点击还是移动/缩放：
        //   位置与尺寸都没变 → 点击选中（哪怕按得久、哪怕进入了长按移动模式）
        //   位置或尺寸变了   → 视为移动/缩放，交给常规落位流程
        // 这样点击命中最稳，同时移动与缩放完全不受影响。
        private Border _msTapCandidate;   // 多选模式下当前按下的键（可能是点击选中）
        private long _msPressTicks;       // 按下时刻（诊断用）
        private double _msPressTx, _msPressTy;      // 按下时的 transform 偏移
        private double _msPressW, _msPressH;        // 按下时的宽高（缩放判定）
        // 最近一次关闭右键菜单的时刻：用于忽略"关菜单的那一下左键"（防误退出多选）
        private long _lastKeyMenuCloseTicks;

        private void BeginMultiSelectTapCandidate(Border b)
        {
            _msTapCandidate = b;
            _msPressTicks = DateTime.UtcNow.Ticks;
            var tt = b.RenderTransform as TranslateTransform;
            _msPressTx = tt != null ? tt.X : 0;
            _msPressTy = tt != null ? tt.Y : 0;
            _msPressW = double.IsNaN(b.Width) ? b.ActualWidth : b.Width;
            _msPressH = double.IsNaN(b.Height) ? b.ActualHeight : b.Height;
        }

        // 供 Key_PointerReleased 调用：返回 true 表示已按"点击选中"处理（调用方直接返回）
        private bool TryFinishMultiSelectTap()
        {
            if (_msTapCandidate == null) return false;
            var cand = _msTapCandidate;
            _msTapCandidate = null;
            var tt = cand.RenderTransform as TranslateTransform;
            double tx = tt != null ? tt.X : 0;
            double ty = tt != null ? tt.Y : 0;
            double w = double.IsNaN(cand.Width) ? cand.ActualWidth : cand.Width;
            double h = double.IsNaN(cand.Height) ? cand.ActualHeight : cand.Height;
            // 阈值 2px：0.5px 会因浮点/布局噪声误判为"移动过"（点击落空的主因之一）
            bool moved = Math.Abs(tx - _msPressTx) > 2.0 || Math.Abs(ty - _msPressTy) > 2.0;
            bool resized = Math.Abs(w - _msPressW) > 0.5 || Math.Abs(h - _msPressH) > 0.5;
            if (moved || resized)
            {
                DiagLog("multiselect tap->drag (moved=" + moved + " resized=" + resized + ")");
                return false;   // 确实拖过：按移动/缩放处理
            }
            ToggleKeySelection(cand);
            return true;
        }

        private bool IsKeySelected(Border b)
        {
            return b != null && _selectedKeys.Contains(b);
        }

        // ===== 0.9.4 批量移动：多选后长按任意"已选中的键"拖动 → 整组一起移动 =====
        // 与单键长按移动完全同一条链路（吸附、参考线、落位持久化都照旧）：
        // 被拖的键由主逻辑驱动，组内其他键按"被拖键的最终位移"同步平移。
        private sealed class GroupMoveEntry
        {
            public Border Key;
            public double StartTx, StartTy;
        }
        private List<GroupMoveEntry> _groupMove;   // 非 null = 正在整组移动

        // 长按进入移动模式时调用：若被拖的键在多选集合内且集合 ≥2，则登记组移动
        private void BeginGroupMoveIfNeeded(Border dragged)
        {
            _groupMove = null;
            if (!_multiSelectMode || dragged == null) return;
            if (!IsKeySelected(dragged)) return;     // 只对已选中的键启用（未选中键 = 单键移动）
            if (_selectedKeys.Count < 2) return;     // 单个键不需要组移动
            var list = new List<GroupMoveEntry>();
            foreach (var k in _selectedKeys)
            {
                if (k == null || k == MousePad) continue;   // 鼠标垫不参与组移动
                var tt = k.RenderTransform as TranslateTransform;
                list.Add(new GroupMoveEntry
                {
                    Key = k,
                    StartTx = tt != null ? tt.X : 0,
                    StartTy = tt != null ? tt.Y : 0
                });
            }
            if (list.Count > 1)
            {
                _groupMove = list;
                DiagLog("group move begin: " + list.Count + " keys");
            }
        }

        // 拖动中：把"被拖键的最终位移"（含吸附修正）同步到组内其他键
        private void ApplyGroupMove(double ddx, double ddy)
        {
            if (_groupMove == null) return;
            foreach (var g in _groupMove)
            {
                if (g.Key == null || ReferenceEquals(g.Key, _moveKey)) continue;   // 被拖键由主逻辑设置
                try { SetTransformXY(g.Key, g.StartTx + ddx, g.StartTy + ddy); } catch { }
            }
        }

        // 落位：返回组内需要一并持久化位置的键（不含被拖键——它由常规流程处理）
        private List<Border> TakeGroupMoveOthers()
        {
            var res = new List<Border>();
            if (_groupMove == null) return res;
            foreach (var g in _groupMove)
                if (g.Key != null && !ReferenceEquals(g.Key, _moveKey)) res.Add(g.Key);
            _groupMove = null;
            return res;
        }

        // ===== 进入 / 退出 =====
        // 0.9.4（用户确认的最终交互）：右键某键 → 菜单「多选」→ 进入多选模式，
        // 并把**被右键的那个键直接选中**（第一个选中项）；之后左键单击其他键继续加选。
        private void EnterMultiSelectMode(Border first)
        {
            _multiSelectMode = true;
            // 先清列表再恢复视觉：SetKey 的选中判断依据 _selectedKeys，
            // 顺序反了会把红框写回旧键（旧选中色彩残留）
            var stale = new List<Border>(_selectedKeys);
            _selectedKeys.Clear();
            foreach (var b in stale) ApplySelectVisual(b, false);
            if (first != null && first != MousePad) AddSelection(first);   // 右键的那个键直接选中
            UpdateMultiSelectHint();
            DiagLog("multiselect enter selected=" + _selectedKeys.Count);
        }

        private void ExitMultiSelectMode()
        {
            ExitMultiSelectMode("unspecified");
        }

        // 0.9.4：退出多选带原因（诊断用——用户报"右键后左键会取消多选"，需从日志确认是哪条路径）
        private void ExitMultiSelectMode(string reason)
        {
            // 同上：必须先清列表，再恢复每个键的主题边框——否则 SetKey 仍按"选中"给回红框，
            // 表现为退出多选后按键上残留红色描边（用户反馈的"以前色彩残留"）
            bool wasActive = _multiSelectMode || _selectedKeys.Count > 0;
            var list = new List<Border>(_selectedKeys);
            _selectedKeys.Clear();
            _multiSelectMode = false;
            _multiDeletePending = false;
            _multiRenameMode = false;
            foreach (var b in list) ApplySelectVisual(b, false);
            if (wasActive)
            {
                UpdateMultiSelectHint();
                DiagLog("multiselect exit: " + reason + " (had " + list.Count + ")");
            }
        }

        // 多选模式状态提示（用户需知道"为什么此刻点键不拖动"）：显示在状态文字位置
        private void UpdateMultiSelectHint()
        {
            try
            {
                if (StatusText == null) return;
                if (_multiSelectMode)
                    StatusText.Text = _selectedKeys.Count > 0
                        ? ("多选模式 · 已选 " + _selectedKeys.Count + " 个（右键已选键操作）")
                        : "多选模式 · 左键单击选择按键";
                else if (StatusText.Text != null && StatusText.Text.StartsWith("多选模式"))
                    StatusText.Text = "";
            }
            catch { }
        }

        private void AddSelection(Border b)
        {
            if (b == null || b == MousePad || _selectedKeys.Contains(b)) return;
            _selectedKeys.Add(b);
            ApplySelectVisual(b, true);
        }

        private void ToggleKeySelection(Border b)
        {
            if (b == null || b == MousePad) return;   // 鼠标垫是背景控件，不参与多选
            if (_selectedKeys.Contains(b))
            {
                _selectedKeys.Remove(b);
                ApplySelectVisual(b, false);
            }
            else
            {
                AddSelection(b);
            }
            // 0.9.4 修正：选择清空**不再自动退出多选模式**——用户点一下已选键把它取消后，
            // 模式仍然保持（否则表现为"左键点一下多选就没了"）。退出方式：左键点键区空白、
            // 或菜单里选「取消选择」。
            UpdateMultiSelectHint();
        }

        private void ApplySelectVisual(Border b, bool on)
        {
            if (b == null) return;
            try
            {
                if (on)
                {
                    b.BorderBrush = MultiSelectBrush;
                    b.BorderThickness = new Thickness(2);
                }
                else
                {
                    b.BorderThickness = new Thickness(1);
                    SetKey(b, false);   // 恢复主题边框色与未按下态
                }
            }
            catch { }
        }

        // ===== 逻辑坐标（不受键区缩放/左缘补偿影响）=====
        private void LogicalCenterOf(Border b, out double cx, out double cy)
        {
            double w = double.IsNaN(b.Width) ? b.ActualWidth : b.Width;
            double h = double.IsNaN(b.Height) ? b.ActualHeight : b.Height;
            double lx = Canvas.GetLeft(b);
            double ly = Canvas.GetTop(b);
            var tt = b.RenderTransform as TranslateTransform;
            if (tt != null) { lx += tt.X; ly += tt.Y; }
            string nm = NameOf(b);
            if (!string.IsNullOrEmpty(nm) && _customKeys.ContainsKey(nm)) ly += CustomPanelOffsetY;
            cx = lx + w / 2;
            cy = ly + h / 2;
        }

        // ===== 批量：复制（整组，保留相对布局）=====
        // 0.9.4：组条目构建抽成公共方法——"复制"与"空白处直接粘贴当前选中"共用同一套逻辑
        private List<KeyGroupEntry> BuildGroupEntriesFromSelection()
        {
            var list = new List<KeyGroupEntry>();
            if (_selectedKeys.Count == 0) return list;
            double cx = 0, cy = 0;
            foreach (var b in _selectedKeys)
            {
                double bx, by;
                LogicalCenterOf(b, out bx, out by);
                cx += bx; cy += by;
            }
            cx /= _selectedKeys.Count;
            cy /= _selectedKeys.Count;
            foreach (var b in _selectedKeys)
            {
                string nm = NameOf(b);
                if (string.IsNullOrEmpty(nm) || nm == "Pad" || nm == "?") continue;
                var tb = b.Child as TextBlock;
                double bx, by;
                LogicalCenterOf(b, out bx, out by);
                list.Add(new KeyGroupEntry
                {
                    InternalName = nm,
                    DisplayName = tb != null ? tb.Text : nm,
                    W = double.IsNaN(b.Width) ? b.ActualWidth : b.Width,
                    H = double.IsNaN(b.Height) ? b.ActualHeight : b.Height,
                    Ox = bx - cx,
                    Oy = by - cy
                });
            }
            return list;
        }

        private void MultiCopyApply()
        {
            var list = BuildGroupEntriesFromSelection();
            _keyGroupClipboard = list.Count > 0 ? list : null;
            if (list.Count > 0) _keyClipboard = null;   // 组复制时清掉单键剪贴板，避免"粘贴"语义混淆
            DiagLog("group copied: " + list.Count);
            ExitMultiSelectMode();
        }

        // 直接在空白处粘贴"当前多选的按键"（无需先点复制；用户描述的用法）
        private void PasteSelectionAt(Point layerPos)
        {
            var list = BuildGroupEntriesFromSelection();
            if (list.Count == 0) return;
            PasteEntriesAt(list, layerPos);
            // 保留多选状态：用户可能想连着贴几份到不同位置
            DiagLog("selection pasted: " + list.Count);
        }

        // ===== 批量：粘贴（整组按原相对布局，点击点 = 组中心）=====
        private void PasteGroupAt(Point layerPos)
        {
            if (_keyGroupClipboard == null || _keyGroupClipboard.Count == 0) return;
            PasteEntriesAt(_keyGroupClipboard, layerPos);
        }

        private void PasteEntriesAt(List<KeyGroupEntry> entries, Point layerPos)
        {
            if (entries == null || entries.Count == 0) return;
            int placed = 0;
            foreach (var e in entries)
            {
                try
                {
                    if (PasteGroupEntry(e, layerPos.X + e.Ox, layerPos.Y + e.Oy)) placed++;
                }
                catch (Exception ex) { DiagLog("group paste entry fail: " + ex.Message); }
            }
            OffsetKeyLayerForNegativeKeys();   // 新键可能带负坐标，粘贴后重算左缘补偿
            DiagLog("group pasted: " + placed + "/" + entries.Count);
        }

        // 粘贴单个组条目：targetCenter 为逻辑坐标下的键中心
        private bool PasteGroupEntry(KeyGroupEntry e, double targetCenterX, double targetCenterY)
        {
            var v = ApplicationData.Current.LocalSettings.Values;
            // 重名命名：名(2)、名(3)…（VkFromName 会剥离 "(n)" 后缀，VK 映射不受影响）
            string nm = e.InternalName;
            if (_customKeys.ContainsKey(nm))
            {
                string baseNm = nm;
                int k = 2;
                while (_customKeys.ContainsKey(nm))
                {
                    nm = baseNm + "(" + k + ")";
                    k++;
                    if (k > 100000) break;
                }
            }
            // 先写尺寸与位置（AddCustomKey 读取 CustomSize_ 恢复尺寸）
            v["CustomSize_" + nm] = ((int)e.W).ToString(CultureInfo.InvariantCulture) + ";"
                + ((int)e.H).ToString(CultureInfo.InvariantCulture);
            double tx = targetCenterX - e.W / 2;
            double ty = targetCenterY - CustomPanelOffsetY - e.H / 2;   // 面板坐标 = KeyLayer 坐标 - 224
            v["CustomPos_" + nm] = tx.ToString(CultureInfo.InvariantCulture) + ";" + ty.ToString(CultureInfo.InvariantCulture);

            AddCustomKey(nm);
            Border b;
            if (!_customKeys.TryGetValue(nm, out b)) return false;

            var tb = b.Child as TextBlock;
            if (tb != null && !string.IsNullOrEmpty(e.DisplayName))
            {
                tb.Text = e.DisplayName;
                v["DisplayName_" + nm] = e.DisplayName;   // 显示名一并持久化（重启不丢）
            }
            SetTransformXY(b, tx, ty);
            return true;
        }

        // ===== 批量：删除（确认框 + 循环删除）=====
        private void MultiDeleteRequest()
        {
            if (_selectedKeys.Count == 0) return;
            _multiDeletePending = true;
            DeleteConfirmText.Text = "删除 " + _selectedKeys.Count + " 个控件 ？";
            DeleteConfirmPanel.Visibility = Visibility.Visible;
            FadeIn(DeleteConfirmPanel);
            DiagLog("multiselect delete confirm: " + _selectedKeys.Count);
        }

        // 由 DeleteConfirmYes_Click 在 _multiDeletePending 时调用
        private void MultiDeleteApply()
        {
            var list = new List<Border>(_selectedKeys);   // 副本：Exit 会清空选择
            int n = list.Count;
            ExitMultiSelectMode();
            foreach (var b in list)
            {
                try { ConfirmDeleteKey(b); }
                catch (Exception ex) { DiagLog("multiselect delete fail: " + ex.Message); }
            }
            DiagLog("multiselect deleted: " + n);
        }

        // ===== 批量：改名（所有选中键统一改成同一个显示名，不加序号）=====
        private void MultiRenameOpen()
        {
            if (_selectedKeys.Count == 0) return;
            _multiRenameMode = true;
            _renameKey = null;                     // 批量模式不使用单键目标
            RenameTitle.Text = "改名";
            RenameInput.Text = "";
            RenamePanel.Visibility = Visibility.Visible;
            FadeIn(RenamePanel);
            ApplySettingsColors();
        }

        // 由 RenameConfirm_Click 在 _multiRenameMode 时调用：统一显示名（用户要求，不做 前缀1/前缀2）
        private void MultiRenameApply(string newText)
        {
            var list = new List<Border>(_selectedKeys);
            int done = 0;
            foreach (var b in list)
            {
                string nm = NameOf(b);
                if (string.IsNullOrEmpty(nm) || nm == "Pad" || nm == "?") continue;
                try
                {
                    ApplicationData.Current.LocalSettings.Values["DisplayName_" + nm] = newText;
                    var tb = b.Child as TextBlock;
                    if (tb != null) tb.Text = newText;
                    done++;
                }
                catch (Exception ex) { DiagLog("multiselect rename item fail: " + ex.Message); }
            }
            DiagLog("multiselect renamed: " + done + " -> " + newText);
            ExitMultiSelectMode();
        }

        // ===== 批量菜单（右键任意已选键时显示）=====
        private void ShowMultiSelectMenu(Point layerPos)
        {
            CancelLongPress();
            KeyMenuItems.Children.Clear();
            int n = _selectedKeys.Count;
            AddMenuItem("删除（" + n + " 个）", () =>
            {
                CloseKeyContextMenu();
                MultiDeleteRequest();
            });
            AddMenuItem("改名", () =>
            {
                CloseKeyContextMenu();
                MultiRenameOpen();
            });
            AddMenuItem("复制（" + n + " 个）", () =>
            {
                CloseKeyContextMenu();
                MultiCopyApply();
            });
            AddMenuItem("取消选择", () =>
            {
                CloseKeyContextMenu();
                ExitMultiSelectMode();
            });
            ShowMenu(layerPos);
            DiagLog("multiselect menu open n=" + n);
        }
    }
}
