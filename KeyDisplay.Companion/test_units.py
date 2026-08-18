"""单元测试：快照协议 + 键位映射 + 钩子回调逻辑。

运行：python -m unittest test_units -v
（模拟输入在无头自动化环境中会被系统丢弃，故钩子回调通过
  直接构造事件结构体验证映射逻辑；真实物理输入已通过
  companion.py + test_client.py 端到端验证。）
"""
import ctypes
import json
import os
import shutil
import struct
import tempfile
import time
import unittest

from state import (InputState, SNAPSHOT_SIZE, KEY_ORDER, MOUSE_ORDER,
                   MAGIC, VERSION, parse_snapshot)
import hooks
import pipe_server
import presets


class SnapshotTests(unittest.TestCase):
    def test_roundtrip_and_size(self):
        st = InputState()
        st.set_key("Q", True)
        st.set_key("Shift", True)
        st.set_mouse("L", True)
        st.set_mouse("X2", True)
        st.mx, st.my, st.seq = 1234, -56, 7
        st.vx, st.vy, st.vw, st.vh = -1920, 0, 3840, 1080
        blob = st.serialize()
        self.assertEqual(len(blob), SNAPSHOT_SIZE)
        snap = parse_snapshot(blob)
        self.assertIsNotNone(snap)
        self.assertEqual(snap["keys"],
                         (1 << KEY_ORDER.index("Q")) | (1 << KEY_ORDER.index("Shift")))
        self.assertEqual(snap["mouse"],
                         (1 << MOUSE_ORDER.index("L")) | (1 << MOUSE_ORDER.index("X2")))
        self.assertEqual((snap["mx"], snap["my"], snap["seq"]), (1234, -56, 7))
        self.assertEqual((snap["vx"], snap["vy"], snap["vw"], snap["vh"]),
                         (-1920, 0, 3840, 1080))
        self.assertIn("extra", snap)
        self.assertEqual(len(snap["extra"]), 32)

    def test_serialize_sizes(self):
        st = InputState()
        self.assertEqual(len(st.serialize()), SNAPSHOT_SIZE)

    def test_raw_input_throttled(self):
        # 限频：同一时刻（<1/500s）内的事件直接跳过，不解析 lParam
        hooks._last_raw_ts = time.monotonic()
        hooks._handle_raw_input(0)  # 限频内应静默返回，不抛异常
        # 时间拨到很久以前 → 放行（lParam=0 → GetRawInputData 失败 → 静默返回）
        hooks._last_raw_ts = 0.0
        hooks._handle_raw_input(0)

    def test_accumulate_motion_scale_and_clamp(self):
        # 隐藏光标累计：按校准比例缩放，并钳制到虚拟屏幕范围
        hooks._state = InputState()
        hooks._state.vx, hooks._state.vy = 0, 0
        hooks._state.vw, hooks._state.vh = 1000, 800
        real_sx, real_sy = hooks._scale_x, hooks._scale_y
        hooks._scale_x, hooks._scale_y = 2.0, 0.5
        try:
            hooks._accumulate_motion(100, 100)
            self.assertEqual((hooks._state.mx, hooks._state.my), (200, 50))
            # 回归"泵线程崩溃(required argument is not an integer)"：
            # 浮点比例累计后 mx/my 必须是整数，serialize 不得抛错
            self.assertIsInstance(hooks._state.mx, int)
            self.assertIsInstance(hooks._state.my, int)
            self.assertEqual(len(hooks._state.serialize()), SNAPSHOT_SIZE)
            # 越界钳制到虚拟屏幕（不再无界漂移）
            hooks._accumulate_motion(5000, 0)
            self.assertEqual((hooks._state.mx, hooks._state.my), (1000, 50))
            hooks._accumulate_motion(-10000, 0)
            self.assertEqual((hooks._state.mx, hooks._state.my), (0, 50))
        finally:
            hooks._scale_x, hooks._scale_y = real_sx, real_sy

    def test_calibrate_scale(self):
        # 桌面校准：10 个原始增量对应光标位移 30px → sx=0.5*1+0.5*3=2.0
        hooks._cal_at = 0.0
        hooks._cal_raw_dx = hooks._cal_raw_dy = 0.0
        hooks._cal_cur_dx = hooks._cal_cur_dy = 0.0
        hooks._cal_last_pos = (0, 0)
        real_pos = hooks._read_cursor_position
        real_sx, real_sy = hooks._scale_x, hooks._scale_y
        hooks._scale_x, hooks._scale_y = 1.0, 1.0
        try:
            hooks._read_cursor_position = lambda: (30, 40)
            hooks._calibrate_scale(10, 0)
            self.assertAlmostEqual(hooks._scale_x, 2.0, places=2)
            self.assertAlmostEqual(hooks._scale_y, 1.0, places=2)  # 无纵向原始增量，不更新
        finally:
            hooks._read_cursor_position = real_pos
            hooks._scale_x, hooks._scale_y = real_sx, real_sy

    def test_calibrate_scale_locked_cursor_keeps_scale(self):
        # 回归"游戏内光标不动"：光标锁定（增量持续但位置不动）时系数不得衰减
        hooks._cal_at = 0.0
        hooks._cal_raw_dx = hooks._cal_raw_dy = 0.0
        hooks._cal_cur_dx = hooks._cal_cur_dy = 0.0
        hooks._cal_last_pos = (500, 300)
        real_pos = hooks._read_cursor_position
        real_sx, real_sy = hooks._scale_x, hooks._scale_y
        hooks._scale_x, hooks._scale_y = 1.0, 1.0
        try:
            hooks._read_cursor_position = lambda: (500, 300)  # 位置不动
            hooks._calibrate_scale(100, 100)                  # 增量持续到达
            self.assertEqual(hooks._scale_x, 1.0)             # 系数保持，不衰减
            self.assertEqual(hooks._scale_y, 1.0)
        finally:
            hooks._read_cursor_position = real_pos
            hooks._scale_x, hooks._scale_y = real_sx, real_sy

    def test_visibility_debounced(self):
        # 可见性闪烁（<150ms）不切换模式，返回稳定值
        real_get = hooks.user32.GetCursorInfo
        real_state = hooks._vis_state
        hooks._vis_state = True
        hooks._vis_change_at = 0.0

        def fake_hidden(ci):
            ctypes.cast(ci, ctypes.POINTER(hooks.CURSORINFO)).contents.flags = 0
            return True

        try:
            hooks.user32.GetCursorInfo = fake_hidden
            hooks._cursor_visible()
            self.assertTrue(hooks._cursor_visible())  # 两次快速调用仍为可见（去抖中）
            self.assertTrue(hooks._vis_state)
        finally:
            hooks.user32.GetCursorInfo = real_get
            hooks._vis_state = real_state
            hooks._vis_change_at = 0.0

    def test_parse_rejects_short(self):
        self.assertIsNone(parse_snapshot(b"\x00" * 10))

    def test_parse_rejects_bad_magic(self):
        data = b"XXXX" + bytes(16)
        self.assertIsNone(parse_snapshot(data))

    def test_keys_bits(self):
        st = InputState()
        st.set_key("Q", True)
        st.set_key("Q", False)
        self.assertEqual(st.keys, 0)
        st.set_key("W", True)
        self.assertEqual(st.keys, 1 << 1)
        st.set_key("Space", True)
        self.assertEqual(st.keys, (1 << 1) | (1 << 11))

    def test_set_vk_roundtrip(self):
        # v3：256 VK 位图按 vk>>3 索引字节、vk&7 索引位；置位/复位往返
        def bit(vk):
            return (vk >> 3, 1 << (vk & 7))

        st = InputState()
        for vk in (0x51, 0x70):  # 'Q' 与 'F1'
            b, m = bit(vk)
            self.assertEqual(st.extra[b] & m, 0)   # 初始松开
            st.set_vk(vk, True)
            self.assertEqual(st.extra[b] & m, m)   # 按下置位
        b, m = bit(0x51)
        st.set_vk(0x51, False)
        self.assertEqual(st.extra[b] & m, 0)       # 松开复位
        b2, m2 = bit(0x70)
        self.assertEqual(st.extra[b2] & m2, m2)    # 另一键不受影响
        st.set_vk(0x70, False)
        self.assertEqual(st.extra[b2] & m2, 0)

    def test_set_vk_bounds_and_clamp(self):
        st = InputState()
        st.set_vk(0, True)      # VK 0 → 字节 0 位 0
        self.assertEqual(st.extra[0] & 1, 1)
        st.set_vk(255, True)    # VK 255 → 字节 31 位 7
        self.assertEqual(st.extra[31] & 0x80, 0x80)
        st.set_vk(256, True)    # 越界截断 → 等价于 VK 0
        self.assertEqual(st.extra[0] & 1, 1)
        self.assertEqual(st.extra[31] & 0x80, 0x80)  # 高位不受影响

    def test_serialize_length_is_68(self):
        st = InputState()
        st.set_vk(0x51, True)
        self.assertEqual(len(st.serialize()), 68)
        self.assertEqual(len(st.serialize()), SNAPSHOT_SIZE)

    def test_parse_extra_preserved(self):
        st = InputState()
        st.set_vk(0x51, True)   # 'Q'
        st.set_vk(0x70, True)   # 'F1'
        st.set_vk(0x9C, True)   # 高位区
        blob = st.serialize()
        snap = parse_snapshot(blob)
        self.assertIsNotNone(snap)
        self.assertEqual(snap["extra"], bytes(st.extra))
        self.assertEqual(len(snap["extra"]), 32)
        for vk in (0x51, 0x70, 0x9C):
            self.assertTrue(snap["extra"][vk >> 3] & (1 << (vk & 7)),
                            "VK 0x%02X 位未在解析结果中置位" % vk)

    def test_v3_snapshot_parseable(self):
        st = InputState()
        st.set_vk(0x41, True)   # 'A'
        blob = st.serialize()
        self.assertEqual(blob[:4], MAGIC)
        self.assertEqual(blob[4], VERSION)      # version 字段 = 3
        self.assertEqual(len(blob), 68)
        snap = parse_snapshot(blob)
        self.assertIsNotNone(snap)
        self.assertEqual(snap["seq"], st.seq)
        # 旧 v2 快照（36 字节，无 extra）必须被拒绝（ver 不匹配）
        v2 = struct.pack("<4sBHBiiiiiiI", MAGIC, 2, 0, 0, 0, 0, 0, 0, 1920, 1080, 0)
        self.assertIsNone(parse_snapshot(v2))


class HookMappingTests(unittest.TestCase):
    def setUp(self):
        hooks._state = InputState()

    def _kb(self, vk, down=True):
        kbd = hooks.KBDLLHOOKSTRUCT()
        kbd.vkCode = vk
        w = hooks.WM_KEYDOWN if down else hooks.WM_KEYUP
        hooks._keyboard_proc(0, w, ctypes.addressof(kbd))

    def _ms(self, msg, x=0, y=0, xbtn=0):
        ms = hooks.MSLLHOOKSTRUCT()
        ms.pt.x, ms.pt.y = x, y
        ms.mouseData = xbtn << 16
        hooks._mouse_proc(0, msg, ctypes.addressof(ms))

    def _wheel(self, delta):
        """构造 WM_MOUSEWHEEL：mouseData 高 16 位为有符号滚轮增量。"""
        ms = hooks.MSLLHOOKSTRUCT()
        ms.mouseData = (delta << 16) & 0xFFFFFFFF
        hooks._mouse_proc(0, hooks.WM_MOUSEWHEEL, ctypes.addressof(ms))

    @staticmethod
    def _vk(vk):
        return vk >> 3, 1 << (vk & 7)

    def test_keyboard_down_up(self):
        for name, vk in hooks.VK.items():
            self._kb(vk, True)
            self.assertTrue(hooks._state.keys & (1 << KEY_ORDER.index(name)),
                            name + " 按下未置位")
            self._kb(vk, False)
            self.assertFalse(hooks._state.keys & (1 << KEY_ORDER.index(name)),
                             name + " 松开未清除")

    def test_right_modifiers_share_label(self):
        self._kb(0xA1, True)  # RightShift
        self.assertTrue(hooks._state.keys & (1 << KEY_ORDER.index("Shift")))
        self._kb(0xA3, True)  # RightCtrl
        self.assertTrue(hooks._state.keys & (1 << KEY_ORDER.index("Ctrl")))
        self._kb(0xA5, True)  # RightAlt
        self.assertTrue(hooks._state.keys & (1 << KEY_ORDER.index("Alt")))

    def test_mouse_buttons(self):
        cases = [("L", hooks.WM_LBUTTONDOWN, hooks.WM_LBUTTONUP),
                 ("R", hooks.WM_RBUTTONDOWN, hooks.WM_RBUTTONUP),
                 ("M", hooks.WM_MBUTTONDOWN, hooks.WM_MBUTTONUP)]
        for name, down, up in cases:
            self._ms(down)
            self.assertTrue(hooks._state.mouse & (1 << MOUSE_ORDER.index(name)))
            self._ms(up)
            self.assertFalse(hooks._state.mouse & (1 << MOUSE_ORDER.index(name)))

    def test_side_buttons(self):
        self._ms(hooks.WM_XBUTTONDOWN, xbtn=1)
        self.assertTrue(hooks._state.mouse & (1 << MOUSE_ORDER.index("X1")))
        self._ms(hooks.WM_XBUTTONUP, xbtn=1)
        self.assertFalse(hooks._state.mouse & (1 << MOUSE_ORDER.index("X1")))
        self._ms(hooks.WM_XBUTTONDOWN, xbtn=2)
        self.assertTrue(hooks._state.mouse & (1 << MOUSE_ORDER.index("X2")))

    def test_mouse_buttons_write_vk_bitmap(self):
        # 鼠标按键按下/松开同步写入 VK 位图（L=0x01 R=0x02 M=0x04 X1=0x05 X2=0x06）
        cases = [
            ("L", hooks.WM_LBUTTONDOWN, hooks.WM_LBUTTONUP, 0),
            ("R", hooks.WM_RBUTTONDOWN, hooks.WM_RBUTTONUP, 0),
            ("M", hooks.WM_MBUTTONDOWN, hooks.WM_MBUTTONUP, 0),
            ("X1", hooks.WM_XBUTTONDOWN, hooks.WM_XBUTTONUP, 1),
            ("X2", hooks.WM_XBUTTONDOWN, hooks.WM_XBUTTONUP, 2),
        ]
        for name, down_msg, up_msg, xbtn in cases:
            vk = hooks.MOUSE_VK[name]
            b, m = self._vk(vk)
            self._ms(down_msg, xbtn=xbtn)
            self.assertTrue(hooks._state.extra[b] & m,
                            name + " 按下未置位 VK 0x%02X 位" % vk)
            self._ms(up_msg, xbtn=xbtn)
            self.assertFalse(hooks._state.extra[b] & m,
                             name + " 松开未复位 VK 0x%02X 位" % vk)

    def test_wheel_up_sets_bit_and_expires(self):
        self._wheel(120)
        b, m = self._vk(hooks.WHEEL_UP_VK)
        self.assertTrue(hooks._state.extra[b] & m, "滚轮上未置位")
        # 刚点亮未过期 → 不熄灭
        hooks.expire_wheel()
        self.assertTrue(hooks._state.extra[b] & m, "未过期却被熄灭")
        # 伪造过期时间戳 → 自动熄灭
        hooks._wheel_up_ts = time.monotonic() - 1.0
        hooks.expire_wheel()
        self.assertFalse(hooks._state.extra[b] & m, "过期后未熄灭")
        self.assertEqual(hooks._wheel_up_ts, 0.0)

    def test_wheel_down_sets_bit_and_expires(self):
        self._wheel(-120)
        b, m = self._vk(hooks.WHEEL_DOWN_VK)
        self.assertTrue(hooks._state.extra[b] & m, "滚轮下未置位")
        hooks.expire_wheel()
        self.assertTrue(hooks._state.extra[b] & m, "未过期却被熄灭")
        hooks._wheel_down_ts = time.monotonic() - 1.0
        hooks.expire_wheel()
        self.assertFalse(hooks._state.extra[b] & m, "过期后未熄灭")
        self.assertEqual(hooks._wheel_down_ts, 0.0)

    def test_wheel_directions_are_exclusive(self):
        # 单个事件只置对应方向位：上滚只置 0x07、下滚只置 0x08
        self._wheel(120)
        b, m = self._vk(hooks.WHEEL_DOWN_VK)
        self.assertFalse(hooks._state.extra[b] & m, "上滚误置滚轮下位")
        # 模拟上滚点亮过期熄灭后，下滚只置 0x08
        hooks._wheel_up_ts = time.monotonic() - 1.0
        hooks.expire_wheel()
        self._wheel(-120)
        b, m = self._vk(hooks.WHEEL_UP_VK)
        self.assertFalse(hooks._state.extra[b] & m, "下滚误置滚轮上位")

    def test_wheel_default_ts_expire_is_noop(self):
        # 时间戳默认 0（从未点亮）→ expire 不触碰任何位
        hooks._wheel_up_ts = 0.0
        hooks._wheel_down_ts = 0.0
        hooks.expire_wheel()
        self.assertEqual(bytes(hooks._state.extra), bytes(32))

    def test_reconcile_skips_wheel_vks(self):
        # 滚轮位是瞬时事件：reconcile 不得用真实键码状态（0x08=VK_BACK）覆盖
        real_get = hooks._get_async_key_state
        hooks._get_async_key_state = lambda vk: True  # 假装所有键都按下
        try:
            hooks._state.set_vk(hooks.WHEEL_UP_VK, True)
            hooks._state.set_vk(hooks.WHEEL_DOWN_VK, True)
            hooks.reconcile(hooks._state)
            for vk in (hooks.WHEEL_UP_VK, hooks.WHEEL_DOWN_VK):
                b, m = self._vk(vk)
                self.assertTrue(hooks._state.extra[b] & m,
                                "reconcile 清掉了滚轮 VK 0x%02X 位" % vk)
        finally:
            hooks._get_async_key_state = real_get

    def test_mouse_move_does_not_write_position(self):
        # 坐标改由 60Hz GetCursorPos 轮询 / RAWINPUT 累计维护，WM_MOUSEMOVE 不再写坐标
        hooks._state.mx, hooks._state.my = 100, 200
        self._ms(hooks.WM_MOUSEMOVE, x=321, y=654)
        self.assertEqual((hooks._state.mx, hooks._state.my), (100, 200))

    def test_sync_mouse_position_visible_updates(self):
        # 光标可见：60Hz 校准以 GetCursorPos 为准
        hooks._state.mx, hooks._state.my = 0, 0
        real_v, real_r = hooks._cursor_visible, hooks._read_cursor_position
        hooks._cursor_visible = lambda: True
        hooks._read_cursor_position = lambda: (123, 456)
        try:
            hooks.sync_mouse_position(hooks._state)
            self.assertEqual((hooks._state.mx, hooks._state.my), (123, 456))
        finally:
            hooks._cursor_visible, hooks._read_cursor_position = real_v, real_r

    def test_sync_mouse_position_hidden_keeps_position(self):
        # 光标隐藏（游戏）：60Hz 校准不写入，坐标交给 RAWINPUT 增量累计
        hooks._state.mx, hooks._state.my = 100, 200
        real_v = hooks._cursor_visible
        hooks._cursor_visible = lambda: False
        try:
            hooks.sync_mouse_position(hooks._state)
            self.assertEqual((hooks._state.mx, hooks._state.my), (100, 200))
        finally:
            hooks._cursor_visible = real_v


class PipeServerTests(unittest.TestCase):
    def test_security_attributes_construction(self):
        # 不提供包 SID 时应正常构造
        sec = pipe_server._security_attributes(None)
        self.assertIsNotNone(sec.lpSecurityDescriptor)

    def test_derive_package_sid_no_crash(self):
        self.assertIsNone(pipe_server._derive_package_sid("__nonexistent__"))

    def test_snapshot_is_68_bytes(self):
        st = InputState()
        self.assertEqual(len(st.serialize()), SNAPSHOT_SIZE)


class StopFlagTests(unittest.TestCase):
    def test_set_and_wait(self):
        flag = pipe_server.StopFlag()
        self.assertFalse(flag.is_set())
        flag.set()
        self.assertTrue(flag.is_set())
        self.assertTrue(flag.wait(0.1))


class PresetsTests(unittest.TestCase):
    """presets.py：包外 JSON 持久化（load / save / 损坏备份 / 原子写）。"""

    def setUp(self):
        self._tmp = tempfile.mkdtemp(prefix="presets_test_")
        # 注入到临时目录下的 KeyDisplay\presets.json（父目录刻意不存在）
        self._path = os.path.join(self._tmp, "KeyDisplay", "presets.json")
        presets.set_presets_path(self._path)

    def tearDown(self):
        presets.set_presets_path(None)
        shutil.rmtree(self._tmp, ignore_errors=True)

    @staticmethod
    def _sample():
        return {
            "version": 1,
            "themePresets": [
                {"name": "配色A", "type": "theme",
                 "savedAt": "2026-08-19T12:00:00",
                 "data": {"theme": "custom", "colors": {"panel": "#B3FFB3C6"}}},
            ],
            "layoutPresets": [
                {"name": "布局A", "type": "layout",
                 "savedAt": "2026-08-19T12:00:00",
                 "data": {"layoutLocked": False, "keys": {"W": "0,0,44,44"}}},
            ],
        }

    def test_load_missing_returns_empty_struct(self):
        obj = presets.load()
        self.assertEqual(
            obj, {"version": 1, "themePresets": [], "layoutPresets": []})
        # 每次返回独立副本：篡改返回值不影响后续读取
        obj["themePresets"].append("x")
        self.assertEqual(presets.load()["themePresets"], [])

    def test_save_then_load_roundtrip(self):
        sample = self._sample()
        presets.save(sample)
        self.assertTrue(os.path.exists(self._path))
        # 原子写：无 .tmp 残留
        self.assertFalse(os.path.exists(self._path + ".tmp"))
        self.assertEqual(presets.load(), sample)
        # 父目录不存在时 save 自动创建
        self.assertTrue(os.path.isdir(os.path.dirname(self._path)))
        # UTF-8 无 BOM
        with open(self._path, "rb") as f:
            self.assertNotEqual(f.read(3), b"\xef\xbb\xbf")
        # 中文以 UTF-8 原文落盘（ensure_ascii=False）
        with open(self._path, "r", encoding="utf-8") as f:
            self.assertIn("配色A", f.read())

    def test_load_corrupt_backs_up_and_returns_empty(self):
        os.makedirs(os.path.dirname(self._path), exist_ok=True)
        with open(self._path, "w", encoding="utf-8") as f:
            f.write("{ 这不是合法 JSON")
        obj = presets.load()
        self.assertEqual(
            obj, {"version": 1, "themePresets": [], "layoutPresets": []})
        self.assertFalse(os.path.exists(self._path))
        self.assertTrue(os.path.exists(self._path + ".bak"))
        with open(self._path + ".bak", "r", encoding="utf-8") as f:
            self.assertEqual(f.read(), "{ 这不是合法 JSON")

    def test_load_non_object_json_is_corrupt(self):
        # 合法 JSON 但顶层不是对象（如数组）→ 同样按损坏处理：备份 + 空结构
        os.makedirs(os.path.dirname(self._path), exist_ok=True)
        with open(self._path, "w", encoding="utf-8") as f:
            json.dump(["not", "a", "dict"], f)
        obj = presets.load()
        self.assertEqual(
            obj, {"version": 1, "themePresets": [], "layoutPresets": []})
        self.assertTrue(os.path.exists(self._path + ".bak"))


class PipeCommandTests(unittest.TestCase):
    """pipe_server 的 CMD 帧路由（纯函数 _parse_cmd，无需真实管道）。"""

    def test_parse_cmd_routing(self):
        self.assertEqual(pipe_server._parse_cmd(b"CMD|GET_PRESETS"),
                         ("GET_PRESETS", None))
        self.assertEqual(pipe_server._parse_cmd(b"CMD|PUT_PRESETS|{\"a\":1}"),
                         ("PUT_PRESETS", "{\"a\":1}"))
        self.assertEqual(pipe_server._parse_cmd(b"CMD|GET_PRESETS|x"),  # 未知 CMD
                         (None, None))
        self.assertEqual(pipe_server._parse_cmd(b"CMD|"), (None, None))
        # 二进制 KDSP 等异常数据一律忽略
        self.assertEqual(pipe_server._parse_cmd(b"KDSP" + bytes(64)), (None, None))
        self.assertEqual(pipe_server._parse_cmd(b"\x00\x01\x02\xff"), (None, None))


if __name__ == "__main__":
    unittest.main(verbosity=2)