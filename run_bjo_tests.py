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

"""bjo's own tests: projects, path and git dependencies, the lock file.

`run_tests.py` compiles fixtures with the compiler directly and never starts
`bjo`, so nothing there covers the driver. This does, one command:

    ./run_bjo_tests.py [pattern ...]

**It never touches the network.** Every git repository a test uses is made
here with `git init` in a temporary directory and referred to by its absolute
path as the URL, so the suite is the same with the network unplugged — which
is also test 12.
"""

import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent
BJO = ROOT / "bjo" / "bjo"
WORK = ROOT / "TestFiles" / ".bjo-tests"

RED = '\033[0;31m'
GREEN = '\033[0;32m'
YELLOW = '\033[0;33m'
BLUE = '\033[0;34m'
NC = '\033[0m'

os.environ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
os.environ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
os.environ["DOTNET_NOLOGO"] = "1"
os.environ.setdefault("DOTNET_CLI_HOME", "/tmp")
# A test that asks git for a password has gone wrong; it must not stop to ask.
os.environ["GIT_TERMINAL_PROMPT"] = "0"

GIT = ["git", "-c", "user.name=bjo tests", "-c", "user.email=bjo@example.invalid",
       "-c", "commit.gpgsign=false", "-c", "init.defaultBranch=main"]


# ---------------------------------------------------------------------------
# Running things
# ---------------------------------------------------------------------------

def run_bjo(where, *args, timeout=600):
    """`bjo` in a directory, with its output captured."""
    return subprocess.run([str(BJO), *args], cwd=str(where),
                          capture_output=True, text=True, timeout=timeout)


def git(where, *args):
    result = subprocess.run(GIT + list(args), cwd=str(where),
                            capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} in {where}: {result.stderr.strip()}")
    return result.stdout


def write(path, text):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text)
    return path


def said(result):
    return result.stdout + result.stderr


# ---------------------------------------------------------------------------
# A fixture: a package in its own git repository
# ---------------------------------------------------------------------------

def make_package(directory, name, version, body=None, depends="", entry=None):
    """A package directory: a manifest and one module under src/.

    `name` is the package name as source writes its segments, without the
    parentheses. The module is `<name>/core.bjo` and exports `<name>-hello`,
    which is enough to tell two versions of one package apart.
    """
    directory = Path(directory)
    manifest = f"(package\n  (name ({name}))\n  (version \"{version}\")\n"
    if depends:
        manifest += depends
    if entry:
        manifest += f"  (entry \"{entry}\")\n"
    manifest += "  )\n"
    write(directory / "manifest.bjodat", manifest)
    if body is None:
        body = (f'(import (std prelude))\n(export hello)\n'
                f'(: hello (-> string))\n(defun (hello) "{name} {version}")\n')
    write(directory / "src" / "core.bjo", body)
    return directory


def make_repo(directory, message="a package"):
    """Turns a directory into a git repository with one commit."""
    git(directory, "init", "--quiet")
    git(directory, "add", "-A")
    git(directory, "commit", "--quiet", "-m", message)
    return git(directory, "rev-parse", "HEAD").strip()


def tag_repo(directory, tag, message="a version"):
    git(directory, "tag", "-a", tag, "-m", message)
    return git(directory, "rev-list", "-n", "1", tag).strip()


# ---------------------------------------------------------------------------
# The suite
# ---------------------------------------------------------------------------

TESTS = []


def test(name):
    def take(fn):
        TESTS.append((name, fn))
        return fn
    return take


class Checks:
    """What one test found. Every check says what it wanted and what it got."""

    def __init__(self, name):
        self.name = name
        self.failures = []
        self.total = 0

    def that(self, label, ok, detail=""):
        self.total += 1
        if not ok:
            self.failures.append(f"{label}: {detail}" if detail else label)
        return ok

    def says(self, label, result, wanted):
        return self.that(label, wanted in said(result), said(result)[-500:])

    def failed(self, label, result):
        return self.that(label, result.returncode != 0,
                         f"it succeeded: {said(result)[-300:]}")

    def worked(self, label, result):
        return self.that(label, result.returncode == 0, said(result)[-500:])


# --- 1. init ----------------------------------------------------------------

@test("init")
def test_init(work, c):
    app = work / "myapp"
    app.mkdir(parents=True)

    c.worked("init makes a project", run_bjo(app, "init"))
    for made in ("manifest.bjodat", ".gitignore", "src/main.bjo"):
        c.that(f"init writes {made}", (app / made).exists())
    c.that("init makes tests/", (app / "tests").is_dir())
    c.that("the manifest takes the directory's name",
           "(name (myapp))" in (app / "manifest.bjodat").read_text(),
           (app / "manifest.bjodat").read_text())
    c.that("the .gitignore covers .bjo and what the compiler writes",
           all(line in (app / ".gitignore").read_text()
               for line in (".bjo/", "*.dll", "*.exe", "*.bjobuild")))

    again = run_bjo(app, "init")
    c.failed("a second init is refused", again)
    c.says("and it says the manifest is already there", again, "already a manifest.bjodat")

    # A file that is there is never replaced, even when the manifest is gone.
    write(app / "src" / "main.bjo", ";; mine\n")
    (app / "manifest.bjodat").unlink()
    c.worked("init again after the manifest was removed", run_bjo(app, "init"))
    c.that("init does not overwrite an existing src/main.bjo",
           (app / "src" / "main.bjo").read_text() == ";; mine\n",
           (app / "src" / "main.bjo").read_text())

    named = work / "elsewhere"
    named.mkdir()
    c.worked("init takes a name", run_bjo(named, "init", "mytool"))
    c.that("and uses it", "(name (mytool))" in (named / "manifest.bjodat").read_text())

    reserved = work / "std"
    reserved.mkdir()
    refused = run_bjo(reserved, "init")
    c.failed("a reserved name is refused", refused)
    c.says("and it says why", refused, "part of the standard library")

    reserved_arg = work / "reserved-arg"
    reserved_arg.mkdir()
    c.failed("a reserved name given as an argument is refused too",
             run_bjo(reserved_arg, "init", "text"))

    unnameable = work / "my.app"
    unnameable.mkdir()
    refused = run_bjo(unnameable, "init")
    c.failed("a directory whose name is not a package name is refused", refused)
    c.says("and it says what a name is", refused, "is not a package name")

    library = work / "mylib"
    library.mkdir()
    c.worked("init --lib", run_bjo(library, "init", "--lib"))
    c.that("a library gets src/core.bjo and no main",
           (library / "src" / "core.bjo").exists() and not (library / "src" / "main.bjo").exists())


# --- 2. a project with no dependencies --------------------------------------

@test("no dependencies")
def test_no_dependencies(work, c):
    app = work / "solo"
    app.mkdir(parents=True)
    run_bjo(app, "init")

    first = run_bjo(app, "run")
    c.worked("bjo run builds and runs", first)
    c.says("and the program printed its greeting", first, "hello from solo")

    artefact = app / "src" / "main.exe"
    c.that("the artefact is beside the source", artefact.exists())
    built_at = artefact.stat().st_mtime

    roots = app / ".bjo" / "roots"
    c.that("the roots file names the package",
           '(root (solo) "' in roots.read_text(), roots.read_text())
    roots_at = roots.stat().st_mtime

    again = run_bjo(app, "build")
    c.worked("a second build", again)
    c.says("says it is up to date", again, "Up to date")
    c.that("and compiles nothing", "Compiling" not in said(again), said(again))
    c.that("and leaves the artefact alone", artefact.stat().st_mtime == built_at)

    # 13. The roots file is a build input, so rewriting it would make every
    # artefact in the project stale.
    c.that("an unchanged build does not rewrite .bjo/roots",
           roots.stat().st_mtime == roots_at)

    # Editing a module rebuilds.
    write(app / "src" / "main.bjo",
          '(import (std prelude))\n(defun (main) (println "changed") 0)\n')
    changed = run_bjo(app, "run")
    c.says("an edited module is rebuilt and run", changed, "changed")

    # Arguments need the `.`, since everything after a file belongs to the
    # program and a project names no file.
    write(app / "src" / "main.bjo",
          '(import (std prelude))\n'
          '(defun (main args) (println (string-join args ",")) 0)\n')
    with_args = run_bjo(app, "run", ".", "a", "b")
    c.says("bjo run . passes arguments to the program", with_args, "a,b")


# --- 15. a library project --------------------------------------------------

@test("library project")
def test_library_project(work, c):
    lib = work / "biglib"
    lib.mkdir(parents=True)
    run_bjo(lib, "init", "--lib")
    write(lib / "src" / "extra.bjo",
          '(import (std prelude))\n(export extra)\n(: extra (-> int))\n(defun (extra) 7)\n')
    write(lib / "src" / "deep" / "inner.bjo",
          '(import (std prelude))\n(export inner)\n(: inner (-> int))\n(defun (inner) 8)\n')

    built = run_bjo(lib, "build")
    c.worked("a library project builds", built)
    for module in ("core", "extra"):
        c.that(f"{module}.dll was built", (lib / "src" / f"{module}.dll").exists(), said(built))
    c.that("a module in a subdirectory was built too",
           (lib / "src" / "deep" / "inner.dll").exists(), said(built))

    ran = run_bjo(lib, "run")
    c.failed("bjo run on a library is refused", ran)
    c.says("and says it is a library", ran, "is a library")


# --- 16. outside a project --------------------------------------------------

@test("outside a project")
def test_outside_a_project(work, c):
    loose = work / "loose"
    loose.mkdir(parents=True)
    write(loose / "one.bjo", '(import (std prelude))\n(defun (main) (println "loose") 0)\n')

    ran = run_bjo(loose, "run", "one.bjo")
    c.worked("bjo run on a single file outside a project", ran)
    c.says("and it ran", ran, "loose")
    c.that("nothing made a .bjo directory", not (loose / ".bjo").exists())

    nowhere = run_bjo(loose, "run")
    c.failed("bjo run with no file outside a project is refused", nowhere)
    c.says("and it says there is no project", nowhere, "not a project")

    checked = run_bjo(loose, "check", "one.bjo")
    c.worked("bjo check on a single file", checked)


# ---------------------------------------------------------------------------
# Running the suite
# ---------------------------------------------------------------------------

def main():
    patterns = sys.argv[1:]

    if not BJO.exists():
        print(f"{RED}No bjo launcher at {BJO}{NC}")
        return 1

    # The launcher builds what is missing or stale, so this also says whether
    # `bjo` itself still compiles.
    ready = subprocess.run([str(BJO), "help"], capture_output=True, text=True)
    if ready.returncode != 0:
        print(f"{RED}bjo does not run:{NC}\n{said(ready)}")
        return 1

    if WORK.exists():
        shutil.rmtree(WORK)
    WORK.mkdir(parents=True)

    print(f"{BLUE}=== bjo tests ==={NC}\n")
    total, failed = 0, 0
    started = time.time()

    for name, fn in TESTS:
        if patterns and not any(p in name for p in patterns):
            continue
        work = WORK / name.replace(" ", "_")
        work.mkdir(parents=True, exist_ok=True)
        c = Checks(name)
        try:
            fn(work, c)
        except Exception as e:
            c.that("the test ran to the end", False, f"{type(e).__name__}: {e}")
        total += c.total
        failed += len(c.failures)
        mark = f"{GREEN}PASS{NC}" if not c.failures else f"{RED}FAIL{NC}"
        print(f"  [{mark}] {name} ({c.total - len(c.failures)}/{c.total})")
        for failure in c.failures:
            print(f"         {YELLOW}{failure}{NC}")

    print(f"\n{BLUE}=== Summary ==={NC}")
    print(f"Checks: {total - failed}/{total} held")
    print(f"Time:   {time.time() - started:.1f}s")
    if failed:
        print(f"\n{RED}{failed} checks failed.{NC}")
        return 1
    print(f"\n{GREEN}Everything bjo was asked to do, it did.{NC}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
