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
# Both suites. Each reduces its own five reps to a minimum with the median
# beside it, so the two tables are reduced the same way and this script only
# runs them.
#
# `DOTNET_gcServer` is set for the Bjolang program because `Cml.Bench.csproj`
# sets `ServerGarbageCollection`, and the Bjolang compiler emits a
# runtimeconfig.json without it. Without this line the two suites run under
# different collectors, which the header of each table now shows.
set -e
cd "$(dirname "$0")/.."

echo "=== raw CML (C#) ==="
dotnet build bench/Cml/Cml.Bench.csproj -c Release >/dev/null
dotnet bench/Cml/bin/Release/net10.0/CmlBench.dll

echo
echo "=== Bjolang ==="
dotnet bin/Release/net10.0/Bjolang.dll bench/bjolang/cmlbench.bjo >/dev/null
DOTNET_gcServer=1 DOTNET_gcConcurrent=1 dotnet bench/bjolang/cmlbench.exe
