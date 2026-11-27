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

"""Compares (std read) with the compiler's reader.

    tools/read_conformance.py [path ...]

- Reads every .bjo file under the paths with both readers: the compiler's,
  through `bjolang --dump-read`, and (std read), through
  tools/read/read_dump.bjo. Both write the text of `Bjolang.Runtime.SyntaxDump`.
- With no paths, reads the .bjo files that git tracks. The test suite writes
  files of its own while this runs, and those are not read.
- A file whose two texts differ is a failure. The first lines that differ are
  written. An error is part of the text, so two readers that reject a file
  have to reject it with the same message.
- Exits with 0 when every file reads the same, and 1 when one does not.

Build the compiler and the standard library first: `./build_std.sh`.
"""

import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CHUNK = 300


def compiler_dll():
    out = subprocess.run([str(ROOT / "build_compiler.sh"), "--path"],
                         capture_output=True, text=True, check=True)
    return out.stdout.strip()


def tracked():
    out = subprocess.run(["git", "ls-files", "-z", "--", "*.bjo"],
                         capture_output=True, text=True, check=True)
    return [Path(f) for f in out.stdout.split("\0") if f and Path(f).is_file()]


def sources(paths):
    if not paths:
        return tracked()
    files = []
    for p in paths:
        p = Path(p)
        if p.is_file():
            files.append(p)
        else:
            files.extend(sorted(q for q in p.rglob("*.bjo") if q.is_file()))
    return files


def dumps(cmd, files):
    """Returns the text of each file, by its path, from `cmd` run over chunks
    of the files."""
    texts = {}
    for k in range(0, len(files), CHUNK):
        chunk = [str(f) for f in files[k:k + CHUNK]]
        out = subprocess.run(cmd + chunk, capture_output=True)
        if out.returncode != 0:
            sys.exit(f"{cmd[0]} exited with {out.returncode}:\n"
                     + out.stderr.decode("utf-8", "replace"))
        current = None
        lines = []
        for line in out.stdout.decode("utf-8", "replace").split("\n"):
            if line.startswith("== ") and line[3:] in chunk:
                if current is not None:
                    texts[current] = lines
                current = line[3:]
                lines = []
            else:
                lines.append(line)
        if current is not None:
            texts[current] = lines
    return texts


def main():
    os.chdir(ROOT)
    files = sources(sys.argv[1:])
    dll = compiler_dll()

    tool = ROOT / "tools" / "read" / "read_dump.bjo"
    built = subprocess.run(["dotnet", dll, str(tool)], capture_output=True, text=True)
    if built.returncode != 0:
        sys.exit("tools/read/read_dump.bjo does not compile:\n" + built.stdout + built.stderr)
    exe = tool.with_suffix(".exe")

    expected = dumps(["dotnet", dll, "--dump-read"], files)
    actual = dumps(["dotnet", str(exe)], files)

    failed = 0
    for f in files:
        f = str(f)
        a, b = expected.get(f), actual.get(f)
        if a == b:
            continue
        failed += 1
        print(f"DIFFERS: {f}")
        if a is None or b is None:
            print("  one reader wrote nothing for it")
            continue
        for i, (x, y) in enumerate(zip(a, b)):
            if x != y:
                print(f"  line {i + 1}")
                print(f"    compiler:   {x}")
                print(f"    (std read): {y}")
                break
        else:
            print(f"  the compiler wrote {len(a)} lines, (std read) {len(b)}")

    print(f"{len(files) - failed} of {len(files)} files read the same.")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
