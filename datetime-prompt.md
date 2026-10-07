# Task: implement `(std datetime)` for Bjolang

`datetime.md` is the original specification. This file records what has been
decided and verified since, and overrides `datetime.md` where the two differ.
Read both, then `CLAUDE.md`.

## Status

- **Done: imported definitions shadow prelude names correctly** (compiler fix
  in `CheckDecl.fs` and `Codegen.fs`, test `TestFiles/243_imported_def_shadow.bjo`,
  entry in `CHANGELOG.org`). Before the fix, a library exporting `now` could not
  be used, because the prelude's `Clock` effect also exports `now`. After it,
  `(std datetime)` may export `now`, `today` and anything else the prelude also
  names, and the importer's calls reach the library's definitions.
- **Done: bug 3.** A library's published bodies may use its own `#:opaque`
  types where they are spliced or specialised. Write the library naturally;
  see "Bug 3" below.
- **Dropped for now: `(std datetime clr)`.** No conversions to .NET types in
  this round. The library is a prototype and may change.
- **Decided: `http-date->instant` accepts IMF-fixdate only**, not the two
  obsolete RFC 850 and asctime forms.

## Verified by prototypes

Each of these was compiled and run against the current compiler:

- `(type (: Date #:opaque (Struct (: v ClrDate))))` compiles to
  `public record struct dt__Date(System.DateOnly v)`. One field, so the wrapper
  costs nothing. A `where`-constrained generic such as `->year` is specialised at
  the call site by `Monomorphise`, so there is no dictionary call.
- `+days`, `-days`, `->year` and `->date` are ordinary identifiers.
- `(out T)` in the Option form imports `TryXyz` methods as Option:
  `DateOnly.TryParseExact` and `TimeZoneInfo.TryFindSystemTimeZoneById` work,
  and give `None` for `2026-02-30` and for an unknown zone id.
- `TimeProvider.System`, `.GetUtcNow`, `.LocalTimeZone` import fine. A clock as
  `(Union (SystemClock TimeProvider) (FixedClock Instant TimeZone))` works
  without subclassing `TimeProvider`.
- Finding the valid offsets for a local time, with
  `TimeZoneInfo.GetUtcOffset(new DateTime(ticks, DateTimeKind.Utc))`, gives the
  right answers for Europe/Stockholm: 2026-10-25 02:30 → `[+2 +1]` (overlap),
  2026-03-29 02:30 → `[]` (gap), June → `[+2]`. The method: take the offsets at
  `local - 1 day` and `local + 1 day` as candidates, and keep each candidate `o`
  for which `offset(local - o) = o`. This assumes at most one transition in any
  two days, which is true of the tz database in practice.
- Compile-fail tests work: `TestFiles/errors/*.bjo` with `;; EXPECT-ERROR:`.
  `+days` on a type without `DateArith` reports
  `no implementation of trait 'DateArith' for 'dt/TimeZone', required by '+days'`.
- The machine's local zone id is `Arctic/Longyearbyen` (read from
  `/etc/localtime`), so tests must never depend on the local zone.

## Bug 3 (fixed)

An exported body that touched the representation of an `#:opaque` type broke
when another module used it: a specialised generic that read an opaque field
crashed the compiler, and an inlined impl that built one fell back to a call
with a warning at every use site. See `CHANGELOG.org`, "A library's bodies may
use its `#:opaque` types where they land".

Now access is decided by the module whose code it is, per expression, and the
representation is published. No helper functions are needed. The prototype in
`/tmp/dtproto` writes `->year` and the `+days` impl directly against the
fields, and in the importing module `(+days d 1)` compiles to
`new dt__Date(d.v.AddDays(1))` and `->year` to a specialised copy: the
zero-cost shape hard rule 4 asks for.

Each opaque type carries an access scope (today always its own module). A
later `(std datetime clr)` could be given access by widening the scope of the
datetime types to it, instead of going through public numbers; that needs a
syntax for the scope, which does not exist yet.

## Repo conventions to follow

- Types and traits are CamelCase (`Eq`, `Ord`, `Refable`). Functions are
  lowercase. `a->b` for conversions, `?` for predicates, `!` for mutation.
- An Option twin of a total function gets `/opt` (`read-line` / `read-line/opt`).
  A plain name when Option is the only version (`path-filename`, `rx-match`).
  `/extra-arg` names a variant that takes one more argument
  (`inbox-try-call/token`, `random-string/chars`).
- Optional arguments are keyword parameters with defaults:
  `(: random-int (-> int int (#:rng RandomSource) int))`.
- The std traits: `Eq` (`=`, `eq-hash`), `Ord` (`compare`), `->str`. Impls of
  `Eq` and `Ord` must be in the module that declares the type, because the
  type's C# class is compiled with them.
- Layout: `lib/std/datetime.bjo`, added to `build_std.sh` after `prelude`.
  Documentation in `Docs/std/datetime.org`. Tests in `TestFiles/NNN_datetime.bjo`
  using `(std simpletest)`'s `expect`. Compile-fail tests in `TestFiles/errors/`.
- Comments follow `CLAUDE.md`: succinct, and explain "this does this, because
  later that".
- Bjolang's `main` installs the invariant culture, but the library still passes
  `CultureInfo.InvariantCulture` to every format and parse call, because a
  program may change the culture itself.

## The API (approved in shape; see "Open decisions")

Items marked *added* are not in `datetime.md`.

```scheme
;; ── Types ───────────────────────────────────────────────────────────────
;; #:opaque:  Date (DateOnly)  Time (TimeOnly)  DateTime (DateTime, Unspecified)
;;            Instant (long: UTC ticks)  TimeZone (TimeZoneInfo)  Duration (TimeSpan)
;;            Moment (Record DateTime Duration TimeZone)
;;            Clock (Union (SystemClock TimeProvider) (FixedClock Instant TimeZone))
;; transparent:
(type (: DatePeriod (Record (: years int) (: months int) (: weeks int) (: days int))))
(type (: Period     (Record (: calendar DatePeriod) (: exact Duration))))
(type (: Gap        (Union GapPush GapNextValid)))
(type (: Overlap    (Union OverlapEarlier OverlapLater)))
(type (: Resolver   (Record (: gap Gap) (: overlap Overlap))))

;; ── Traits: one required method each ────────────────────────────────────
(def/trait (HasDate %a)    (: ->date    (-> %a Date)))
(def/trait (HasTime %a)    (: ->time    (-> %a Time)))
(def/trait (HasInstant %a) (: ->instant (-> %a Instant)))
(def/trait (DateArith %a)  (: +date-period (-> %a DatePeriod %a)))
(def/trait (TimeArith %a)  (: +duration    (-> %a Duration %a)))
(def/trait (Timeline %a)   (: duration-between (-> %a %a Duration)))   ; added, see D4
;; Date: HasDate DateArith                 Time: HasTime TimeArith
;; DateTime: HasDate HasTime DateArith TimeArith Timeline(local)
;; Moment: all six (Timeline via instant)  Instant: HasInstant TimeArith Timeline
;; Duration: TimeArith (added: (+hours (duration #:minutes 30) 1) → PT1H30M)

;; ── Construction (bad input → None) ─────────────────────────────────────
(: date     (-> int int int (Option Date)))
(: time     (-> int int (#:seconds int) (#:nanoseconds int) (Option Time)))
(: datetime (-> int int int (#:hours int) (#:minutes int) (#:seconds int)
                (#:nanoseconds int) (Option DateTime)))
(: date+time->datetime (-> Date Time DateTime))
(: ->datetime  (-> %a DateTime) (where (HasDate %a) (HasTime %a)))
(: duration    (-> (#:days long) (#:hours long) (#:minutes long) (#:seconds long)
                   (#:milliseconds long) (#:nanoseconds long) Duration))
(: date-period (-> (#:years int) (#:months int) (#:weeks int) (#:days int) DatePeriod))
(: period      (-> (#:years int) (#:months int) (#:weeks int) (#:days int)
                   (#:hours long) (#:minutes long) (#:seconds long)
                   (#:milliseconds long) (#:nanoseconds long) Period))

;; ── Zones and moments ───────────────────────────────────────────────────
(: timezone    (-> string (Option TimeZone)))          ; IANA id
(: utc         TimeZone)
(: timezone-id (-> TimeZone string))
(: timezone-offset-at (-> TimeZone Instant Duration))  ; added
(: resolver    (-> Gap Overlap Resolver))
(: datetime->moment     (-> DateTime TimeZone Resolver Moment))
(: datetime->moment/opt (-> DateTime TimeZone (Option Moment)))    ; strict
(: instant->moment      (-> Instant TimeZone Moment))
(: +date-period/resolver (-> Moment DatePeriod Resolver Moment))
(: -date-period/resolver (-> Moment DatePeriod Resolver Moment))   ; added
(: ->utc-offset (-> Moment Duration))
(: ->timezone   (-> Moment TimeZone))
(: adjust-timezone (-> Moment TimeZone Moment))   ; added (Gregor): same instant, other zone

;; ── Instants (added) ────────────────────────────────────────────────────
(: instant->utc-datetime (-> Instant DateTime))
(: utc-datetime->instant (-> DateTime Instant))
(: instant->unix-seconds (-> Instant long))              ; floor
(: unix-seconds->instant (-> long (Option Instant)))
(: instant->unix-milliseconds (-> Instant long))
(: unix-milliseconds->instant (-> long (Option Instant)))

;; ── Clock: the only functions that read time ────────────────────────────
(: system-clock Clock)
(: fixed-clock  (-> Instant TimeZone Clock))
(: clock-timezone (-> Clock TimeZone))                   ; added
(: now        (-> Clock Instant))
(: now/zone   (-> Clock TimeZone Moment))
(: today      (-> Clock Date))
(: today/zone (-> Clock TimeZone Date))

;; ── Accessors ───────────────────────────────────────────────────────────
(: ->year (-> %a int) (where (HasDate %a)))      ; ->month 1–12, ->day 1–31,
;; ->wday 0=Sunday…6 (Gregor), ->yday 1–366: same shape
(: ->hours (-> %a int) (where (HasTime %a)))     ; ->minutes, ->seconds,
;; ->milliseconds 0–999, ->nanoseconds 0–999 999 900 (added): same shape

;; ── Arithmetic ──────────────────────────────────────────────────────────
(: +days  (-> %a int %a)  (where (DateArith %a)))  ; also ±years ±months ±weeks, -days
(: -date-period (-> %a DatePeriod %a) (where (DateArith %a)))
(: +hours (-> %a long %a) (where (TimeArith %a)))  ; also ±minutes ±seconds ±milliseconds, -hours
(: -duration (-> %a Duration %a) (where (TimeArith %a)))
(: +period (-> %a Period %a) (where (DateArith %a) (TimeArith %a)))   ; and -period
(: duration->hours (-> Duration long))  ; ->minutes ->seconds ->milliseconds; toward zero
(: duration-negate (-> Duration Duration))

;; ── Differences (whole units, toward zero) ──────────────────────────────
(: days-between  (-> %a %b int)  (where (HasDate %a) (HasDate %b)))   ; years months weeks(added)
(: hours-between (-> %a %a long) (where (Timeline %a)))               ; minutes seconds milliseconds

;; ── Text (->str prints ISO 8601 for every type) ─────────────────────────
(: iso8601->date     (-> string (Option Date)))       ; 2026-10-25
(: iso8601->time     (-> string (Option Time)))       ; 02:30, 02:30:00.5
(: iso8601->datetime (-> string (Option DateTime)))
(: iso8601->instant  (-> string (Option Instant)))    ; …Z, or an offset converted to UTC
(: iso8601->moment   (-> string (Option Moment)))     ; …+01:00[Europe/Stockholm]
(: iso8601->duration (-> string (Option Duration)))   ; PT26H30M
(: iso8601->date-period (-> string (Option DatePeriod)))   ; P1Y2M3W4D
(: iso8601->period   (-> string (Option Period)))     ; P1DT3H
(: instant->http-date (-> Instant string))            ; Sun, 25 Oct 2026 01:30:00 GMT
(: http-date->instant (-> string (Option Instant)))   ; IMF-fixdate only
```

Trait instances:

- Date, Time, DateTime, Instant and Duration: `Eq`, `Ord`, `->str`.
- TimeZone: `Eq` (by id) and `->str` (its id).
- Moment: `Eq`, `Ord`, `->str`; see D6.
- DatePeriod, Period, Gap, Overlap, Resolver: `Eq` and `->str`.
- Clock: none.

## Design choices, and what each rules out

Each of these goes into `Docs/std/datetime.org` with its example.

- **D1. Instant is a `long` of UTC ticks, not a UTC `DateTime`.**
  `DateTime.Equals` ignores the Kind field, so a `DateTime` saying 12:00 local
  time would compare equal to 12:00 UTC, although they are two hours apart. A
  `long` has no Kind to get wrong. The cost: the library checks the year 1–9999
  range itself on every addition.
- **D2. The value types are opaque.** Nobody can build a Moment whose offset is
  wrong for its zone, such as `2026-07-01T12:00+01:00[Europe/Stockholm]` (July is
  +02:00). Every Moment comes from a function that checks it. This is also what
  keeps .NET types out of the public signatures.
- **D3. Periods are transparent records.** Any mix of numbers is a valid period
  ("1 month and 40 days" is fine, because months differ in length), so there is
  nothing to protect. `(record-ref p months)` works. Equality is field by field:
  `(= (date-period #:weeks 1) (date-period #:days 7))` is `#f`.
- **D4. A sixth trait, `Timeline`.** Exact differences must accept a DateTime or
  anything with an instant, and a `where` clause cannot say "A or B". Making
  DateTime `HasInstant` would let `(->instant some-datetime)` compile, and it
  would have to guess a zone. Both arguments have one type (`%a %a`), so
  `(hours-between some-moment some-instant)` is a compile error; write
  `(->instant some-moment)`. On DateTime it counts local hours: 2026-10-25 00:00
  → 2026-10-26 00:00 is 24 hours as DateTimes and 25 as Stockholm moments.
- **D5. Calendar differences look at dates only, and count months the
  java.time way.** `(days-between 2026-01-01T23:00 2026-01-02T01:00)` is 1,
  although only two hours passed; `hours-between` is for elapsed time.
  `(months-between 2026-01-31 2026-02-28)` is 0, even though
  `(+months 2026-01-31 1)` is 2026-02-28. In return, swapping the arguments only
  flips the sign.
- **D6. Moment `=` compares all fields. `compare` orders by instant, then local
  time, then zone id** (as java.time does, so `Eq` and `Ord` agree).
  12:00+02:00[Stockholm] and 10:00Z[UTC] are the same instant and are not `=`;
  an ordered set keeps both, next to each other. Same instant:
  `(= (->instant a) (->instant b))`.
- **D7. Resolvers are a closed set** (`GapPush`, `GapNextValid`,
  `OverlapEarlier`, `OverlapLater`). A resolver of your own ("on a gap, go back
  to the last valid time") needs a new case in the library. In return, a
  resolver cannot produce a moment for the wrong local time or zone. `/opt`
  covers "refuse".
- **D8. Going past year 9999 or before year 1 raises an exception.**
  `(+days (date 9999 12 31) 1)` raises, because `+days` returns a Date, not an
  Option; an Option would force a `match` after every addition. Constructors and
  parsers return None instead, because bad input is expected there.
- **D9. `+days` and the other calendar units take `int`; `+seconds` and the
  other clock units take `long`.** No date is 2 billion days away, but an `int`
  of seconds overflows at 68 years.
- **D10. `now/zone` returns a Moment.** An Instant has no zone, so "now in
  Stockholm" cannot be one.
- **D11. Importing `(std datetime)` hides the prelude's `(now)`** (monotonic
  milliseconds, the `Clock` effect) in that module. `(std stopwatch)` keeps
  working. A module that needs both writes
  `(import (rename (std datetime) (now instant-now)))`.
- Nanoseconds are cut down to 100 ns: `(time 1 2 #:nanoseconds 150)` holds
  100 ns.
- `(std clr-ord)` also exports the name `DateTime` for `System.DateTime`. In a
  module that imports both, the later import decides what bare `DateTime` means.

## Behaviour from `datetime.md`, restated

- Month arithmetic clamps: 2026-01-31 + 1 month = 2026-02-28; 2024-01-31 +
  1 month = 2024-02-29 (`DateOnly.AddMonths` already does this).
- A period is applied largest unit first: years, then months, then weeks and
  days, then the duration.
- Time arithmetic wraps at midnight: 23:00 + 2 h = 01:00 (`TimeOnly.Add` wraps).
- Date arithmetic on a Moment works on the local datetime and re-resolves.
  Overlap: keep the original offset if still valid, otherwise the earlier one.
  Gap: push forward.
- Duration arithmetic on a Moment is exact: through the instant, then back into
  the same zone.
- `instant->moment` never needs a resolver.
- Moment text: `2026-10-25T02:30:00+01:00[Europe/Stockholm]`. Parsing refuses an
  offset that is not valid for that local time in that zone (None), rather than
  picking one.
- Instant text: `2026-10-25T01:30:00Z`.
- No custom format patterns, and no .NET format strings in the public API.

## Required tests (zone Europe/Stockholm)

All from `datetime.md`:

- Moment 2026-10-24 12:00: `+days 1` → 2026-10-25 12:00+01:00; `+hours 24` →
  2026-10-25 11:00+01:00.
- 2026-10-25 02:30 (overlap): earlier → +02:00 (00:30Z); later → +01:00
  (01:30Z); strict → None.
- 2026-03-29 02:30 (gap): push → 03:30+02:00 (01:30Z); next valid → 03:00+02:00
  (01:00Z); strict → None.
- Moment 2026-10-24 02:30+02:00, `+days 1` → 2026-10-25 02:30+02:00.
- Moment 2026-10-26 02:30+01:00, `-days 1` → 2026-10-25 02:30+01:00.
- `+months 1` on 2026-01-31 → 2026-02-28; on 2024-01-31 → 2024-02-29.
- `+hours 2` on time 23:00 → 01:00.
- An invalid date (2026-02-30) and an unknown zone id both give None.
- A fixed clock makes `now` and `today` deterministic.
- Compile-fail tests in `TestFiles/errors/`: `+days` on an Instant; `+hours` on
  a Date.
- If IANA tz data is missing (`timezone "Europe/Stockholm"` gives None), the test
  prints `FAILURE:` with a message saying the tz database was not found, rather
  than failing on some later assertion.

## Open decisions (ask before implementing)

1. Keep or drop the *added* items in the API.
2. `months-between`: the java.time rule (D5), or "the largest n where `+months`
   does not pass the end" (1 for Jan 31 → Feb 28, but not symmetric).

## Process

1. Confirm the open decisions.
2. Implement `lib/std/datetime.bjo`, add it to `build_std.sh`, and run
   `./build_std.sh`.
3. Write the tests and the two compile-fail tests. Run `./run_tests.py`; the
   whole suite must stay green.
4. Write `Docs/std/datetime.org`: the type model, the limits (100 ns precision,
   years 1–9999, IANA data from the machine), and D1–D11, each with its example.
