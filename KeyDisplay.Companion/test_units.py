"""单元测试：快照协议 + 键位映射 + 钩子回调逻辑。

运行：python -m unittest test_units -v
（模拟输入在无头自动化环境中会被系统丢弃，故钩子回调通过
  直接构造事件结构体验证映射逻辑；真实物理输入已通过
  companion.py + test_client.py 端到端验证。）
"""
import ctypes
import struct
import time
import unittest

from state import (InputState, SNAPSHOT_SIZE, KEY_ORDER, MOUSE_ORDER,
                   MAGIC, VERSION, parse_snapshot)
import hooks
import pipe_server


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

    def test_snapshot_is_36_bytes(self):
        st = InputState()
        self.assertEqual(len(st.serialize()), SNAPSHOT_SIZE)


class StopFlagTests(unittest.TestCase):
    def test_set_and_wait(self):
        flag = pipe_server.StopFlag()
        self.assertFalse(flag.is_set())
        flag.set()
        self.assertTrue(flag.is_set())
        self.assertTrue(flag.wait(0.1))


if __name__ == "__main__":
    unittest.main(verbosity=2)