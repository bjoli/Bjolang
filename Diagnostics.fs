(* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 *
 * As a special exception to the Mozilla Public License, version 2.0, if you
 * compile your application source code and portions of this software are
 * embedded into the generated object code or executable form as a normal
 * consequence of the compilation process (such as inline functions,
 * templates, generics, or macros), you may redistribute such embedded portions
 * in such object code or executable form without complying with the source code
 * availability requirements or notice obligations of Section 3 of the MPL 2.0.
 *)

namespace Bjolang

/// How the compiler reports a failure, and how it tells one kind from the other.
///
/// A phase collects every error it finds and compilation halts before the next
/// phase begins. Errors are collected rather than raised to the top because one
/// `failwith` ending the build means a file with five broken declarations is
/// fixed and recompiled five times. Compilation halts at the phase boundary
/// because a declaration the type checker never saw cannot be reported on
/// sensibly: parse errors would otherwise reach inference as unknown names, and
/// each one would be reported twice.
///
/// `recover` is what collects. It exists so that the several hundred sites that
/// already raise through `failwith` can stay as they are — the alternative is
/// rewriting each of them into a constructor that returns a diagnostic, and the
/// exception type is already the distinction that matters. Within a phase, only
/// the loops over independent units call it: one per top-level form in the
/// parser, one per declaration in the checker. That is what bounds a failure to
/// the declaration it happened in.
module Diagnostics =
    open System.Text.RegularExpressions

    /// Whether an exception is a diagnostic raised on purpose.
    ///
    /// `failwith` and `failwithf` raise `System.Exception` itself; everything
    /// the runtime raises is a subclass. The exact type is the test, which is
    /// what lets the several hundred sites already reporting this way stay as
    /// they are.
    let isDiagnostic (ex: exn) = ex.GetType() = typeof<System.Exception>

    /// `file.bjo:12`, in any position in a message.
    let private located = Regex(@"\bat \S+:\d+", RegexOptions.Compiled)

    /// Whether a diagnostic should have a location attached to it.
    ///
    /// A message that already says where it happened is left alone, so the
    /// innermost report wins — that is the one that knows most about what went
    /// wrong. A genuine bug is never touched, and is left to be caught by
    /// nothing so that its stack trace survives.
    let needsLocation (ex: exn) =
        isDiagnostic ex && not (located.IsMatch ex.Message)

    let withLocation (where: Lexer.Range) (ex: exn) : exn =
        System.Exception($"%s{ex.Message}\n  at %s{Lexer.formatPos where}")

    /// An invented name's `__12` suffix, wherever one appears in a message.
    let private invented = Regex(@"(?<=[A-Za-z0-9_?!*/<>=+'&.-])__\d+\b", RegexOptions.Compiled)

    /// The suffix `Naming.suspendingCopy` adds to a generated second body.
    let private generatedCopy =
        Regex(@"(?<=[A-Za-z0-9_?!*/<>=+'&.-])__bjo\b", RegexOptions.Compiled)

    /// The infix `Naming.specializedCopy` adds to a monomorphised copy, and
    /// everything after it — the type arguments it was made at.
    ///
    /// Stripped for the reason `__bjo` is: `same?__at_int` is a real method and
    /// a stack trace naming it is useful, but it appears in no source file, so
    /// a compiler error naming it sends the reader looking for something that
    /// is not there.
    let private specializedCopy =
        Regex(@"(?<=[A-Za-z0-9_?!*/<>=+'&.-])__at_[A-Za-z0-9_]+", RegexOptions.Compiled)

    /// The qualifier on a landing pad — the impl's own method, named directly
    /// instead of dispatched: `Fetcher_System_Int32.Instance::fetch`.
    ///
    /// Which method the reader wrote is the part after the `::`; everything
    /// before it is the class `Codegen` will emit and a spelling of the
    /// implementor nobody chose. Whether a call was inlined, landed on a pad or
    /// went through a dictionary is the compiler's business, and a message that
    /// leaks the answer is asking the reader to care about it.
    let private landingPad =
        Regex(@"[A-Za-z0-9_.]+::(?=[A-Za-z0-9_?!*/<>=+'&.-])", RegexOptions.Compiled)

    /// Strips the suffix `Gensym.fresh` adds.
    ///
    /// Every renaming in the compiler goes through `Gensym`, and none of the
    /// names it invents is one the programmer wrote: a loop's copied slot, an
    /// inlined body's freshened binder, and — the reason this exists — a macro
    /// template's identifier, renamed apart from the call site so that it cannot
    /// capture. Reporting `tmp__37` names a thing that appears in no source
    /// file. Reporting `tmp` names what was written.
    ///
    /// Applied at the point of printing rather than at each of the several
    /// hundred sites that raise, and only there: the names themselves have to
    /// stay distinct right up until the message is built, since being distinct
    /// is their whole purpose.
    ///
    /// A generated suspending copy is stripped here for the same reason. The
    /// copy a `-?->` or a `defbjouble` promises is emitted as `read-line__bjo`,
    /// and a call site rewritten to it carries that name into whatever is
    /// reported next. A stack trace naming it is fine — it is a real method. A
    /// compiler error naming it is not, because it appears in no source file
    /// and the reader has no way to find it.
    let humanize (message: string) =
        landingPad.Replace(
            generatedCopy.Replace(specializedCopy.Replace(invented.Replace(message, ""), ""), ""),
            ""
        )

    /// Whether the compiler narrates what it is doing.
    ///
    /// A REPL entry runs the same pipeline as a build, and six step banners per
    /// keystroke is not what the prompt is for — so the narration is a setting
    /// rather than something the REPL reimplements a quieter pipeline to avoid.
    let mutable verbose = true

    type Severity =
        | Error
        | Warning

    /// One thing the compiler has to say about the program.
    ///
    /// `Where` is set only when the message does not already carry a location,
    /// so that the innermost report — the one that knows most about what went
    /// wrong — keeps its own. `Phase` records which gate collected the
    /// diagnostic; nothing prints it, and it is what tells a parse error from a
    /// type error when reading the collector in a debugger.
    type Diagnostic =
        { Severity: Severity
          Message: string
          Where: Lexer.Range option
          Phase: string }

    /// Everything collected since the last `reset`.
    let private collected = ResizeArray<Diagnostic>()

    /// How much of `collected` has already been printed.
    ///
    /// A watermark rather than clearing on print, because `hasErrors` decides an
    /// exit code and is asked *after* the report has gone out. Reporting twice
    /// is normal — a phase gate prints what it collected, and `Build.compile`
    /// prints whatever was collected after it — and each diagnostic has to
    /// appear once across the two.
    let mutable private reported = 0

    /// Names whose declaration failed, and how much had been collected when it
    /// did.
    ///
    /// A declaration that failed to check is absent from the environment, so
    /// every later mention of it is an unknown name — a report about the first
    /// failure, worded as though it were a second one. The checker answers this
    /// by binding a placeholder, which covers values; a type, a trait or an
    /// instance cannot be stood in for as cheaply, and this is what covers
    /// those. The index is what makes it *later* only: a diagnostic collected
    /// before the declaration failed is about something else that happens to
    /// share the name.
    let private poisoned = System.Collections.Generic.Dictionary<string, int>()

    let reset () =
        collected.Clear()
        reported <- 0
        poisoned.Clear()

    let record (d: Diagnostic) = collected.Add d

    let hasErrors () =
        collected |> Seq.exists (fun d -> d.Severity = Error)

    /// Whether `message` names `name` as a word rather than inside a longer one.
    ///
    /// `'` is not treated as part of a name, so that the quoting most messages
    /// put around one does not hide it. Everything else a Bjolang identifier can
    /// contain is, so `broken` does not match inside `broken-thing`.
    let private mentions (name: string) (message: string) =
        let isNameChar (c: char) =
            System.Char.IsLetterOrDigit c || "_?!*/<>=+&.-".Contains c

        let rec search (from: int) =
            match message.IndexOf(name, from, System.StringComparison.Ordinal) with
            | -1 -> false
            | i ->
                let beforeOk = i = 0 || not (isNameChar message[i - 1])
                let after = i + name.Length
                let afterOk = after >= message.Length || not (isNameChar message[after])

                if beforeOk && afterOk then true else search (i + 1)

        not (System.String.IsNullOrEmpty name) && search 0

    /// Whether the diagnostic at `index` is a later mention of a name some
    /// earlier declaration already failed over.
    ///
    /// Shared by the report and the count so that `N errors.` says how many
    /// errors were printed. A warning is never suppressed: it is about the
    /// declaration it was raised in, not about the one that failed.
    let private suppressedAt (index: int) (d: Diagnostic) =
        d.Severity = Error
        && poisoned |> Seq.exists (fun kv -> index >= kv.Value && mentions kv.Key d.Message)

    /// How many errors the compiler has to report. Warnings are not counted, and
    /// neither is a cascade the report will drop: this is what decides an exit
    /// code and what `N errors.` says.
    let count () =
        collected
        |> Seq.indexed
        |> Seq.filter (fun (i, d) -> d.Severity = Error && not (suppressedAt i d))
        |> Seq.length

    /// `file.bjo:12`, with the parts pulled out rather than matched.
    let private locationParts = Regex(@"\bat (\S+):(\d+)", RegexOptions.Compiled)

    /// Where a diagnostic sorts: file, then line, then column.
    ///
    /// A message that carried its own location has `Where` unset, so the key is
    /// read back out of the text — the first match, which is the innermost
    /// report. Nothing that can be sorted on survives in a message with no
    /// location at all, and those sort last.
    let private sortKey (d: Diagnostic) : string * int * int =
        match d.Where with
        | Some r -> System.IO.Path.GetFileName r.File, r.Start.Line, r.Start.Column
        | None ->
            let m = locationParts.Match d.Message

            if m.Success then
                m.Groups[1].Value, int m.Groups[2].Value, 0
            else
                "\uffff", System.Int32.MaxValue, 0

    /// A diagnostic as it is printed, location included.
    let private render (d: Diagnostic) =
        match d.Where with
        | Some r -> $"%s{d.Message}\n  at %s{Lexer.formatPos r}"
        | None -> d.Message

    /// How many errors are printed before the rest are counted instead.
    ///
    /// One broken declaration early in a file can put every later one in the
    /// list, and a screen of them buries the one worth reading.
    let private reportLimit = 25

    /// Marks `names` as belonging to a declaration that failed to check.
    let poison (names: string seq) =
        for name in names do
            if not (poisoned.ContainsKey name) then
                poisoned[name] <- collected.Count

    /// Whether anything was poisoned since the last `reset`.
    let hasPoison () = poisoned.Count > 0

    /// Refuses to hand on a checked program built over a placeholder binding.
    ///
    /// A declaration checked against `forall a. a` can succeed against a type
    /// nobody wrote, so the tree is only trustworthy if nothing was poisoned.
    /// The phase gates return `None` long before this, which is why reaching it
    /// is a fault in the compiler rather than in the program — and why it is
    /// raised as something `isDiagnostic` rejects, so that it keeps its trace.
    let assertNoPoison () =
        if hasPoison () then
            raise (
                System.InvalidOperationException(
                    "A checked program reached code generation with placeholder bindings in its environment. "
                    + "The type-check gate should have stopped the build. Poisoned: "
                    + (poisoned.Keys |> String.concat ", ")
                )
            )

    /// Builds a diagnostic from an exception raised by a phase.
    ///
    /// The location is taken from `where` only when the message carries none,
    /// which is the same rule `withLocation` is applied under.
    let ofException (phase: string) (where: Lexer.Range option) (ex: exn) : Diagnostic =
        { Severity = Error
          Message = humanize ex.Message
          Where = if needsLocation ex then where else None
          Phase = phase }

    /// Prints everything collected since the last time this was called.
    ///
    /// Sorted by position so that the list reads in the order the file does,
    /// rather than in whatever order the passes happened to run. Exact
    /// duplicates are dropped: a declaration checked once as itself and once as
    /// a generated copy reports the same message twice, and the second adds
    /// nothing.
    let report () =
        let first = min reported collected.Count

        let fresh =
            collected
            |> Seq.indexed
            |> Seq.skip first
            // A diagnostic naming something an earlier declaration already
            // failed over is that failure reported a second time, under a name
            // that only looks unrelated.
            |> Seq.filter (fun (i, d) -> not (suppressedAt i d))
            |> Seq.map snd
            |> Seq.toList

        reported <- collected.Count

        let ordered =
            fresh
            |> List.sortWith (fun a b -> compare (sortKey a) (sortKey b))
            |> List.distinctBy (fun d -> d.Severity, render d)

        // On stderr and unprefixed by anything else, because that is what
        // `warn` wrote before warnings were collected rather than printed.
        for d in ordered |> List.filter (fun d -> d.Severity = Warning) do
            eprintfn $"Warning: %s{render d}"

        let errors = ordered |> List.filter (fun d -> d.Severity = Error)

        if not errors.IsEmpty then
            // The blank line `reportFailure` opens with, so that a failure looks
            // the same whether one phase collected it or the outer handler did.
            printfn ""

            for d in errors |> List.truncate reportLimit do
                printfn $"%s{render d}"

            let hidden = errors.Length - reportLimit

            if hidden > 0 then
                printfn $"... and %d{hidden} more errors."

    /// Runs `f`, and answers `fallback ()` if it raised a diagnostic.
    ///
    /// The `when` filter is load-bearing: it runs before the stack unwinds, so
    /// an exception that is not a diagnostic is never caught here and keeps its
    /// trace. This is the only new control flow the collector introduces, and
    /// every phase gate is a loop that calls it once per independent unit.
    ///
    /// The fallback is a function because the type checker's is not a constant:
    /// it binds a placeholder for the declaration that failed, and doing that
    /// eagerly would poison the name of every declaration that succeeded.
    let recoverWith (phase: string) (where: Lexer.Range option) (fallback: unit -> 'a) (f: unit -> 'a) : 'a =
        try
            f ()
        with ex when isDiagnostic ex ->
            record (ofException phase where ex)
            fallback ()

    /// The same, where the fallback is already a value.
    let recover (phase: string) (where: Lexer.Range option) (fallback: 'a) (f: unit -> 'a) : 'a =
        recoverWith phase where (fun () -> fallback) f

    let progress (message: string) = if verbose then printfn "%s" message

    /// Records something the compiler accepted and suspects was not meant.
    ///
    /// Collected rather than printed, so that a warning sorts into position
    /// beside the errors instead of landing wherever the pass that raised it
    /// happened to run. It still reaches stderr unprefixed by anything but
    /// `Warning: `, which is what `TestFiles/warnings/` matches on, and it
    /// still survives `verbose` being off: a warning is not narration.
    /// Humanized for the reason a failure is, since a warning can name a binder
    /// a macro introduced.
    let warn (message: string) =
        record
            { Severity = Warning
              Message = humanize message
              Where = None
              Phase = "warn" }

    /// Prints a failed compilation.
    ///
    /// A diagnostic is the message and nothing else: a stack trace through the
    /// inferencer describes the compiler rather than the program, and there is
    /// nothing in it for whoever wrote the program. A genuine bug keeps its
    /// trace and says which of the two it is.
    let reportFailure (ex: exn) =
        printfn ""
        printfn $"%s{humanize ex.Message}"

        if not (isDiagnostic ex) then
            printfn ""
            printfn "This is a bug in the compiler, not in the program above. Trace:"
            printfn $"%s{ex.StackTrace}"

    /// Kör `f` med allt den skriver till konsolen uppsamlat, och lämnar
    /// tillbaka både resultatet och texten.
    ///
    /// Hela `Console` växlas om, inte bara den här modulens utskrifter. En
    /// kompilering rapporterar från ett dussin moduler med rakt `printfn` —
    /// `Pipeline` säger vilket beroende den bygger, `Build` skickar vidare vad
    /// C#-kompilatorn sade — och en batch måste kunna svara på vilken indatafil
    /// som sade vad. Att fånga vid källan skulle betyda ett anrop att ändra på
    /// varje ställe som skriver, och nästa tillagda `printfn` skulle tyst falla
    /// utanför.
    ///
    /// Priset är att `Console.SetOut` gäller hela processen: det här duger för
    /// en batch som kompilerar en fil i taget, och för ingenting som kompilerar
    /// två samtidigt.
    ///
    /// stdout och stderr går till samma buffert, så att en varning hamnar där
    /// den skrevs i förhållande till resten. Den som läser texten letar efter
    /// en sträng i den, inte efter vilken ström den kom på.
    let captured (f: unit -> 'a) : 'a * string =
        let buffer = System.Text.StringBuilder()
        use writer = new System.IO.StringWriter(buffer)
        let previousOut = System.Console.Out
        let previousError = System.Console.Error

        System.Console.SetOut writer
        System.Console.SetError writer

        try
            // Texten läses innanför `try`, eftersom en `f` som kastar har hunnit
            // skriva det som förklarar varför.
            let result = f ()
            result, buffer.ToString()
        finally
            System.Console.SetOut previousOut
            System.Console.SetError previousError
