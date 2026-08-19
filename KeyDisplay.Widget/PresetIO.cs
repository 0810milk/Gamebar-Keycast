// PresetIO.cs — 主题预设 / 布局预设 的导出序列化与导入解析校验（纯静态、自包含工具类）。
// 仅依赖 Windows.Data.Json（与 Widget1.xaml.cs 现有序列化器输出格式完全一致），
// 供 UI 层在 FilePicker 导出 / 导入前后调用：
//   BuildExport        把一条预设打包为导出文件 JsonObject（外层 app/formatVersion/type/name/savedAt/data）
//   ParseExport        解析并校验导入文件；字段级容错（坏项只丢该项，绝不整体失败），失败返回 null + 中文错误
//   SanitizeFileName   FileSavePicker SuggestedFileName 用文件名清洗
//   UniqueName         导入后的重名建议（"name (2)" / "name (3)" …）
// 自包含说明：Widget1.xaml.cs 中的 PresetEntry / KeyPos 为 private 嵌套类（该文件 ~line 178/194），
// 外部文件无法直接引用，故本文件自带同构 DTO（PresetIO.PresetEntry / PresetIO.KeyPos，字段名一一对应），
// UI 层调用时做一次字段拷贝即可。命名空间用 KeyDisplay（与 Widget1.xaml.cs 一致）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Windows.Data.Json;

namespace KeyDisplay
{
    /// <summary>主题/布局预设的导出序列化与导入解析校验（纯静态；仅使用 Windows.Data.Json）</summary>
    public static class PresetIO
    {
        /// <summary>导出文件格式版本；导入文件 version &gt; 1 视为版本过新而拒绝</summary>
        private const int FormatVersion = 1;

        /// <summary>#RRGGBB 十六进制颜色校验</summary>
        private static readonly Regex HexColorRegex = new Regex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

        // ===================== 自包含数据 DTO（与 UI 层 PresetEntry / KeyPos 同构） =====================

        /// <summary>一条预设（内存表示；与 Widget1.xaml.cs 的私有 PresetEntry 字段一一对应）</summary>
        public sealed class PresetEntry
        {
            public string Name;         // 预设名（中文/任意）
            public string Type;         // "theme" | "layout"
            public string SavedAt;      // ISO 时间字符串
            public string Theme;        // theme 预设："dark" | "light" | "custom"
            public string[] Colors;     // theme 预设：8 个 hex（panel,border,keyBg,keyFg,pressedBg,pressedFg,pad,dot）
            public bool LayoutLocked;   // layout 预设：旧版字段，导入时兼容读取；导出不再写入（与 0.7.1 一致）
            public int KeyOpacity;      // layout 预设：10..100
            public bool PadVisible;     // layout 预设：鼠标垫可见
            public double PadW;         // layout 预设：鼠标垫宽度（发布者当前垫面宽度；0=未提供/旧版预设）
            public double PadH;         // layout 预设：鼠标垫高度（发布者当前垫面高度；仅作比例参考，导入端按本机屏幕比例重算）
            public double? PadPosX;     // layout 预设：鼠标垫位置 transform tx（null=未提供/旧版预设，导入端保持本机位置）
            public double? PadPosY;     // layout 预设：鼠标垫位置 transform ty（null=未提供/旧版预设）
            public Dictionary<string, string> Keys;        // layout 预设："Layout_键名" -> "w;h;tx;ty"
            public Dictionary<string, KeyPos> CustomKeys;  // layout 预设：键名 -> KeyPos
            public List<string> DeletedKeys;               // layout 预设：已删除的默认键名
        }

        /// <summary>布局预设中的自定义键：pos="tx;ty"（transform 偏移），size="w;h"</summary>
        public sealed class KeyPos
        {
            public string Pos;
            public string Size;
        }

        // ===================== 1) 导出 =====================

        /// <summary>从一条预设构建导出文件 JsonObject（含外层包装 app/formatVersion/type/name/savedAt/data）</summary>
        public static JsonObject BuildExport(PresetEntry p)
        {
            if (p == null) p = new PresetEntry();
            bool isLayout = p.Type == "layout";
            var root = new JsonObject();
            root.SetNamedValue("app", JsonValue.CreateStringValue("KeyDisplay"));
            root.SetNamedValue("formatVersion", JsonValue.CreateNumberValue(FormatVersion));
            root.SetNamedValue("type", JsonValue.CreateStringValue(isLayout ? "layout" : "theme"));
            root.SetNamedValue("name", JsonValue.CreateStringValue(p.Name ?? ""));
            root.SetNamedValue("savedAt", JsonValue.CreateStringValue(p.SavedAt ?? ""));
            root.SetNamedValue("data", isLayout ? LayoutData(p) : ThemeData(p));
            return root;
        }

        /// <summary>theme 预设 data：{theme, colors:{panel,border,keyBg,keyFg,pressedBg,pressedFg,pad,dot}}（与 ThemePresetToJson 输出一致）</summary>
        private static JsonObject ThemeData(PresetEntry p)
        {
            var data = new JsonObject();
            data.SetNamedValue("theme", JsonValue.CreateStringValue(p.Theme ?? "dark"));
            var colors = new JsonObject();
            string[] keys = { "panel", "border", "keyBg", "keyFg", "pressedBg", "pressedFg", "pad", "dot" };
            for (int k = 0; k < 8; k++)
            {
                colors.SetNamedValue(keys[k],
                    JsonValue.CreateStringValue(p.Colors != null && k < p.Colors.Length && p.Colors[k] != null ? p.Colors[k] : ""));
            }
            data.SetNamedValue("colors", colors);
            return data;
        }

        /// <summary>layout 预设 data：{keyOpacity,padVisible,padW,padH,keys,customKeys,deletedKeys}（与 LayoutPresetToJson 输出一致；不写 layoutLocked）</summary>
        private static JsonObject LayoutData(PresetEntry p)
        {
            var data = new JsonObject();
            data.SetNamedValue("keyOpacity", JsonValue.CreateNumberValue(p.KeyOpacity));
            data.SetNamedValue("padVisible", JsonValue.CreateBooleanValue(p.PadVisible));
            // 0.7.1：同步鼠标垫尺寸（宽/高），但不含比例——比例由导入端按本机虚拟屏幕重算
            data.SetNamedValue("padW", JsonValue.CreateNumberValue(p.PadW));
            data.SetNamedValue("padH", JsonValue.CreateNumberValue(p.PadH));
            // 0.7.1：鼠标垫位置（transform tx/ty）一并同步（可空；旧版导入端忽略未知字段）
            if (p.PadPosX.HasValue) data.SetNamedValue("padPosX", JsonValue.CreateNumberValue(p.PadPosX.Value));
            if (p.PadPosY.HasValue) data.SetNamedValue("padPosY", JsonValue.CreateNumberValue(p.PadPosY.Value));
            var keys = new JsonObject();
            if (p.Keys != null)
                foreach (var kv in p.Keys) keys.SetNamedValue(kv.Key, JsonValue.CreateStringValue(kv.Value ?? ""));
            data.SetNamedValue("keys", keys);
            var cks = new JsonObject();
            if (p.CustomKeys != null)
                foreach (var kv in p.CustomKeys)
                {
                    var pos = new JsonObject();
                    pos.SetNamedValue("pos", JsonValue.CreateStringValue(kv.Value != null && kv.Value.Pos != null ? kv.Value.Pos : "0;0"));
                    pos.SetNamedValue("size", JsonValue.CreateStringValue(kv.Value != null && kv.Value.Size != null ? kv.Value.Size : ""));
                    cks.SetNamedValue(kv.Key, pos);
                }
            data.SetNamedValue("customKeys", cks);
            var del = new JsonArray();
            if (p.DeletedKeys != null)
                foreach (var d in p.DeletedKeys) del.Add(JsonValue.CreateStringValue(d ?? ""));
            data.SetNamedValue("deletedKeys", del);
            return data;
        }

        // ===================== 2) 导入 =====================

        /// <summary>解析导入文件文本（FilePicker 读取后直接传入）。非对象根（数组/数值/null 文本）返回 null + "无效的预设文件"。</summary>
        public static PresetEntry ParseExport(string json, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(json)) { error = "无效的预设文件"; return null; }
            try
            {
                return ParseExport(JsonObject.Parse(json), out error);
            }
            catch
            {
                error = "无效的预设文件";
                return null;
            }
        }

        /// <summary>解析已解析的根对象；校验链：root → formatVersion → type → data → name；字段级容错见各 Parse 方法。</summary>
        public static PresetEntry ParseExport(JsonObject root, out string error)
        {
            error = null;
            // a. root 为 null（非对象根由字符串重载 / 调用方在解析阶段拦截）
            if (root == null) { error = "无效的预设文件"; return null; }

            // b. formatVersion 存在且 > 1 → 版本过新
            IJsonValue fv = root.GetNamedValue("formatVersion", null);
            if (fv != null && fv.ValueType == JsonValueType.Number && fv.GetNumber() > FormatVersion)
            {
                error = "预设文件版本过新";
                return null;
            }

            // c. type 必须是 "theme" | "layout"
            string type = GetStringValue(root, "type", null);
            if (type != "theme" && type != "layout") { error = "无效的预设文件"; return null; }

            // d. data 必须是对象
            IJsonValue dv = root.GetNamedValue("data", null);
            if (dv == null || dv.ValueType != JsonValueType.Object) { error = "预设文件缺少数据"; return null; }
            JsonObject data = dv.GetObject();

            // e. name 缺失/空 → "未命名预设" 兜底（此处不清洗，清洗由调用方决定）
            string name = GetStringValue(root, "name", "");
            if (string.IsNullOrEmpty(name)) name = "未命名预设";

            var p = new PresetEntry
            {
                Type = type,
                Name = name,
                SavedAt = GetStringValue(root, "savedAt", "")
            };

            if (type == "layout") ParseLayoutData(data, p);
            else ParseThemeData(data, p);
            return p;
        }

        /// <summary>theme 预设字段级容错：theme 非法回退 "dark"；colors 取 8 个语义槽位，非法置 ""</summary>
        private static void ParseThemeData(JsonObject data, PresetEntry p)
        {
            p.Theme = "dark";
            IJsonValue tv = data.GetNamedValue("theme", null);
            if (tv != null && tv.ValueType == JsonValueType.String)
            {
                string t = tv.GetString();
                if (t == "light" || t == "custom") p.Theme = t;
            }

            p.Colors = new string[8];
            IJsonValue cv = data.GetNamedValue("colors", null);
            if (cv != null && cv.ValueType == JsonValueType.Object)
            {
                JsonObject colors = cv.GetObject();
                string[] keys = { "panel", "border", "keyBg", "keyFg", "pressedBg", "pressedFg", "pad", "dot" };
                for (int k = 0; k < 8; k++)
                {
                    string s = GetStringValue(colors, keys[k], "");
                    p.Colors[k] = HexColorRegex.IsMatch(s) ? s : "";
                }
            }
        }

        /// <summary>layout 预设字段级容错：坏项只丢该项，绝不整体失败</summary>
        private static void ParseLayoutData(JsonObject data, PresetEntry p)
        {
            // 兼容旧版预设（0.7.1 起不再导出该字段）
            p.LayoutLocked = GetBooleanValue(data, "layoutLocked", true);
            p.KeyOpacity = ClampOpacity(GetNumberValue(data, "keyOpacity", 100.0));
            p.PadVisible = GetBooleanValue(data, "padVisible", true);
            // 鼠标垫尺寸（0.7.1）：缺省 0 = 旧版预设未提供，导入端跳过鼠标垫尺寸处理
            p.PadW = ClampPadDim(GetNumberValue(data, "padW", 0.0));
            p.PadH = ClampPadDim(GetNumberValue(data, "padH", 0.0));
            // 鼠标垫位置（0.7.1）：缺省 NaN = 未提供，导入端保持本机位置
            double px = GetNumberValue(data, "padPosX", double.NaN);
            double py = GetNumberValue(data, "padPosY", double.NaN);
            p.PadPosX = double.IsNaN(px) || double.IsInfinity(px) ? (double?)null : px;
            p.PadPosY = double.IsNaN(py) || double.IsInfinity(py) ? (double?)null : py;

            // keys：键名须以 "Layout_" 开头；值须 "w;h;tx;ty" 四段 double（InvariantCulture），否则丢弃该项
            p.Keys = new Dictionary<string, string>();
            IJsonValue kvv = data.GetNamedValue("keys", null);
            if (kvv != null && kvv.ValueType == JsonValueType.Object)
            {
                foreach (var kv in kvv.GetObject())
                {
                    if (!kv.Key.StartsWith("Layout_", StringComparison.Ordinal)) continue;
                    if (kv.Value == null || kv.Value.ValueType != JsonValueType.String) continue;
                    string normalized;
                    if (TryNormalizeKeyValue(kv.Value.GetString(), out normalized)) p.Keys[kv.Key] = normalized;
                }
            }

            // customKeys：pos/size 字符串直存，空串给 "0;0" 兜底；非对象项丢弃
            p.CustomKeys = new Dictionary<string, KeyPos>();
            IJsonValue ckv = data.GetNamedValue("customKeys", null);
            if (ckv != null && ckv.ValueType == JsonValueType.Object)
            {
                foreach (var kv in ckv.GetObject())
                {
                    if (kv.Value == null || kv.Value.ValueType != JsonValueType.Object) continue;
                    try
                    {
                        JsonObject ck = kv.Value.GetObject();
                        string pos = GetStringValue(ck, "pos", "0;0");
                        string size = GetStringValue(ck, "size", "0;0");
                        if (string.IsNullOrEmpty(pos)) pos = "0;0";
                        if (string.IsNullOrEmpty(size)) size = "0;0";
                        p.CustomKeys[kv.Key] = new KeyPos { Pos = pos, Size = size };
                    }
                    catch { }
                }
            }

            // deletedKeys：仅收字符串元素，其余跳过
            p.DeletedKeys = new List<string>();
            IJsonValue dv = data.GetNamedValue("deletedKeys", null);
            if (dv != null && dv.ValueType == JsonValueType.Array)
            {
                foreach (var item in dv.GetArray())
                {
                    if (item != null && item.ValueType == JsonValueType.String) p.DeletedKeys.Add(item.GetString());
                }
            }
        }

        // ===================== 3) 文件名清洗 =====================

        /// <summary>清洗预设名用于文件名：\ / : * ? " &lt; &gt; | → '_'，去首尾空白；结果为空返回 "preset"</summary>
        public static string SanitizeFileName(string name)
        {
            if (name == null) return "preset";
            char[] chars = name.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                    chars[i] = '_';
            }
            string result = new string(chars).Trim();
            return result.Length == 0 ? "preset" : result;
        }

        // ===================== 4) 重名建议 =====================

        /// <summary>若 name 已存在于 existing，依次返回 "name (2)"、"name (3)"… 直到不冲突</summary>
        public static string UniqueName(string name, ICollection<string> existing)
        {
            string baseName = name ?? "";
            if (existing == null) return baseName;
            string candidate = baseName;
            int n = 2;
            while (existing.Contains(candidate))
            {
                candidate = baseName + " (" + n + ")";
                n++;
            }
            return candidate;
        }

        // ===================== 私有辅助 =====================

        private static string GetStringValue(JsonObject o, string key, string def)
        {
            try
            {
                IJsonValue v = o.GetNamedValue(key, null);
                if (v != null && v.ValueType == JsonValueType.String) return v.GetString();
            }
            catch { }
            return def;
        }

        private static double GetNumberValue(JsonObject o, string key, double def)
        {
            try
            {
                IJsonValue v = o.GetNamedValue(key, null);
                if (v != null && v.ValueType == JsonValueType.Number) return v.GetNumber();
            }
            catch { }
            return def;
        }

        private static bool GetBooleanValue(JsonObject o, string key, bool def)
        {
            try
            {
                IJsonValue v = o.GetNamedValue(key, null);
                if (v != null && v.ValueType == JsonValueType.Boolean) return v.GetBoolean();
            }
            catch { }
            return def;
        }

        /// <summary>解析 "w;h;tx;ty"（InvariantCulture）；w/h 钳制 [10,2000]，tx/ty 钳制 [-2000,2000]；失败返回 false</summary>
        private static bool TryNormalizeKeyValue(string raw, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrEmpty(raw)) return false;
            string[] parts = raw.Split(';');
            if (parts.Length != 4) return false;
            double w, h, tx, ty;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out w)) return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out h)) return false;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out tx)) return false;
            if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out ty)) return false;
            // NaN / Infinity 无法钳制，视为非法项丢弃
            if (double.IsNaN(w) || double.IsInfinity(w) || double.IsNaN(h) || double.IsInfinity(h) ||
                double.IsNaN(tx) || double.IsInfinity(tx) || double.IsNaN(ty) || double.IsInfinity(ty)) return false;
            w = Math.Max(10, Math.Min(2000, w));
            h = Math.Max(10, Math.Min(2000, h));
            tx = Math.Max(-2000, Math.Min(2000, tx));
            ty = Math.Max(-2000, Math.Min(2000, ty));
            normalized = w.ToString(CultureInfo.InvariantCulture) + ";" + h.ToString(CultureInfo.InvariantCulture)
                       + ";" + tx.ToString(CultureInfo.InvariantCulture) + ";" + ty.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>keyOpacity 钳制到 10..100；非数 / NaN 用 100</summary>
        private static int ClampOpacity(double v)
        {
            if (double.IsNaN(v)) return 100;
            return Math.Max(10, Math.Min(100, (int)Math.Round(v)));
        }

        /// <summary>鼠标垫尺寸钳制：[10,2000]；非数 / NaN / 无限 → 0（= 未提供）</summary>
        private static double ClampPadDim(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0.0;
            if (v <= 0) return 0.0;
            return Math.Max(10, Math.Min(2000, v));
        }
    }
}