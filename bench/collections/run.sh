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
#
# Builds and runs the collection tables: `run.sh` for both, or
# `run.sh list-vs-vec` / `run.sh map-vs-orderedmap` for one.
#
# `DOTNET_TC_CallCountingDelayMs=0`: the runtime waits to optimise a method
# until it has stopped compiling new ones for a while, and every case is a new
# closure. Without this, the first case of a table can be timed before it is
# optimised, at several times what it costs afterwards.
set -e
cd "$(dirname "$0")"
root=../..

"$root/build_compiler.sh"
compiler="$("$root/build_compiler.sh" --path)"

programs=("$@")
[ ${#programs[@]} -eq 0 ] && programs=(list-vs-vec map-vs-orderedmap)

for p in "${programs[@]}"; do
    dotnet "$compiler" "$p.bjo" >/dev/null
    DOTNET_TC_CallCountingDelayMs=0 dotnet "$p.exe"
    echo
done
