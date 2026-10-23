# -*- coding: utf-8 -*-
"""
The fix, at last.

Because usb_pcap_parse clamped headerLen to 28 instead of the real 27, every
interrupt report lost its first byte. With that fixed, the wire frames read:

    app   64B:  aa 27 00 00 00 0a 01 03 05 00 03 00 ff 00 46 ...
    ours  64B:  00 27 00 00 00 0a 01 00 05 00 03 00 ff 00 00 ...
                ^^
                report id - the app sends 0xAA, we sent 0x00

So the firmware was receiving report 0, not report 0xAA, and ignoring it. The
64-byte data stage after the id is otherwise identical.

HidD_SetOutputReport takes a 65-byte buffer whose FIRST byte is the id; the
driver then puts the remaining 64 bytes on the wire. To make the wire start
with 0xAA we therefore need buffer = [0x00][0xAA][63-byte frame].

    python mchose_fix.py            # red / green / blue test
    python mchose_fix.py FF8800     # one colour
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time

import hid

VID, PID, UP = 0x3837, 0x6008, 0xFF00
REPORT_ID = 0xAA          # the id the app puts on the wire
WIRE_DATA = 64            # bytes after the id
BUF_LEN = 65              # OutputReportByteLength

hid_dll = ctypes.WinDLL("hid.dll", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32.dll", use_last_error=True)
kernel32.CreateFileW.restype = wt.HANDLE
kernel32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, wt.LPVOID,
                                 wt.DWORD, wt.DWORD, wt.HANDLE]
kernel32.CloseHandle.argtypes = [wt.HANDLE]


def frame(cmd=0x27, mode=0, rgb=(0, 0, 0), direction=0):
    """
    The 64-byte data stage as it appears on the wire: id first, then the command.

    app, verified:  aa 27 00 00 00 0a 01 03 05 00 03 00 ff 00 46
                    ^^ id  ^cmd        ^mode  ^dir     ^R  ^G  ^B
    """
    p = bytearray(WIRE_DATA)
    p[0] = REPORT_ID
    p[1] = cmd
    if cmd == 0x27:
        p[5] = 0x0A
        p[6] = 0x01
        p[7] = mode
        p[8] = 0x05
        p[9] = direction
        p[10] = 0x03
        p[12], p[13], p[14] = rgb
    return bytes(p)


def to_buf(data_stage):
    """The driver drops buffer[0], so the wire gets buffer[1:] (64 bytes)."""
    b = bytes([0x00]) + data_stage
    return b[:BUF_LEN].ljust(BUF_LEN, b"\x00")


def open_dev():
    for d in hid.enumerate(VID, PID):
        if d.get("usage_page") == UP:
            dev = hid.device()
            dev.open_path(d["path"])
            return dev
    return None


def send(dev, label, data_stage, settle=0.25):
    while dev.read(64, 5):
        pass
    n = dev.write(to_buf(data_stage))
    time.sleep(settle)
    rep = dev.read(64, 700)
    nz = bool(rep) and any(rep)
    print("  %-16s wrote=%2d  reply=%-8s %s"
          % (label, n, "NON-ZERO" if nz else "zeros",
             bytes(rep).hex(" ")[:52] if rep else "none"), flush=True)
    return bytes(rep) if nz else None


def main():
    dev = open_dev()
    if not dev:
        print("0x6008/0xFF00 not found")
        return 1
    print("wire target: %s" % to_buf(frame(0x27, 0, (255, 0, 0)))[:6].hex(" "))
    print("(byte0 is consumed by the driver, wire starts at byte1)\n")

    ack = send(dev, "07 query", frame(cmd=0x07), 0.4)
    if ack:
        txt = "".join(chr(b) if 32 <= b < 127 else "." for b in ack)
        print("        reply ascii: |%s|" % txt)
    time.sleep(0.3)

    if len(sys.argv) > 1:
        h = sys.argv[1].lstrip("#")
        plan = [(h, (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16)))]
    else:
        plan = [("RED", (255, 0, 0)), ("GREEN", (0, 255, 0)),
                ("BLUE", (0, 0, 255)), ("CYAN", (0, 254, 255))]

    hits = 0
    for name, rgb in plan:
        if send(dev, "27 " + name, frame(0x27, 0, rgb)):
            hits += 1
        time.sleep(2.5)

    dev.close()
    print("\nnon-zero replies: %d/%d" % (hits, len(plan)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
