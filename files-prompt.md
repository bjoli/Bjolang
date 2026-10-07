# Task: the file operations a script needs next

The prelude can read, write and append text, test, delete and move a path,
and list one directory level. A script such as `Playground/dirwatch` (sort a
Downloads directory into folders by extension) hits a wall right after that:
it cannot ask how big a file is or when it was written, cannot copy, cannot
walk a tree, cannot read bytes whole, and cannot move over an existing file.

This task adds those, in the prelude, over the `FS` effect, so that
`with-fake-fs` answers for all of it. Two of the eight operations change;
everything else is written over them.

About me: English is not my first language, and I am not trained in compiler
design. When you make a design choice, explain what it rules out, with a
concrete example.

## Read first

- `CLAUDE.md`: comment style ("this does this, because later that"), no
  references to conversations in comments, and prompts such as this file are
  never committed.
- `lib/std/prelude.bjo`, "File operations" (around 1335), "Directories", and
  "The filesystem is an effect" (around 1594–1715): `WriteMode`, `FsKind`,
  `raise-err`, `write-whole-file`, the `/raw` defaults, and `(defeffect FS …)`.
  Also the `#:blocking`/`#:async` extern pairs around 1785–1803: that is how a
  function gets a suspending copy, as `file-write-text` has.
- `lib/std/simpletest.bjo`, the fake filesystem (around 134–463): `FakeEntry`,
  `FakeFs`, the `fake-fs-*` handlers and `with-fake-fs-of`. `fake-fs-written`
  reads a written `MemoryStream` with `.ToArray`, because `.Length` throws
  once the stream is disposed; size has to be read the same way.
- `lib/std/ports.bjo`: the byte ports use `(Array byte)`; use the same type.
- `lib/std/datetime.bjo`: `Instant` is opaque .NET ticks, and
  `unix-milliseconds->instant` is the way in from outside.
- `TestFiles/214_fake_filesystem.bjo`: how a test runs the same checks on a
  fake and on the real disk (`214_probe_dir`).
- `Docs/prelude.org`: "Files", "Directories", "Files, and the `FS` effect",
  and the `with-fake-fs` entry under "Testing".
- `Playground/dirwatch/dirwatch.bjo`: the program this is for.

## Ground rules

- Build: `dotnet build -c Release` after a compiler or runtime change, then
  `./build_std.sh` (log to a file, never pipe it to `head`), then
  `./run_tests.py` and `bjoweb` (in `bjoweb/`: `../bjo/bjo build`,
  `../bjo/bjo run tests/html.bjo`, `../bjo/bjo run tests/demo.bjo`). Green
  before every commit.
- One edit call per file per turn. Several changes to one file: one Python
  script of `str.replace` calls, each asserted to match once.
- To read emitted C#, compile a copy in `/tmp`, never a file under `lib/` in
  place.
- Stage files by name. Before each commit, run `git status` and `git diff
  --cached --stat`, and check that nothing I am editing myself went in. I often
  work in the same tree at the same time. If a file you need has uncommitted
  changes you did not make, stop and ask.
- Commit per phase: the subject is a sentence, the body says what changed and
  why and names the tests, and it ends with the ECA trailer used in the log.
  A `CHANGELOG.org` entry per phase. Push only when I ask.

## Decided

1. **`fs-info` replaces `fs-kind`; it does not sit beside it.**
   `(fs-info path)` answers `(Option FsInfo)`:

   ```scheme
   (type (: FsInfo (Record (: kind FsKind)
                           (: size long)          ; bytes; 0 for a directory
                           (: modified-ms long)   ; Unix milliseconds, UTC
                           (: link? bool))))      ; the path itself is a symbolic link
   ```

   One operation, because two could disagree: a sandbox handler that hides
   `secret.txt` from `fs-kind` but forgets `fs-info` would still give away its
   size. `file-exists?` and `directory-exists?` are rewritten over it, and
   `(file-info path)` is the public name for the operation's answer.

2. **Time is Unix milliseconds, not an `Instant`.** The prelude cannot import
   `(std datetime)`, which imports the prelude. A program that wants an
   `Instant` writes `(unix-milliseconds->instant (record-ref info
   modified-ms))`; the docs show it. Milliseconds, not ticks, because that is
   the way into an `Instant` that already exists. What it gives up: two writes
   in the same millisecond look equally old, so a build tool that rebuilds
   when the input is "not older" rebuilds once too often, never too rarely.

3. **The fake has its own clock.** It starts at 2000-01-01T00:00:00Z, and
   every change it makes (a write opened, a directory created, a move) stamps
   the entry and then moves the clock one second on. A seeded entry has the
   start time. `(fake-fs-set-modified! fs path ms)` sets one by hand. A test
   is then deterministic, and "written later" always means "newer"; with the
   real clock, two writes in one test could get the same millisecond.
   Size is the UTF-8 byte count of the text, or the written stream's length
   read through `.ToArray`.

4. **`fs-move` takes a mode.** `(fs-move from to mode)` with
   `(type (: MoveMode (Union MoveNew MoveReplace)))`: `MoveNew` fails if `to`
   exists, as today; `MoveReplace` replaces a *file* at `to` in one rename
   (`File.Move(from, to, true)`), which is what an atomic write needs later. A
   directory is never replaced: `MoveReplace` onto an existing directory is an
   `IOException`. `(file-move from to #:mode MoveNew)` is the public form, and
   the default keeps every existing call's meaning. A union rather than a
   `bool`: `(fs-move a b #t)` says nothing at the call.

5. **`file-copy` is a file copy, and refuses to overwrite by default.**
   `(file-copy from to #:mode CreateNew)` takes the `WriteMode` the target is
   opened with: `CreateNew` (the default) fails if `to` exists, `Truncate`
   replaces it, `Append` adds to it. The default differs from
   `open-output-file`'s on purpose: `dirwatch` copying `report.pdf` into a
   `Documents` that already has one should fail, not lose the old one. It is
   the default `File.Copy` has too. Built from `open-read-stream` and
   `open-write-stream`, with no new operation, so a fake copies. A directory
   as `from` is an error. Copying a file onto itself is an error, checked on
   `path-absolute` of both, because `Truncate` would empty the file before
   reading it. The copy is a new file: its modified time is the copy's, not
   the original's. It has a suspending copy, as `file-write-text` does.

6. **Bytes whole, as the text functions are.** `(file-read-bytes path)` →
   `(Array byte)`, `(file-write-bytes path bytes)`,
   `(file-append-bytes path bytes)`. Same errors, same suspending copies.

7. **Lines whole.** `(file-read-lines path)` → `(Vec string)` and
   `(file-write-lines path lines)` with `lines` a `(Vec string)`. Writing puts
   `"\n"` after every line, the last included, on every OS. Reading accepts
   `"\n"` and `"\r\n"`, a last line with no newline, and gives no extra empty
   line for a final newline. So `(file-read-lines p)` after
   `(file-write-lines p v)` is `v` whenever no element contains a newline. A
   test pins that. `"\n"` everywhere, because a file written on Windows and on
   Linux should then be the same bytes and diff clean.

8. **`directory-walk` is lazy, sorted, and does not follow links.**
   `(directory-walk dir #:into? (fun (d) #t))` → `(Seq string)`: the files
   under `dir`, depth first. Each directory's entries are sorted ordinally, so
   the order is the same on every OS and on the fake. Without that, a tool
   that hashes the files in walk order gets a different hash on another
   machine. `#:into?` is asked about each directory before the walk goes into
   it: `(fun (d) (not (= (path-filename d) (Some ".git"))))` skips `.git`.
   A directory that is a symbolic link (`link?`) is not entered, so a link
   pointing back up cannot loop the walk forever. Lazy, so `(find … (directory-walk dir))`
   stops at the first match. It is opened when walked, so the handler installed at the walk
   answers, as for `file-read/seq`. An error on the way raises, as
   `directory-files` does. No glob patterns:
   `(filter #(string-ends-with? & ".bjo") (directory-walk "src"))` asks the
   same thing in the language.

9. **What `directory-walk` and the lazy readers do not do** goes in the docs
   in one sentence: pulling from any of them never suspends, so a bjoroutine
   that walks a tree or reads lines lazily parks its thread on each pull. That
   covers `file-read/seq`, `file-read-lines/seq`, `(std ports)`' `port->seq`
   and `file->seq`, and `directory-walk`. For a bjoroutine, use
   `file-read-lines` or `port->chan`.

Stop and ask if any of these does not work, in particular if a union case
name clashes with something already exported, if `File.Move(…, true)`
cannot be reached through `import/extern`, or if the fake needs a larger
change than a time beside each entry.

## Phases

1. **`fs-info`.** `FsInfo`, the operation replacing `fs-kind` in
   `(defeffect FS …)` and its `#:default`/`#:cold` lists, `fs-info/raw`
   (`FileInfo` for a file, `Directory.GetLastWriteTimeUtc` for a directory,
   `LinkTarget` not null for `link?`), `file-info`, `file-exists?` and
   `directory-exists?` over it. `entries-of-kind` and `directory-delete-tree`
   move to it too. The fake: a time beside each entry, the clock,
   `fake-fs-info` in place of `fake-fs-kind`, `fake-fs-set-modified!`, and
   `with-fake-fs-of` installing it. Update `214_fake_filesystem.bjo` where it
   names `fs-kind`. Commit.
2. **`fs-move` with a mode.** `MoveMode`, the operation's third argument,
   `fs-move/raw`, the fake, `file-move #:mode`. Commit.
3. **Copy, bytes and lines.** `file-copy`, `file-read-bytes`,
   `file-write-bytes`, `file-append-bytes`, `file-read-lines`,
   `file-write-lines`, each with a suspending copy where it reads or writes a
   stream. Commit.
4. **`directory-walk`.** Commit.
5. **Documentation.** `Docs/prelude.org`: "Files" and "Directories" for the
   new functions; the `FS` table (`fs-info`, `fs-move`'s mode); the
   `with-fake-fs` entry (the clock, `fake-fs-set-modified!`); the sentence
   from "Decided" 9. Commit.

The `CHANGELOG.org` entry for phases 1 and 2 says the change is breaking: a
handler for `fs-kind` is now one for `fs-info`, and one for `fs-move` takes
the mode.

## Tests

In a new `TestFiles/249_files.bjo`. Every check runs twice where it can,
under `with-fake-fs` and on the real disk in a probe directory the test
creates and deletes, as `214` does:

- `file-info`: kind, size of a text file in bytes (`"å"` is 2), a directory's
  size 0, `None` for a missing path; on the fake, a later write is newer, a
  seed has the start time, and `fake-fs-set-modified!` is what `file-info`
  answers.
- `file-move`: `MoveNew` onto an existing file fails and changes nothing;
  `MoveReplace` replaces it; `MoveReplace` onto a directory fails.
- `file-copy`: the default refuses an existing target and leaves it as it
  was; `Truncate` replaces; `Append` concatenates; a missing source is the
  source's error; copying onto itself fails and the file is intact; a
  directory as the source fails; bytes that are not UTF-8 survive a copy.
- Bytes: a round trip of all 256 byte values; append.
- Lines: the round trip; a file ending without a newline; `"\r\n"`; an empty
  file is an empty `Vec`; the written bytes end with `"\n"`.
- `directory-walk`: the files of a small tree in sorted order; `#:into?`
  pruning a directory; stopping early after the first match; an empty
  directory; the handler installed at the walk answering. On the real disk
  only, a symbolic link to a parent directory is not followed; skip this check
  if the OS refuses to create the link.

Then write `/tmp/dirwatch-check/dirwatch.bjo`: a version of
`Playground/dirwatch` using the new functions against a fake Downloads
directory. It sorts files by extension (`.tar.gz` included) with
`file-move #:mode MoveNew`, skips files modified in the last five seconds, and
walks with `directory-walk`. Do not commit it and do not touch the
`Playground` copy. Report anything that was awkward to write: that is what
the next task is made of.

## Out of scope

- Atomic writes, temporary files and directories tied to a scope, and
  `Instant` versions of the times. These belong in a later `(std files)`
  module, which can import `(std datetime)`.
- Watching a directory for changes (an event for `choose`).
- Permissions, locks, encodings other than UTF-8, and globs.
- Preserving the modified time on a copy.

## Final report

For each phase: what was built, the commit, the tests added, anything decided
that this file did not decide, with the example that decided it, and what is
left open. Then the `dirwatch` check: what was easy and what was not.
