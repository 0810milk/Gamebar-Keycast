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
        st.ux, st.uy = 333, 666
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
        self.assertEqual((snap["ux"], snap["uy"]), (333, 666))

    def test_serialize_sizes(self):
        st = InputState()
        self.assertEqual(len(st.serialize()), SNAPSHOT_SIZE)

    def test_normalize_position(self):
        # 活动范围 x∈[100,300], y∈[0,100]：x 两端→0/1000，中间→500
        self.assertEqual(pipe_server.normalize_position(100, 50, 100, 300, 0, 100),
                         (0, 500))
        self.assertEqual(pipe_server.normalize_position(300, 50, 100, 300, 0, 100),
                         (1000, 500))
        self.assertEqual(pipe_server.normalize_position(200, 50, 100, 300, 0, 100),
                         (500, 500))
        # 范围退化（单点）时返回中间值
        self.assertEqual(pipe_server.normalize_position(50, 50, 50, 50, 50, 50),
                         (500, 500))
        # 越界钳制到 0..1000
        self.assertEqual(pipe_server.normalize_position(-100, 50, 100, 300, 0, 100),
                         (0, 500))
        self.assertEqual(pipe_server.normalize_position(9999, 50, 100, 300, 0, 100),
                         (1000, 500))

    def test_raw_input_throttled(self):
        # 限频：同一时刻（<1/500s）内的事件直接跳过，不解析 lParam
        hooks._last_raw_ts = time.monotonic()
        hooks._handle_raw_input(0)  # 限频内应静默返回，不抛异常
        # 时间拨到很久以前 → 放行（lParam=0 → GetRawInputData 失败 → 静默返回）
        hooks._last_raw_ts = 0.0
        hooks._handle_raw_input(0)

    def test_update_normalized_end_to_end(self):
        # 回归：_update_normalized 的窗口推进参数顺序错误会崩溃/产出错误归一化。
        from collections import deque
        st = InputState()
        st.vw, st.vh = 1920, 1080
        ps = pipe_server.PipeServer.__new__(pipe_server.PipeServer)
        ps._state = st
        h = deque()
        # 单点 → 退化 → 中心
        st.mx, st.my = 400, 300
        ps._update_normalized(h, 10)
        self.assertEqual((st.ux, st.uy), (500, 500))
        # 窗口 [100,400] 内 x=400 → 1000（满行程）
        st.mx, st.my = 100, 300
        ps._update_normalized(h, 10)
        st.mx, st.my = 400, 300
        ps._update_normalized(h, 10)
        self.assertEqual(st.ux, 1000)
        # 静止不推进：窗口不变，归一化值不变（不漂移）
        ps._update_normalized(h, 10)
        self.assertEqual(st.ux, 1000)

    def test_normalize_frozen_cursor_does_not_drift(self):
        # 冻结光标时窗口也冻结（_push_sample 不推进）→ 归一化值不变；
        # 若窗口被强制收敛，地板会把 span<floor 的窗口锚定在中心，值稳定在 500。
        from collections import deque
        h = deque([(100, 0), (300, 0), (400, 0)])
        # 位置未变 → 不推进、不过期（窗口保持 [100,400]，ux 保持 1000 不变）
        self.assertFalse(pipe_server.PipeServer._push_sample(h, 10, 400, 0))
        self.assertEqual(list(h), [(100, 0), (300, 0), (400, 0)])
        ux, _ = pipe_server.normalize_position(400, 0, 100, 400, 0, 100)
        self.assertEqual(ux, 1000)
        # 位置变化 → 推进，窗口随之更新
        self.assertTrue(pipe_server.PipeServer._push_sample(h, 10, 401, 0))
        self.assertEqual(len(h), 4)
        # 即便窗口被人为收敛到 span<floor，地板锚定也让值稳定在 500（不漂移）
        for lo, hi in [(380, 420), (400, 400)]:
            ux2, _ = pipe_server.normalize_position(400, 400, lo, hi, 0, 100, 192.0, 96.0)
            self.assertEqual(ux2, 500, "window=[%d,%d] 不应漂移" % (lo, hi))

    def test_normalize_floor_damps_jitter(self):
        # 微抖动 ±2px 在 192px 地板内只引起 ~1% 位移，不会被放大成满垫跑动
        ux_a, _ = pipe_server.normalize_position(398, 0, 398, 402, 0, 100, 192.0, 96.0)
        ux_b, _ = pipe_server.normalize_position(402, 0, 398, 402, 0, 100, 192.0, 96.0)
        self.assertLessEqual(abs(ux_a - ux_b), 30)
        # 活动范围大于地板时仍保持满行程自适应
        ux_full, _ = pipe_server.normalize_position(100, 0, 100, 500, 0, 100, 192.0, 96.0)
        self.assertEqual(ux_full, 0)
        ux_full2, _ = pipe_server.normalize_position(500, 0, 100, 500, 0, 100, 192.0, 96.0)
        self.assertEqual(ux_full2, 1000)

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