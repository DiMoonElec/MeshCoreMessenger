"""Optional native SerialPort smoke test on macOS/Linux, using a private pseudo-terminal.

Build Release first, then run: python3 tests/MeshCoreSharp.Tests/serial_pty_smoke.py
No physical serial device is opened.
"""

import os
from pathlib import Path
import select
import subprocess
import time
import tty


def main():
    assembly = Path(__file__).resolve().parent / "bin/Release/net10.0/MeshCoreSharp.Tests.dll"
    master, slave = os.openpty()
    process = None
    try:
        tty.setraw(slave)
        process = subprocess.Popen(
            ["dotnet", str(assembly), "--serial-pty", os.ttyname(slave)],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
        )
        wire = bytearray()
        deadline = time.monotonic() + 8
        while len(wire) < 4:
            if process.poll() is not None:
                raise RuntimeError(process.communicate()[0])
            if time.monotonic() >= deadline:
                raise TimeoutError("Serial client did not send GET_DEVICE_TIME")
            readable, _, _ = select.select([master], [], [], 0.1)
            if readable:
                wire.extend(os.read(master, 1024))
        assert wire == bytes.fromhex("3C 01 00 05"), wire.hex()
        for fragment in (b">", b"\x05", b"\x00\x09\x78", b"\x56\x34\x12"):
            os.write(master, fragment)
            time.sleep(0.01)
        output, _ = process.communicate(timeout=8)
        print(output, end="")
        if process.returncode != 0:
            raise RuntimeError(f"Serial client exited with code {process.returncode}")
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.communicate()
        os.close(master)
        os.close(slave)


if __name__ == "__main__":
    main()
