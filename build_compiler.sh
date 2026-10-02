#!/bin/bash
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

# The compiler: where it is, and building it.
#
#   ./build_compiler.sh           build it if anything it is made of is newer
#   ./build_compiler.sh --force   build it, and let MSBuild decide what is current
#   ./build_compiler.sh --path    print where it is, and build nothing
#
# bjo, build_std.sh, run_tests.py, run_bjo_tests.py and bench/run.sh all
# call this script, either for the path or to have the compiler built. Moving
# the compiler, or changing how it is built, therefore only needs a change here.
#
# The compiler is built with a ReadyToRun publish rather than `dotnet build`.
# A compiler process is started for every compile and for every graph-build
# worker, and ReadyToRun saves the time a JIT-only compiler spends compiling
# itself at each start. The compiler's output is the same either way.
#
# `dotnet build -c Release` still works and writes bin/Release/net10.0, which
# is what an editor or a debugger uses. No tool runs that build.
set -e

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPILER_DIR="$ROOT/bin/compiler"
COMPILER_DLL="$COMPILER_DIR/Bjolang.dll"

if [ "$1" = "--path" ]; then
    echo "$COMPILER_DLL"
    exit 0
fi

# The portable runtime identifier for this machine, which is what ReadyToRun
# needs: it precompiles for one platform, with a crossgen package that exists
# only for portable identifiers. Worked out from the OS and the CPU rather than
# asked of the SDK, because a distribution's own SDK answers with one of its
# own — Fedora's says fedora.43-x64 — that no crossgen package is published for.
portable_rid() {
    local os arch

    case "$(uname -s)" in
        Linux)
            if ldd --version 2>&1 | grep -qi musl; then os=linux-musl; else os=linux; fi ;;
        Darwin) os=osx ;;
        MINGW* | MSYS* | CYGWIN*) os=win ;;
        *) return 1 ;;
    esac

    case "$(uname -m)" in
        x86_64 | amd64) arch=x64 ;;
        aarch64 | arm64) arch=arm64 ;;
        *) return 1 ;;
    esac

    echo "$os-$arch"
}

# Anything the compiler is built from that is newer than it. The runtime's C#
# counts: the compiler project references it, and every compiled program runs
# against it. `obj` and `bin` are skipped, because a build writes generated C#
# into them and would make the compiler stale the moment it was built.
is_stale() {
    [ -f "$COMPILER_DLL" ] || return 0

    [ -n "$(find "$ROOT" -maxdepth 1 \( -name '*.fs' -o -name '*.fsi' -o -name '*.fsproj' \) \
                 -newer "$COMPILER_DLL" -print -quit)" ] && return 0

    [ -n "$(find "$ROOT/BjolangRuntime" \( -name '*.cs' -o -name '*.csproj' \) \
                 -not -path '*/obj/*' -not -path '*/bin/*' -newer "$COMPILER_DLL" -print -quit)" ] &&
        return 0

    return 1
}

if [ "$1" != "--force" ] && ! is_stale; then
    exit 0
fi

if rid=$(portable_rid); then
    echo "Building the compiler (ReadyToRun, $rid)..."
    dotnet publish "$ROOT/Bjolang.fsproj" -c Release -r "$rid" --self-contained false \
        -p:PublishReadyToRun=true -o "$COMPILER_DIR" -v q --nologo
else
    # Somewhere ReadyToRun has no crossgen for. The same compiler, just slower
    # to start.
    echo "Building the compiler (no ReadyToRun for $(uname -s) $(uname -m))..."
    dotnet publish "$ROOT/Bjolang.fsproj" -c Release -o "$COMPILER_DIR" -v q --nologo
fi

# MSBuild does not rewrite an output that comes out the same, so a source that
# was touched without being changed can leave the compiler older than it, and
# every later call here would publish again. Touched only in that case: the
# compiler's timestamp is an input of every module, so touching it after a
# build that changed nothing would make all of them stale.
if is_stale; then
    touch "$COMPILER_DLL"
fi
