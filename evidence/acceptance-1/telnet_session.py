#!/usr/bin/env python3
"""Dumps a 7dtd dedicated server telnet console session to a transcript file.

Usage: telnet_session.py <host> <port> <outfile> <cmd> [cmd...]
Connects, answers the password prompt with an empty line, runs each command,
waits briefly, and writes everything received to the transcript.
"""

import socket
import sys
import time


def recv_until(sock, needle, timeout):
    sock.settimeout(timeout)
    data = b""
    try:
        while needle not in data:
            chunk = sock.recv(4096)
            if not chunk:
                break
            data += chunk
    except socket.timeout:
        pass
    return data


def main():
    host, port, outfile = sys.argv[1], int(sys.argv[2]), sys.argv[3]
    commands = sys.argv[4:]

    transcript = []
    sock = socket.create_connection((host, port), timeout=15)
    banner = recv_until(sock, b"password", 10)
    transcript.append(banner)
    sock.sendall(b"\n")  # empty password
    time.sleep(0.5)

    for cmd in commands:
        sock.sendall(cmd.encode() + b"\n")
        time.sleep(1.0)
        resp = recv_until(sock, b"\n", 2.0)
        transcript.append(resp)

    sock.close()
    with open(outfile, "wb") as f:
        for chunk in transcript:
            f.write(chunk)
            f.write(b"\n---\n")
    print(f"transcript written to {outfile} ({sum(len(c) for c in transcript)} bytes)")


if __name__ == "__main__":
    main()
