#!/usr/bin/env python3
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at http://mozilla.org/MPL/2.0/.
#
# As a special exception to the Mozilla Public License, version 2.0, if you
# compile your application source code and portions of this software are
# embedded into the generated object code or executable form as a normal
# consequence of the compilation process (such as inline functions,
# templates, generics, or macros), you may redistribute such embedded portions
# in such object code or executable form without complying with the source code
# availability requirements or notice obligations of Section 3 of the MPL 2.0.

"""bjoweb's examples/hello.exe at fixed request rates: latency and CPU per rate.

A closed-loop tool (bench/bjoweb.sh's bombardier) sends a connection's next
request when its last one answers, so a stall slows the sender and hides
itself. This uses oha at a fixed rate over a fixed set of connections, with
--latency-correction: a request is timed from when it should have been sent,
so a stall shows in every request that waited through it.

Each rate gets a server of its own, pinned to CPUs 0-11, warmed up, and then
loaded for --duration seconds by oha pinned to CPUs 12-23 (on this 5900X 2k
and 2k+1 share a core: six whole cores each). A server that saw an overload
would otherwise carry its backlog into the next rate.

bjoweb answers 503 when its request queue (128 by default) is full. The ok
column is the 200s; the latency columns are of every answer, 503s included.
With the default scheduler the queue is where a burst waits: Kestrel's threads
go on reading requests while the workers' threads answer them, and 256
connections can have more requests in flight than 16 workers and 128 queued
hold, so a few are refused from 25k requests a second up, in 10 ms windows whose
slowest answer took a millisecond. With BJO_SCHEDULER=pool a request's fiber is
queued on the pool thread that posted it, which runs it before it takes more of
Kestrel's work, so the burst waits in Kestrel instead, unbounded, and nothing is
refused. With `#:queue 1024` neither refuses anything.

    (cd ../bjoweb && ../Bjolang/bjo/bjo build -c examples/hello.bjo)
    cargo install oha
    bench/bjoweb-rate.py                                  # both schedulers
    bench/bjoweb-rate.py --rates 50000,200000 --schedulers workers
"""
import argparse, json, os, signal, socket, subprocess, sys, time, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
WEB = os.environ.get("BJOWEB", os.path.join(os.path.dirname(os.path.dirname(HERE)), "bjoweb"))
OHA = os.environ.get("OHA", os.path.expanduser("~/.cargo/bin/oha"))
HOST, PORT = "127.0.0.1", 8080
URL = f"http://{HOST}:{PORT}"
TICK = os.sysconf("SC_CLK_TCK")


def cpu_s(pid):
    f = open(f"/proc/{pid}/stat").read().rsplit(")", 1)[1].split()
    return (int(f[11]) + int(f[12])) / TICK


def port_free():
    with socket.socket() as s:
        return s.connect_ex((HOST, PORT)) != 0


def start(scheduler):
    if not port_free():
        raise SystemExit(f"something already listens on {PORT}; a server left over?")
    env = dict(os.environ, DOTNET_gcServer="1", DOTNET_gcConcurrent="1", BJO_SCHEDULER=scheduler)
    # taskset execs dotnet, so this pid is the server's own.
    p = subprocess.Popen(["taskset", "-c", "0-11", "dotnet", os.path.join(WEB, "examples", "hello.exe")],
                         env=env, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                         stderr=subprocess.DEVNULL)
    for _ in range(100):
        try:
            urllib.request.urlopen(URL + "/", timeout=1).read()
            if open(f"/proc/{p.pid}/comm").read().strip() != "dotnet":
                raise SystemExit("the process answering is not the server started here")
            return p
        except (OSError, ValueError):
            time.sleep(0.1)
    p.kill()
    raise SystemExit("the server did not come up")


def stop(p):
    p.send_signal(signal.SIGINT)
    try:
        p.wait(10)
    except subprocess.TimeoutExpired:
        p.kill()
        p.wait()
    for _ in range(50):
        if port_free():
            return
        time.sleep(0.1)


def load(path, rate, duration, connections):
    out = subprocess.run(["taskset", "-c", "12-23", OHA, "-z", f"{duration}s", "-q", str(rate),
                          "-c", str(connections), "--latency-correction", "--no-tui",
                          "--output-format", "json", URL + path],
                         stdin=subprocess.DEVNULL, capture_output=True, check=True).stdout
    return json.loads(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rates", default="25000,50000,100000,150000,200000,230000,260000,300000")
    ap.add_argument("--schedulers", default="workers,pool")
    ap.add_argument("--path", default="/hello/ada")
    ap.add_argument("--duration", type=int, default=5)
    ap.add_argument("--connections", type=int, default=256)
    a = ap.parse_args()
    rates = [int(r) for r in a.rates.split(",")]

    print(f"GET {a.path}, {a.duration} s a rate over {a.connections} connections; "
          f"server on CPUs 0-11, oha on 12-23; a fresh server per rate")
    cols = ["got/s", "ok %", "p50 us", "p99 us", "p99.9 ms", "p99.99 ms", "max ms", "cores"]
    for scheduler in a.schedulers.split(","):
        print(f"\n## BJO_SCHEDULER={scheduler}")
        print(f"{'rate':>9}" + "".join(f"{c:>11}" for c in cols))
        for rate in rates:
            server = start(scheduler)
            try:
                load(a.path, min(rate, 50000), 3, a.connections)   # warm-up
                c0, t0 = cpu_s(server.pid), time.time()
                r = load(a.path, rate, a.duration, a.connections)
                cores = (cpu_s(server.pid) - c0) / (time.time() - t0)
            finally:
                stop(server)
            codes = r["statusCodeDistribution"]
            answered = sum(codes.values())
            pct = r["latencyPercentiles"]
            row = [f"{r['summary']['requestsPerSec']:.0f}",
                   f"{100 * codes.get('200', 0) / answered:.1f}" if answered else "-",
                   f"{pct['p50'] * 1e6:.0f}", f"{pct['p99'] * 1e6:.0f}",
                   f"{pct['p99.9'] * 1e3:.1f}", f"{pct['p99.99'] * 1e3:.1f}",
                   f"{r['summary']['slowest'] * 1e3:.0f}", f"{cores:.1f}"]
            print(f"{rate:>9}" + "".join(v.rjust(11) for v in row), flush=True)


if __name__ == "__main__":
    sys.exit(main())
