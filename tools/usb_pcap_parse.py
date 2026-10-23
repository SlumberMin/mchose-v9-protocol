# -*- coding: utf-8 -*-
"""
USBPcap .pcap reader - pull USB transfers out of a capture and diff two runs.

Verified header layout (28 bytes, measured from a real capture on this machine):

    off 0..1    headerLen            = 28
    off 14      info                 bit0 = 1 -> host to device (OUT)
    off 17..18  idA                  device-ish selector
    off 19..20  idB                  bus-ish selector
    off 21      endpoint
    off 22      transfer             0 iso, 1 interrupt, 2 control, 3 bulk
    off 23..26  dataLength           (== incl_len - headerLen)

Payload starts at `headerLen`. For a control transfer the first 8 payload bytes
are the setup packet (bmRequestType, bRequest, wValue, wIndex, wLength).

Sanity check that pinned this down: the first packets of a capture decode as
`80 06 00 01 00 00 12 00` (GET_DESCRIPTOR device, 18 bytes) followed by an
18-byte device descriptor `12 01 00 02 09 00 01 40 ...`, which only lines up
with a 28-byte header.

Usage:
    python usb_pcap_parse.py active.pcap             # list OUT transfers with data
    python usb_pcap_parse.py idle.pcap active.pcap   # diff active minus idle
"""
import struct
import sys

HDR_LEN = 28

SETUP_NAMES = {
    (0x00, 0x09): "SET_CONFIGURATION",
    (0x00, 0x0B): "SET_INTERFACE",
    (0x00, 0x01): "CLEAR_FEATURE",
    (0x02, 0x01): "CLEAR_FEATURE(class)",
    (0x21, 0x0A): "HID SET_IDLE",
    (0x21, 0x09): "HID SET_REPORT",
    (0x21, 0x0B): "HID SET_PROTOCOL",
    (0x22, 0x01): "HID SET_REPORT(feature)",
    (0xA1, 0x01): "HID GET_REPORT",
    (0x80, 0x06): "GET_DESCRIPTOR",
    (0x81, 0x06): "GET_DESCRIPTOR(iface)",
    (0x82, 0x06): "GET_DESCRIPTOR(ep)",
    (0xC0, 0x01): "vendor IN",
    (0x40, 0x01): "vendor OUT",
}


def read_pcap(path):
    raw = open(path, "rb").read()
    if len(raw) < 24:
        return []
    magic = raw[:4]
    endian = "<" if magic in (b"\xd4\xc3\xb2\xa1", b"\x4d\x3c\xb2\xa1") else ">"
    off = 24
    out = []
    while off + 16 <= len(raw):
        ts_sec, ts_usec, incl, orig = struct.unpack(endian + "IIII",
                                                    raw[off:off + 16])
        off += 16
        data = raw[off:off + incl]
        off += incl
        out.append((ts_sec + ts_usec / 1e6, data))
    return out


def parse(data):
    if len(data) < 20:
        return None
    # headerLen is given per packet and is 27 for interrupt transfers, 28 for
    # control transfers on this build. Never clamp it upward: the old code
    # forced it to 28, which silently dropped the first payload byte of every
    # interrupt report (the report id) and made 64-byte frames look like 63.
    header_len = struct.unpack_from("<H", data, 0)[0]
    if header_len < 20 or header_len > len(data):
        header_len = 27
    return {
        "info": data[16],
        "status": struct.unpack_from("<I", data, 10)[0],
        "idA": struct.unpack_from("<H", data, 17)[0],
        "idB": struct.unpack_from("<H", data, 19)[0],
        "ep": data[21],
        "transfer": data[22],
        "dlen": struct.unpack_from("<I", data, 23)[0],
        "data": data[header_len:],
    }


def is_out(p):
    return bool(p["info"] & 1)


def setup_of(p):
    if p["transfer"] != 2 or len(p["data"]) < 8:
        return None
    return struct.unpack("<BBHHH", p["data"][:8])


def label(p):
    s = setup_of(p)
    if s:
        name = SETUP_NAMES.get((s[0], s[1]), "req")
        return "ctl bm=%02X bReq=%02X wVal=%04X wIdx=%04X wLen=%d %s" % (
            s[0], s[1], s[2], s[3], s[4], name)
    kind = {0: "iso", 1: "int", 2: "ctl", 3: "bulk"}.get(p["transfer"], "?")
    return "%s ep=0x%02X" % (kind, p["ep"])


def short(p, limit=40):
    d = p["data"]
    if p["transfer"] == 2 and len(d) >= 8:
        d = d[8:]
    h = d[:limit].hex(" ")
    if len(d) > limit:
        h += " ... (%d bytes)" % len(d)
    return h or "(empty)"


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return 1

    if len(args) == 1:
        rows = [parse(d) for _, d in read_pcap(args[0])]
        rows = [r for r in rows if r]
        print("packets: %d" % len(rows))
        print("\n=== OUT transfers carrying data ===")
        n = 0
        for p in rows:
            if not is_out(p) or len(p["data"]) <= 8:
                continue
            if p["transfer"] not in (1, 2, 3):
                continue
            n += 1
            print("  [%d] dev=%d bus=%d  %s\n      %s"
                  % (n, p["idA"], p["idB"], label(p), short(p)))
            if n >= 80:
                print("  ... truncated")
                break
        return 0

    idle = [parse(d) for _, d in read_pcap(args[0])]
    active = [parse(d) for _, d in read_pcap(args[1])]
    idle = [r for r in idle if r]
    active = [r for r in active if r]
    print("idle packets=%d  active packets=%d" % (len(idle), len(active)))

    def sig(p):
        d = p["data"]
        if p["transfer"] == 2 and len(d) >= 8:
            d = d[8:]
        return (p["idA"], p["idB"], p["ep"], p["transfer"], p["info"], d)

    idle_sigs = {sig(p) for p in idle}

    print("\n=== OUT transfers unique to the active capture ===")
    seen = set()
    n = 0
    for p in active:
        if not is_out(p):
            continue
        if p["transfer"] not in (1, 2, 3):
            continue
        s = sig(p)
        if s in idle_sigs or s in seen:
            continue
        seen.add(s)
        n += 1
        print("  [%d] dev=%d bus=%d  %s\n      %s"
              % (n, p["idA"], p["idB"], label(p), short(p)))
        if n >= 60:
            print("  ... truncated")
            break
    if n == 0:
        print("  none found")
    return 0


if __name__ == "__main__":
    sys.exit(main())
