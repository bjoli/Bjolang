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


def release(directory, name, version, depends="", body=None):
    """The package at one version, committed and tagged `vX.Y.Z`.

    Called again on the same directory to add the next version, which is what
    a repository with a history of releases looks like.
    """
    directory = Path(directory)
    fresh = not (directory / ".git").exists()
    make_package(directory, name, version, depends=depends, body=body)
    if fresh:
        make_repo(directory, f"{name} {version}")
    else:
        git(directory, "add", "-A")
        git(directory, "commit", "--quiet", "-m", f"{name} {version}")
    return tag_repo(directory, f"v{version}")


def git_depends(*clauses):
    """A `(depends ...)` clause, one `(package ...)` per dependency.

    Each clause is `(name, url, constraint)`; `url` is a directory, because
    every repository in this suite is one on this disk.
    """
    written = "  (depends\n"
    for name, url, constraint in clauses:
        written += f'    (package (name ({name}))\n'
        if constraint:
            written += f'             (version {constraint})\n'
        if url is not None:
            written += f'             (source (git (url "{url}"))))\n'
        else:
            written = written.rstrip(",\n") + ")\n"
    return written + "    )\n"


def app_with(where, depends, name="app", uses="lib"):
    """A project whose `main` prints `hello` from one of its dependencies.

    Every fixture package exports `hello` under that one name, so a program
    imports exactly one of them — which is enough, since what a test asks is
    *which version* answered.
    """
    where = Path(where)
    write(where / "manifest.bjodat",
          f'(package\n  (name ({name}))\n  (version "0.1.0")\n{depends}  )\n')
    body = '(import (std prelude))\n(defun (main) 0)\n' if uses is None else (
        f'(import (std prelude))\n(import ({uses} core))\n'
        '(defun (main) (println (hello)) 0)\n')
    write(where / "src" / "main.bjo", body)
    return where


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


# --- 3. path dependencies ---------------------------------------------------

@test("path dependency")
def test_path_dependency(work, c):
    lib = make_package(work / "lib", "lib", "0.1.0")
    app = work / "app"
    app.mkdir(parents=True)
    write(app / "manifest.bjodat",
          '(package\n  (name (app))\n  (version "0.1.0")\n'
          '  (depends (package (name (lib)) (source (path (dir "../lib"))))))\n')
    write(app / "src" / "main.bjo",
          '(import (std prelude))\n(import (lib core))\n'
          '(defun (main) (println (hello)) 0)\n')

    ran = run_bjo(app, "run")
    c.worked("an app with a path dependency builds", ran)
    c.says("and the dependency's code ran", ran, "lib 0.1.0")

    roots = (app / ".bjo" / "roots").read_text()
    c.that("the roots file names the dependency's src/",
           str(lib / "src") in roots and "(root (lib)" in roots, roots)

    # 14. The package name is not one of its modules.
    write(app / "src" / "main.bjo",
          '(import (std prelude))\n(import (lib))\n(defun (main) 0)\n')
    refused = run_bjo(app, "run")
    c.failed("importing the package name is refused", refused)
    c.says("and the compiler says a package is not a module", refused,
           "is a package, not a module")

    # An edit to the dependency is an edit to the program that links it.
    write(app / "src" / "main.bjo",
          '(import (std prelude))\n(import (lib core))\n'
          '(defun (main) (println (hello)) 0)\n')
    run_bjo(app, "run")
    write(lib / "src" / "core.bjo",
          '(import (std prelude))\n(export hello)\n'
          '(: hello (-> string))\n(defun (hello) "edited")\n')
    edited = run_bjo(app, "run")
    c.says("editing the dependency rebuilds what links it", edited, "edited")


@test("dependencies refused")
def test_dependencies_refused(work, c):
    make_package(work / "lib", "lib", "0.1.0")
    make_package(work / "other", "other", "0.1.0")

    def app_with(name, depends, package_name="app"):
        where = work / name
        write(where / "manifest.bjodat",
              f'(package\n  (name ({package_name}))\n  (version "0.1.0")\n{depends}  )\n')
        write(where / "src" / "main.bjo",
              '(import (std prelude))\n(defun (main) 0)\n')
        return where

    missing = app_with("no-source", '  (depends (package (name (lib))))\n')
    result = run_bjo(missing, "build")
    c.failed("a dependency with no source anywhere is refused", result)
    c.says("and it says what to write", result, "no manifest says where it comes from")

    itself = app_with("itself", '  (depends (package (name (app))))\n')
    result = run_bjo(itself, "build")
    c.failed("a dependency named like the project is refused", result)
    c.says("and it says so", result, "is this project itself")

    reserved = app_with("reserved",
                        '  (depends (package (name (std)) (source (path (dir "../lib")))))\n')
    result = run_bjo(reserved, "build")
    c.failed("a dependency called (std) is refused", result)
    c.says("and it says the name is the standard library's", result,
           "part of the standard library")

    wrong = app_with("wrong-name",
                     '  (depends (package (name (lib)) (source (path (dir "../other")))))\n')
    result = run_bjo(wrong, "build")
    c.failed("a directory whose manifest has another name is refused", result)
    c.says("and it names both", result, "says the package there is (other)")

    gone = app_with("gone",
                    '  (depends (package (name (lib)) (source (path (dir "../nowhere")))))\n')
    result = run_bjo(gone, "build")
    c.failed("a path source that is not a directory is refused", result)
    c.says("and it says which directory", result, "which is not a directory")

    registry = app_with("registry",
                        '  (depends (package (name (lib)) (source (registry))))\n')
    result = run_bjo(registry, "build")
    c.failed("a registry source is refused", result)
    c.says("and it says there are none", result, "there are none yet")

    # Two manifests giving one name two different sources, and the root
    # manifest settling it.
    middle = work / "middle"
    write(middle / "manifest.bjodat",
          '(package\n  (name (middle))\n  (version "0.1.0")\n'
          '  (depends (package (name (lib)) (source (path (dir "../other")))))\n  )\n')
    write(middle / "src" / "core.bjo",
          '(import (std prelude))\n(export middle-hello)\n'
          '(: middle-hello (-> string))\n(defun (middle-hello) "middle")\n')

    clash = app_with("clash",
                     '  (depends (package (name (middle)) (source (path (dir "../middle"))))\n'
                     '           (package (name (lib)) (source (path (dir "../lib")))))\n')
    # The root manifest names lib, so the root's source wins and middle's is
    # not consulted at all.
    write(clash / "src" / "main.bjo",
          '(import (std prelude))\n(import (lib core))\n(import (middle core))\n'
          '(defun (main) (println (str (hello) " " (middle-hello))) 0)\n')
    result = run_bjo(clash, "run")
    c.worked("the root manifest's source wins over a dependency's", result)
    c.says("and the root's copy is the one that is linked", result, "lib 0.1.0 middle")

    # The same graph without the root naming lib: two manifests, two different
    # directories for one name, and nothing to choose between them. Both
    # directories hold a package called (lib), so what is refused is the
    # disagreement rather than a name that does not match.
    make_package(work / "libcopy", "lib", "0.2.0")
    write(middle / "manifest.bjodat",
          '(package\n  (name (middle))\n  (version "0.1.0")\n'
          '  (depends (package (name (lib)) (source (path (dir "../libcopy")))))\n  )\n')
    indirect = app_with("indirect", "")
    write(indirect / "manifest.bjodat",
          '(package\n  (name (app))\n  (version "0.1.0")\n'
          '  (depends (package (name (middle)) (source (path (dir "../middle"))))\n'
          '           (package (name (second)) (source (path (dir "../second")))))\n  )\n')
    write(work / "second" / "manifest.bjodat",
          '(package\n  (name (second))\n  (version "0.1.0")\n'
          '  (depends (package (name (lib)) (source (path (dir "../lib")))))\n  )\n')
    write(work / "second" / "src" / "core.bjo",
          '(import (std prelude))\n(export second-hello)\n'
          '(: second-hello (-> string))\n(defun (second-hello) "second")\n')
    result = run_bjo(indirect, "build")
    c.failed("two different sources for one name is refused", result)
    c.says("and it lists both manifests", result, "is given two different sources")


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


# --- 4. a git dependency ----------------------------------------------------

@test("git dependency")
def test_git_dependency(work, c):
    origin = work / "origin"
    first = release(origin, "lib", "0.1.0")
    release(origin, "lib", "0.2.0")

    app = app_with(work / "app",
                   git_depends(("lib", origin, '(version-at-least "0.1")')))

    ran = run_bjo(app, "run")
    c.worked("an app with a git dependency builds", ran)
    c.says("the lowest version that satisfies the constraint is used", ran, "lib 0.1.0")
    c.that("and not the newest", "lib 0.2.0" not in said(ran), said(ran))

    lock = (app / "bjo.lock").read_text()
    c.that("the lock names the version", '(version "0.1.0")' in lock, lock)
    c.that("the lock names the commit the tag points at", first in lock, lock)
    c.that("the lock names the source", str(origin) in lock, lock)
    c.says("and the change was printed", ran, "added (lib) 0.1.0")

    c.that("the package was unpacked under .bjo/pkg",
           (app / ".bjo" / "pkg" / "lib@0.1.0" / "src" / "core.bjo").exists())
    c.that("and the clone is bare, under .bjo/git",
           any(p.is_dir() for p in (app / ".bjo" / "git").iterdir()))

    again = run_bjo(app, "build")
    c.worked("a second build", again)
    c.that("says nothing about the lock", "Updated" not in said(again), said(again))

    # 12. Offline. Nothing above asked git for anything it had not got, so a
    # repository that is no longer there changes nothing.
    origin.rename(work / "origin-moved")
    offline = run_bjo(app, "build")
    c.worked("a build with the repository gone still works", offline)
    (work / "origin-moved").rename(origin)

    # A git dependency has to say which versions it takes.
    vague = app_with(work / "vague", git_depends(("lib", origin, None)))
    result = run_bjo(vague, "build")
    c.failed("a git dependency with no version constraint is refused", result)
    c.says("and it says what to write", result, "(version-at-least")
    c.says("and lists the versions that exist", result, "v0.1.0, v0.2.0")

    # A version nothing satisfies.
    too_new = app_with(work / "too-new",
                       git_depends(("lib", origin, '(version-at-least "9.0")')))
    result = run_bjo(too_new, "build")
    c.failed("a lower bound no tag satisfies is refused", result)
    c.says("and lists what there is", result, "v0.1.0, v0.2.0")


# --- 5. the diamond ---------------------------------------------------------

@test("diamond")
def test_diamond(work, c):
    a = work / "a"
    release(a, "a", "1.0.0")
    release(a, "a", "1.1.0")
    release(a, "a", "1.2.0")

    b = work / "b"
    release(b, "b", "1.0.0",
            depends=git_depends(("a", a, '(version-at-least "1.1")')))

    app = app_with(work / "app",
                   git_depends(("a", a, '(version-at-least "1.0")'),
                               ("b", b, '(version-at-least "1.0")')),
                   uses="a")
    ran = run_bjo(app, "run")
    c.worked("a diamond builds", ran)
    c.says("the highest version anybody asked for is used", ran, "a 1.1.0")
    c.that("and not the newest that exists", "a 1.2.0" not in said(ran), said(ran))

    lock = (app / "bjo.lock").read_text()
    c.that("the lock has both packages", "(name (a))" in lock and "(name (b))" in lock, lock)
    c.that("and the lock is sorted by name",
           lock.index("(name (a))") < lock.index("(name (b))"), lock)


# --- 6. an upper bound that the chosen version breaks -----------------------

@test("upper bound")
def test_upper_bound(work, c):
    a = work / "a"
    release(a, "a", "1.0.0")
    release(a, "a", "1.2.0")

    b = work / "b"
    release(b, "b", "1.0.0",
            depends=git_depends(("a", a, '(version-at-least "1.2")')))

    app = app_with(work / "app",
                   git_depends(("a", a, '(version-at-most "1.1")'),
                               ("b", b, '(version-at-least "1.0")')),
                   uses="a")
    result = run_bjo(app, "build")
    c.failed("a violated upper bound is refused", result)
    c.says("and it names the package and the version", result, "(a) 1.2.0")
    c.says("and the manifest whose bound is broken", result, str(app / "manifest.bjodat"))
    c.says("and the manifest that asked for more", result, "v1.0.0")
    # Written "1.1" in the manifest; a version is three numbers, and what is
    # shown is the version rather than the text it was written as.
    c.says("and shows both constraints", result, '(version-at-most "1.1.0")')


# --- 7. a tag that moved ----------------------------------------------------

@test("moved tag")
def test_moved_tag(work, c):
    origin = work / "origin"
    release(origin, "lib", "0.1.0")

    app = app_with(work / "app",
                   git_depends(("lib", origin, '(version-at-least "0.1")')))
    c.worked("the first build", run_bjo(app, "build"))
    artefact = app / "src" / "main.exe"
    artefact.unlink()

    # The same version, different code. This is what the lock exists for.
    write(origin / "src" / "core.bjo",
          '(import (std prelude))\n(export hello)\n'
          '(: hello (-> string))\n(defun (hello) "not what was locked")\n')
    git(origin, "add", "-A")
    git(origin, "commit", "--quiet", "-m", "sneaky")
    git(origin, "tag", "-d", "v0.1.0")
    git(origin, "tag", "-a", "v0.1.0", "-m", "moved")

    # The clone has to learn about the move, which is what a fetch is for.
    clone = next((app / ".bjo" / "git").iterdir())
    subprocess.run(GIT + ["--git-dir", str(clone), "fetch", "--tags", "--force", "--quiet"],
                   capture_output=True, text=True)

    moved = run_bjo(app, "fetch")
    c.failed("a tag that moved is refused", moved)
    c.says("and it says the tag no longer points at the locked commit", moved,
           "no longer points at the commit")
    c.that("and nothing was built", not artefact.exists())


# --- 8. --locked ------------------------------------------------------------

@test("locked")
def test_locked(work, c):
    origin = work / "origin"
    release(origin, "lib", "0.1.0")
    release(origin, "lib", "0.2.0")

    app = app_with(work / "app",
                   git_depends(("lib", origin, '(version-at-least "0.1")')))
    c.worked("the first build", run_bjo(app, "run"))

    unchanged = run_bjo(app, "build", "--locked")
    c.worked("--locked with nothing changed", unchanged)

    # Raising the requirement is a manifest change, which is the only thing
    # that changes what minimal version selection chooses.
    write(app / "manifest.bjodat",
          '(package\n  (name (app))\n  (version "0.1.0")\n' +
          git_depends(("lib", origin, '(version-at-least "0.2")')) + "  )\n")

    refused = run_bjo(app, "build", "--locked")
    c.failed("--locked after a manifest change is refused", refused)
    c.says("and it says what differs", refused, "lib) 0.2.0 (was 0.1.0)")
    c.says("and how to fix it", refused, "without --locked")

    updated = run_bjo(app, "run")
    c.worked("without --locked the lock is updated", updated)
    c.says("and the change is printed", updated, "(lib) 0.2.0 (was 0.1.0)")
    c.says("and the new version is what runs", updated, "lib 0.2.0")
    c.that("and the lock says so", '(version "0.2.0")' in (app / "bjo.lock").read_text())


# --- 9, 10, 11. what a fetched package may not do ---------------------------

@test("fetched packages refused")
def test_fetched_refused(work, c):
    one = work / "one"
    release(one, "dup", "1.0.0")
    two = work / "two"
    release(two, "dup", "2.0.0")

    middle = work / "middle"
    release(middle, "middle", "1.0.0",
            depends=git_depends(("dup", one, '(version-at-least "1.0")')))
    other = work / "other"
    release(other, "other", "1.0.0",
            depends=git_depends(("dup", two, '(version-at-least "1.0")')))

    # 9. Two URLs for one name.
    clash = app_with(work / "clash",
                     git_depends(("middle", middle, '(version-at-least "1.0")'),
                                 ("other", other, '(version-at-least "1.0")')),
                     uses=None)
    result = run_bjo(clash, "build")
    c.failed("one name from two repositories is refused", result)
    c.says("and both sources are named", result, "is given two different sources")

    # ...and the root manifest settling it.
    settled = app_with(work / "settled",
                       git_depends(("middle", middle, '(version-at-least "1.0")'),
                                   ("other", other, '(version-at-least "1.0")'),
                                   ("dup", one, '(version-at-least "1.0")')),
                       uses="dup")
    result = run_bjo(settled, "run")
    c.worked("unless the root manifest gives the name a source", result)
    c.says("and the root's repository is the one used", result, "dup 1.0.0")

    # 10. A repository whose manifest has another name.
    misnamed = work / "misnamed"
    release(misnamed, "actually", "1.0.0")
    wrong = app_with(work / "wrong",
                     git_depends(("expected", misnamed, '(version-at-least "1.0")')),
                     uses=None)
    result = run_bjo(wrong, "build")
    c.failed("a repository whose manifest has another name is refused", result)
    c.says("and it names both", result, "(actually)")

    # 11. A path source inside a fetched package.
    reaching = work / "reaching"
    release(reaching, "reaching", "1.0.0",
            depends='  (depends (package (name (lib)) (source (path (dir "../lib")))))\n')
    pathy = app_with(work / "pathy",
                     git_depends(("reaching", reaching, '(version-at-least "1.0")')),
                     uses=None)
    result = run_bjo(pathy, "build")
    c.failed("a path source inside a fetched package is refused", result)
    c.says("and it says whose disk that is", result, "may not")


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
