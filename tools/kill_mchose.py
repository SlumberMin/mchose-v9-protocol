# -*- coding: utf-8 -*-
"""
Kill every MCHOSE HUB process, including the watchdog that restarts the app.

The watchdog respawns the app within a couple of seconds, so a single
Stop-Process pass is not enough. This loops: kill everything matching
MCHOSE*, wait, kill again, until two consecutive passes find nothing.

    python kill_mchose.py
"""
import subprocess
import sys
import time

PS_LIST = (
    "Get-CimInstance Win32_Process "
    "| Where-Object { $_.Name -match 'MCHOSE' } "
    "| ForEach-Object { '{0}|{1}|{2}' -f $_.ProcessId, $_.Name, $_.ExecutablePath }"
)
PS_KILL = (
    "Get-CimInstance Win32_Process "
    "| Where-Object { $_.Name -match 'MCHOSE' } "
    "| ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop; "
    "'killed ' + $_.ProcessId } catch { 'fail ' + $_.ProcessId } }"
)


def run(ps):
    r = subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                       capture_output=True, text=True, timeout=90)
    return (r.stdout or "") + (r.stderr or "")


def listing():
    out = run(PS_LIST)
    rows = []
    for line in out.splitlines():
        line = line.strip()
        if line.count("|") >= 2:
            pid, name, path = line.split("|", 2)
            rows.append((pid.strip(), name.strip(), path.strip()))
    return rows


def main():
    is_admin = "--check-admin" not in sys.argv
    print("pass 0 listing:")
    for pid, name, path in listing():
        print("   pid=%-6s %-32s %s" % (pid, name, path))

    quiet = 0
    for i in range(1, 7):
        out = run(PS_KILL)
        for line in out.splitlines():
            line = line.strip()
            if line.startswith(("killed", "fail")):
                print("  pass %d: %s" % (i, line))
        time.sleep(2.0)
        left = listing()
        print("  pass %d done, remaining: %d" % (i, len(left)))
        if not left:
            quiet += 1
            if quiet >= 2:
                print("CLEAN: no MCHOSE process left after two consecutive passes")
                return 0
        else:
            quiet = 0
            for pid, name, path in left:
                print("     still: pid=%s %s" % (pid, name))

    left = listing()
    print("WARNING: %d process(es) survived" % len(left))
    for pid, name, path in left:
        print("   pid=%s %s %s" % (pid, name, path))
    return 1


if __name__ == "__main__":
    sys.exit(main())
