#!/usr/bin/env python3
"""Dumps a 7dtd dedicated server telnet console session to a transcript file.

Follows the workspace harness pattern (7dtd-server-container lib-env.sh):
send the password line immediately, then each command, then read the reply.
The server sends no banner, so the client must not wait for one. The game
resets any session whose first line is not the configured password.

The password comes from ZDT_TELNET_PASSWORD, or from a hidden prompt when
the variable is unset; it is never taken from argv, which is world-readable
in the process table.
"""

import argparse
import getpass
import os
import socket
import sys
import time
from pathlib import Path

PASSWORD_ENV = "ZDT_TELNET_PASSWORD"  # noqa: S105 - the env var's name, not a password


def drain(sock, window):
    data = b""
    try:
        while True:
            chunk = sock.recv(4096)
            if not chunk:
                break
            data += chunk
            sock.settimeout(window)
    except TimeoutError:
        pass
    return data


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="telnet_session.py",
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("host")
    parser.add_argument("port", type=int)
    parser.add_argument("outfile")
    parser.add_argument("commands", nargs="+", metavar="cmd")
    args = parser.parse_args(argv)

    password = os.environ.get(PASSWORD_ENV)
    if not password:
        password = getpass.getpass(f"telnet password ({PASSWORD_ENV}): ")

    transcript = []
    sock = socket.create_connection((args.host, args.port), timeout=10)
    sock.settimeout(1.0)

    sock.sendall(password.encode() + b"\n")
    time.sleep(0.3)

    for cmd in args.commands:
        sock.sendall(cmd.encode() + b"\n")
        time.sleep(0.5)
        transcript.append(drain(sock, 1.5))

    sock.close()
    with Path(args.outfile).open("wb") as f:
        for chunk in transcript:
            f.write(chunk)
            f.write(b"\n---\n")
    print(
        f"transcript written to {args.outfile} ({sum(len(c) for c in transcript)} bytes)",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
