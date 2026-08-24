#!/usr/bin/env python3
"""Dumps a 7dtd dedicated server telnet console session to a transcript file.

Usage: telnet_session.py <host> <port> <password> <outfile> <cmd> [cmd...]
Follows the workspace harness pattern (7dtd-server-container lib-env.sh):
send the password line immediately, then each command, then read the reply.
The server sends no banner, so the client must not wait for one. The game
resets any session whose first line is not the configured password.
"""

import socket
import sys
import time


def drain(sock, window):
    data = b""
    try:
        while True:
            chunk = sock.recv(4096)
            if not chunk:
                break
            data += chunk
            sock.settimeout(window)
    except socket.timeout:
        pass
    return data


def main():
    host, port, password, outfile = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]
    commands = sys.argv[5:]

    transcript = []
    sock = socket.create_connection((host, port), timeout=10)
    sock.settimeout(1.0)

    sock.sendall(password.encode() + b"\n")
    time.sleep(0.3)

    for cmd in commands:
        sock.sendall(cmd.encode() + b"\n")
        time.sleep(0.5)
        transcript.append(drain(sock, 1.5))

    sock.close()
    with open(outfile, "wb") as f:
        for chunk in transcript:
            f.write(chunk)
            f.write(b"\n---\n")
    print(f"transcript written to {outfile} ({sum(len(c) for c in transcript)} bytes)")


if __name__ == "__main__":
    main()
