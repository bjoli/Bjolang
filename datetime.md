# Task: implement `(std datetime)` for Bjolang

## Context
Bjolang is my Scheme-inspired, statically typed language that compiles to C#. I am the only user, so there are no backward-compatibility concerns.

Before writing anything, read the repository to learn:
- the syntax for traits, records/structs, and `import/extern` of .NET APIs
- how Option is used
- how other `(std ...)` modules are laid out and tested
- naming conventions, especially for Option-returning functions and optional arguments

Do not assume Racket or Scheme syntax. Use what the repo actually does.

Today I import many C# time types directly, and the result is incoherent. I want one coherent model based on Racket's Gregor (the same model as java.time and NodaTime), plus two ideas from OCaml's Ptime:
- the clock is explicit
- the type system rules out meaningless operations

About me: English is not my first language, and I am not trained in compiler design. When you make a design choice, explain what it rules out later, with a concrete example.

## Hard rules
1. No .NET time type (DateTime, DateOnly, TimeOnly, DateTimeOffset, TimeSpan, TimeZoneInfo, TimeProvider) appears in any public signature of `(std datetime)`. Conversions live in a separate module, `(std datetime clr)`.
2. No nulls or invalid values ever reach Bjolang code. Import .NET's TryXyz methods as Option instead of catching exceptions.
3. Write pure Bjolang over `import/extern`: no C# shim and no runtime changes. If something truly needs one, stop and explain why before adding it.
4. Wrappers must be zero-cost: each should compile to a single-field C# struct. If Bjolang can't express that, tell me.
5. Use invariant culture everywhere. Nothing may depend on the machine's locale.

## Types and backing
- `date`: calendar date, no zone. Wraps DateOnly.
- `time`: time of day, no zone. Wraps TimeOnly.
- `datetime`: date + time, no zone. Wraps DateTime, with Kind always Unspecified.
- `instant`: a point on the global timeline. Wraps a UTC DateTime or a long of UTC ticks; choose one and explain why.
- `timezone`: wraps TimeZoneInfo. Looked up by IANA id (e.g. "Europe/Stockholm") and returns Option. Also provide a `utc` value.
- `moment`: datetime + UTC offset + timezone. A Bjolang record.
- `duration`: exact elapsed time. Wraps TimeSpan.
- `date-period`: calendar amounts (years, months, weeks, days). A Bjolang record.
- `period`: a date-period plus a duration, e.g. "1 day and 3 hours".
- `clock`: provide a system clock (backed by TimeProvider.System) and a fixed clock for tests (holds an instant and a timezone). Pick a representation, such as a sum type or a trait, that does not require subclassing TimeProvider from Bjolang.

Limits to document: 100 ns precision (.NET ticks), and years 1 to 9999 only.

## Traits
Each trait has exactly one required method. Everything else is an ordinary generic function built on the traits, so adding a function later (e.g. `->quarter`) never requires touching instances.
- `has-date`: `->date`
- `has-time`: `->time`
- `has-instant`: `->instant`
- `date-arith`: add a `date-period`
- `time-arith`: add a `duration`

Instances:
- date: has-date, date-arith
- time: has-time, time-arith. Wraps at midnight: 23:00 + 2h = 01:00.
- datetime: has-date, has-time, date-arith, time-arith
- moment: all five
- instant: has-instant and time-arith only. `+days` on an instant must be a compile error, because "one day later" needs a zone.

Generic functions (use Gregor's names unless they clash with repo conventions):
- Accessors: `->year`, `->month`, `->day`, `->wday`, `->yday`, `->hours`, `->minutes`, `->seconds`, `->milliseconds`. On moment, also `->utc-offset` and `->timezone`.
- Arithmetic: `+years`, `+months`, `+weeks`, `+days`, `+hours`, `+minutes`, `+seconds`, `+milliseconds`, and the matching `-` versions. Also `+date-period`, `+duration`, and `+period`; `+period` requires both date-arith and time-arith.
- Month arithmetic clamps to the end of the month: 2026-01-31 + 1 month = 2026-02-28.
- A period is applied largest unit first: years, then months, then weeks/days, then the duration.
- Differences, counted in whole units and truncated toward zero:
  - Calendar units (`years-between`, `months-between`, `days-between`) work on has-date types, using local dates.
  - Exact units (`hours-between`, `seconds-between`, ...) use instants for has-instant types, and the local timeline for datetime.
- Comparison, equality, hashing and printing: implement whatever std traits the repo already has for these.

## Zones and daylight saving
- `datetime->moment` takes a required resolver argument, so every ambiguity is visible at the call site.
- A resolver handles two cases:
  - gap: the local time never happens
  - overlap: the local time happens twice
- Built-in resolvers:
  - gap: "push forward by the gap length" and "next valid time"
  - overlap: "earlier" and "later"
- Also provide a strict variant that returns Option: None on a gap or an overlap.
- Date arithmetic on a moment works on the local datetime, then re-resolves with a fixed policy. (A trait method has one signature, so it can't take a resolver.) The policy:
  - overlap: keep the original offset if it is still valid, otherwise take the earlier one
  - gap: push forward
- Also provide a moment function that adds a date-period with an explicit resolver, named by repo convention.
- Duration arithmetic on a moment is exact: go through the instant, then convert back into the same zone.
- `instant->moment` takes a timezone and never needs a resolver.

## Clock
There is no hidden global clock and no global "current timezone". Only these functions read the clock:
- `now`: returns an instant
- `today`: returns the date in the clock's local zone
- variants of both that take an explicit timezone

Every other function is pure.

## Text
- ISO 8601 in both directions for every type. Parsing returns Option.
  - moment: `2026-10-25T02:30:00+01:00[Europe/Stockholm]`
  - instant: `2026-10-25T01:30:00Z`
- `instant->http-date` and `http-date->instant` (IMF-fixdate, RFC 9110), for the bjoweb HTTP library.
- No custom format patterns in v1. Do not expose .NET format strings; that would lock Bjolang to .NET's pattern language.

## `(std datetime clr)`
Explicit conversions in both directions for each wrapped type.
- Conversions from DateTime check Kind and return Option:
  - `clr->datetime` accepts only Unspecified
  - `clr->instant` accepts only Utc
  - Local-kind values are always refused, because their meaning depends on the machine's zone.
- DateTimeOffset → instant always succeeds.

The future SQL wrapper will use these conversions: `timestamp` → datetime, `timestamptz` → instant.

## Out of scope
Non-Gregorian calendars, leap seconds, localized month/day names, custom format patterns, recurrence rules, and bundling a tz database or NodaTime.

## Required tests (zone Europe/Stockholm)
- Moment 2026-10-24 12:00:
  - `+days 1` → 2026-10-25 12:00+01:00
  - `+hours 24` → 2026-10-25 11:00+01:00
- 2026-10-25 02:30 (overlap):
  - earlier → +02:00 (00:30Z)
  - later → +01:00 (01:30Z)
  - strict → None
- 2026-03-29 02:30 (gap):
  - push → 03:30+02:00 (01:30Z)
  - next valid → 03:00+02:00 (01:00Z)
  - strict → None
- Moment 2026-10-24 02:30+02:00, `+days 1` → 2026-10-25 02:30+02:00
- Moment 2026-10-26 02:30+01:00, `-days 1` → 2026-10-25 02:30+01:00
- `+months 1` on 2026-01-31 → 2026-02-28; on 2024-01-31 → 2024-02-29
- `+hours 2` on time 23:00 → 01:00
- An invalid date (2026-02-30) and an unknown zone id both give None, never a value.
- A fixed clock makes `now` and `today` deterministic.
- Compile-fail tests, if the repo supports them: `+days` on an instant; `+hours` on a date.
- If IANA tz data is missing on the machine (e.g. a slim Docker image), tests must fail with a clear message.

## Process
1. Read the repo and summarize the conventions you found (traits, structs, Option, naming, std layout).
2. Write the full public API as signatures only, and show it to me before implementing.
3. Implement and test.
4. Write a short doc page with the type model and the limitations above, each with an example.
