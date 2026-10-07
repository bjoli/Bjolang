# Task: read text with `(std datetime)`'s own formats, and add Unix time

`(std datetime)` writes text with formats of your own: a format is a quoted
list of field names, strings and characters, elaborated into one union per
kind of value (`DatePart`, `TimePart`, `DateTimePart`, `MomentPart`), and
`date->text`, `time->text`, `datetime->text` and `moment->text` write with it.
Nothing reads with those formats yet. This task adds the readers, and Unix time
as a field and as an accessor.

About me: English is not my first language, and I am not trained in compiler
design. When you make a design choice, explain what it rules out, with a
concrete example.

## Read first

- `CLAUDE.md`: comment style ("this does this, because later that"), no
  references to conversations in comments, and prompts such as this file are
  never committed.
- `Docs/std/datetime.org`, especially "Text", "Text of your own", "Range and
  errors" and "Why it is built this way".
- `lib/std/datetime.bjo`, sections "Reading ISO 8601", "HTTP dates" and "Text
  of your own". The helpers to reuse are already there: `read-fixed`,
  `read-number`, `read-fraction`, `read-optional-fraction`, `read-offset`,
  `read-until`, `skip-string`, `skip-chars`, `starts-with-char?`, `whole`,
  `read-month`, and on the writing side `add-padded!`, `part-width`, `name-at`,
  `argument-error`, `roman-steps` and `add-roman!`.
- The top entries of `CHANGELOG.org`, for the style of an entry.
- `Docs/Syntax.org`, "Quoted lists, and the constructors they elaborate into":
  `#:tag`, `#:rest`, and delegation through an untagged case.

## Ground rules

- Build and test: `dotnet build -c Release` after a compiler change (none is
  expected here), `./build_std.sh` after any library change, `./bjor file.bjo`
  to run one file, `./run_tests.py` for the suite (about 40 s). Green after
  every phase, before the next.
- One edit call per file per turn. Parallel edits to one file have raced and
  cut a file short before.
- Everything a reader answers for bad *text* is `None`, never an exception. A
  *format* that cannot be read (see "Formats that cannot be read") is a
  programming error and raises an `ArgumentException` naming the problem, as
  `(year 4 2)` already does when written.
- A body an importing module copies (a constrained generic such as `->unix`, or
  a trait method) may only call exported names and `import/extern` aliases.
  Anything else is exported automatically under its own name. `./build_std.sh`
  prints the line `Auto-exporting 7 name(s) ... date+date-period, ...` for this
  module; after your change it must list the same seven and no more. Inline
  the arithmetic with literal constants rather than calling a helper.
- A module-level value cannot be generic (it compiles to a static field), and
  a format kept in a variable needs its type written:
  `(def (: stamp (List DateTimePart)) '(...))`.
- No new string syntax. Formats stay quoted lists.
- Readers walk the string with `StringCursor`s and answer through `Option` and
  `Tuple`, which are structs, as the ISO readers do: nothing is allocated but a
  zone id. Keep the fields read so far in a `Struct`, not a `Record`.
- The existing `iso8601->...` and `http-date->instant` readers stay as they
  are. They accept things one format cannot say, such as optional seconds and a
  comma before a fraction, and they are the fast path.
- Commit after each phase: the subject is a sentence, the body says what
  changed and why and names the tests, and it ends with the ECA trailer used in
  the log. Add a `CHANGELOG.org` entry per phase. Do not commit `Readme.org`,
  `Todo.org`, `lib/std/fmt.bjo` or `lib/std/rx.bjo`, which hold my own
  uncommitted edits, nor any `*.md` prompt. Push only when I ask.

## Decided

1. **Readers mirror the writers:** `(text->date s format)`,
   `(text->time s format)`, `(text->datetime s format)`,
   `(text->moment s format)` and `(text->instant s format)`, each answering an
   `Option`, each taking `#:names` like the writers (defaulting to
   `current-date-names`). The text comes first and the format second, as the
   value comes first in `date->text`.
2. **Unix time as a field.** `unix` (seconds) and `unix-ms` (milliseconds) are
   tags with no width. They are written and read by `MomentPart` and by the new
   `InstantPart` (item 4). Both round toward the past when written, as
   `instant->unix-seconds` does: half a second before 1970 is `-1`.
3. **`(->unix x)`**, for anything `HasInstant` (`Instant` and `Moment`): the
   Unix time in seconds, as a `long`, rounded toward the past. It is added
   beside the existing functions, which keep their names:
   `instant->unix-seconds`, `unix-seconds->instant`,
   `instant->unix-milliseconds` and `unix-milliseconds->instant` are not
   renamed or removed.
4. **Instants get a format and a writer of their own.** `InstantPart` holds the
   tags of `DateTimePart` (through delegation, as `MomentPart` does), `offset`,
   `unix` and `unix-ms`. `(instant->text i format)` writes the date and time
   fields in UTC, and `offset` writes `Z` for an instant, so
   `'((year 4) "-" (month 2) "-" (day 2) "T" (hours 2) ":" (minutes 2) ":" (seconds 2) offset)`
   writes what `->str` writes for a whole second. `text->instant` reads the
   fields as UTC unless the format has `offset`, in which case they are the
   local time at that offset. That is the inverse of the writer, not a guess at
   a zone.
5. **`text->moment` needs a `zone` field.** With an `offset` field as well, the
   offset has to be one the zone has at that local time, as `iso8601->moment`
   demands. Without one, a local time in a gap or an overlap is `None`;
   `(text->moment/resolver s format resolver)` resolves it instead, the
   module's `/resolver` convention. A text with an offset and no zone
   (RFC 3339) is read with `text->instant` and put in a zone with
   `instant->moment`: two steps, and no zone is ever guessed.
6. **Names match exactly, case included.** `month-name`, `month-abbr`,
   `weekday-name`, `weekday-abbr` and `am-pm` read only the table's own
   spelling: `oktober`, not `Oktober`; `PM`, not `pm`.

Stop and ask if any of these turns out not to work.

## What a reader does

A reader walks the format's parts in order over the text, and the whole text
has to be used, as in `whole`.

- **Text and characters** match exactly, case included.
- **A number field** reads ASCII digits only. Without a width it reads from 1
  up to the field's most digits; with `(field w)` it reads at least `w` and at
  most `w` or the field's most, whichever is more. It reads greedily and never
  backs up. The most digits: `year` and `week-year` 4, `month`, `day`, `week`,
  `hours`, `hours12`, `minutes` and `seconds` 2, `yday` 3, `weekday` 1.
  Whatever a writer wrote with a format, the reader reads back with the same
  format (the writer writes at least `w` digits and never more than the
  field's most, or `w`).
- **`(fraction n)`** reads exactly `n` digits; digits past the seventh are read
  and dropped, as a tick holds no less than 100 ns. A bare `fraction` reads
  nothing, or a point or comma and at least one digit, as ISO 8601 does.
- **`unix`** reads an optional `-` and up to 12 digits, **`unix-ms`** up to 15.
  A value outside the years 1 to 9999 is `None`, as `unix-seconds->instant`
  and `unix-milliseconds->instant` answer.
- **`offset`** reads `Z`, or `±hh:mm` with an optional `:ss`: `read-offset`.
- **`zone`** reads the longest run of the characters an IANA id is made of
  (letters, digits, `/`, `_`, `-`, `+`) and looks it up with `timezone`: an
  unknown id is `None`.
- **Names** (`month-name`, `month-abbr`, `weekday-name`, `weekday-abbr`,
  `am-pm`) match an entry of the names table exactly, the longest entry that
  matches when two could, without allocating (`skip-string`).
- **`(roman year)`, `(roman month)`, `(roman day)`** read a run of `MDCLXVI`,
  and only the form the writer writes: `MCM`, not `MDCCCC`; `IV`, not `IIII`;
  `MMMM` for 4000. Check it by comparing the text with the value written back,
  character by character.

Then the fields read are put together, and anything read twice has to agree.

- **A date** is year, month and day; or year and `yday` (an ordinal date,
  2026-298); or week-year, week and weekday (a week date). A field a format
  leaves out is its first value: no `day` is the 1st, no `weekday` is Monday.
  So `'((year 4) "-" (month 2))` reads HTML's month input, `2026-10`, as
  2026-10-01, and `'((week-year 4) "-W" (week 2))` reads its week input,
  `2026-W43`, as that week's Monday. A field read beside the ones that made the
  date is checked against it: `weekday-name` must be the date's own, as
  `http-date->instant` checks, and so must `yday`, `week` or a Roman field. A
  Roman year and a numeric year in one format must agree.
- **A time** needs `hours`, or `hours12` with `am-pm`; minutes, seconds and the
  fraction left out are 0. `am-pm` beside `hours` must agree with it.
- **A datetime** is a date and a time; left out, the time is midnight.
- **An instant or a moment from `unix`/`unix-ms`**: see "Formats that cannot be
  read".
- Every value is checked as the constructors check it: month 1 to 12, a day
  its month has, hours 0 to 23, `hours12` 1 to 12, minutes and seconds 0 to 59
  (no leap second), a week its week-year has, and the range of years 1 to
  9999. A value that fails is `None`.

## Formats that cannot be read

Each raises an `ArgumentException` when used to read, with a message that says
which part and why:

- **No largest unit.** A date's format needs `year` (or `(roman year)`) or
  `week-year`; a time's needs `hours`, or `hours12` with `am-pm`. `'(month "/"
  day)` cannot make a date.
- **`hours12` without `am-pm`**: 1:05 could be either.
- **Two readings of one text.** A part that can take more than one length
  (a number without a width, or with a width below the field's most digits;
  `unix`; a Roman numeral; `zone`) followed by a part that can begin with a
  character it could also have taken. `'(year month day)` reads `2026110`
  either as January 10 or as November 0, so it is refused, and
  `'((year 4) (month 2) (day 2))` is not. Text starting with a digit after a
  number, or a Roman letter after a numeral, is the same case.
- **`unix` or `unix-ms` beside another field of the instant**, except `zone`
  in a moment's format: the number alone decides the instant, and a second
  source could disagree with it. `'(unix " " zone)` reads a moment;
  `'(unix " " (year 4))` is refused. Text around it is fine: `'("@" unix)`.

Writing has none of these limits. A format that cannot be read can still be
written with.

## Phases

1. **Unix time and instants.** Add `->unix` (a constrained generic: inline the
   arithmetic, with `621355968000000000` and `10000000` written out, or call
   the exported `instant->unix-seconds`), leaving the four existing Unix
   functions as they are. Add `unix` and `unix-ms` to `MomentPart`, and add
   `InstantPart` and `instant->text`.
   Tests: `->unix` at the epoch, half a second before it (`-1`), 0001-01-01
   (`-62135596800`) and 9999-12-31T23:59:59Z (`253402300799`), on an instant
   and on a moment; the fields written, negative ones included; an instant's
   `offset` writing `Z`; `instant->text` matching `->str` for a whole second.
2. **The reader for dates.** `text->date`, with the checks above for a date's
   parts and for formats that cannot be read. Tests: every field, widths and
   none, names in English and Swedish, Roman years (and a non-canonical
   numeral refused), ordinal and week dates, HTML's month and week inputs, a
   wrong weekday name, and each refused format.
3. **Times and datetimes.** `text->time` and `text->datetime`. Tests: the
   twelve-hour clock (12 AM is 00, 12 PM is 12), both kinds of fraction, and
   left-out fields.
4. **Instants and moments.** `text->instant`, `text->moment` and
   `text->moment/resolver`, with `unix`, `unix-ms`, `offset` and `zone`.
   Tests: RFC 3339 with `Z` and with an offset; both offsets of Stockholm's
   2026-10-25 02:30; that hour without an offset (`None`, then resolved); a
   zone the machine does not know (`None`); Unix time at both ends of the
   range and one past each end (`None`); `'(unix " " zone)`.
5. **Round trips and hostile text.** In `245_datetime.bjo`, a loop over a few
   thousand values of every type written and read back with a set of readable
   formats, which must give back the value (cut to the format's precision
   where the format drops some). Outside the repo, the way
   `/tmp/dtbench/diff/diff.bjo` was built for the text rewrite: random
   mutations of written texts fed to every reader, which must answer `Some` or
   `None` and never raise. Report the counts.

`/tmp/dtbench/` held the benchmark and the mutation program when this was
written. If it is gone, write them again: a loop per operation timed with
`System.Diagnostics.Stopwatch` and `GC.GetAllocatedBytesForCurrentThread`,
and a seeded 64-bit LCG driving the values and the mutations (replace, insert,
delete, swap, truncate, with letters, digits, signs and one character outside
the BMP).

After each phase: `./build_std.sh`, the datetime tests, `./run_tests.py`, and
`bjoweb` (in `bjoweb/`: `../bjo/bjo build`, then `../bjo/bjo run
tests/html.bjo` and `../bjo/bjo run tests/demo.bjo`).

## Documentation

- `Docs/std/datetime.org`: a subsection "Reading" under "Text of your own",
  with the rules above in plain words, an example for each kind of value, HTML's
  month and week inputs, the list of formats that cannot be read and why, and
  the round-trip promise. Add `->unix` to "Instants", and
  `InstantPart` to the table of formats.
- "Why it is built this way": why a format that could be read two ways is
  refused rather than read greedily, with `'(year month day)` and `2026110`;
  why fields left out are their first value and not refused; and why
  `text->moment` never guesses a zone.
- Update the open-items comment at the top of `lib/std/datetime.bjo`: reading
  formats is done; say what is still open.
- Measure `text->date` and `text->instant` against `iso8601->instant` with
  `/tmp/dtbench/bench.bjo` (reading allocates nothing but a zone id), and put
  the numbers in the changelog entry.

## Out of scope

- Optional parts in a format (seconds that may be missing). The ISO readers
  cover that case.
- Formats as text, from a file or a setting. A program chooses among formats
  it holds.
- Reading with .NET's parsing, or any culture's rules.
- Leap seconds, other calendars, years outside 1 to 9999.

## Final report

For each phase: what was built, the commit, the tests added, and anything
decided along the way that this file did not decide, with the example that
decided it. Then the round-trip and mutation counts, the benchmark numbers,
and anything left open.
