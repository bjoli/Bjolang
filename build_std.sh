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

# Builds the standard library: every module under lib/, in parallel, in the
# order their imports give.
#
# The compiler reads the imports and builds each out-of-date module as soon as
# everything it imports is built (see BuildGraph.fs). A module added under
# lib/ is therefore built without being listed here.
#
# Arguments are passed on to the compiler: `./build_std.sh -j 4`, or
# `./build_std.sh --dry-run` to see what would be built and why.
set -e

cd "$(dirname "${BASH_SOURCE[0]}")"

# The compiler first, when anything it is built from is newer than it. Every
# module counts the compiler as one of its inputs, so a library built by a
# compiler that is about to change is built for nothing.
#
# This is the one thing the graph build cannot do for itself: a compiler cannot
# tell that its own sources have moved on, only that its assembly is newer than
# a module. build_compiler.sh can, and knows where the compiler is.
./build_compiler.sh

echo "Building standard library..."
exec dotnet "$(./build_compiler.sh --path)" --build-graph lib "$@"
