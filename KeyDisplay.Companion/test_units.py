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
        self.assertEqual(len(st.serialize()), 36)

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

    def test_mouse_move_updates_position(self):
        self._ms(hooks.WM_MOUSEMOVE, x=321, y=654)
        self.assertEqual((hooks._state.mx, hooks._state.my), (321, 654))


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