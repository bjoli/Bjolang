module Bjolang.Macro

open System.Collections.Generic
open System.Reflection
open Bjolang.Lexer
open Bjolang.Parser

/// Procedural macros with implicit-renaming hygiene.
///
/// A macro is defined in one module and used from another. Its module is
/// compiled to a `.dll`, loaded into the compiler process, and its transformer
/// invoked by reflection while the *using* module is being parsed. That is the
/// Template Haskell arrangement, and for the same reason: a transformer is
/// ordinary compiled code, so it has to exist before it can run.
///
/// Hygiene is implicit renaming rather than syntax-case. The transformer is
/// handed the form, `inject` and `compare`, and everything it *constructs* is
/// renamed apart from the call site while everything it took from the input is
/// left exactly as written. That gives both halves of what hygiene is for: a
/// binder the template introduces cannot capture a variable the caller wrote,
/// and an identifier the caller wrote still means what it meant where it was
/// written.
///
/// The renaming is a `Gensym.fresh` per template identifier per invocation, so
/// a marked name is a name nothing at the call site can spell. Three
/// consequences, which are the three resolution rules:
///
///   1. If the expansion *binds* the name, ordinary scoping does the work and
///      nothing more is needed. `AlphaRename.freeNames` on the parsed result is
///      exactly the set this did not cover.
///   2. A free marked name that the macro's module exports resolves to
///      `Module_Module::name`, which no local at the call site can shadow.
///   3. Anything else has its mark stripped: a prelude binding, a data
///      constructor, or — via `Parser.headName` — a special form, which is what
///      lets a template write `let`, `if` and `->` unchanged.

type private Syn = Bjolang.Runtime.Syntax
type private Origin = Bjolang.Runtime.SyntaxOrigin
type private SrcRange = Bjolang.Runtime.SrcRange

/// How deep expansion may nest before it is called a runaway.
///
/// A transformer that expands to a call to itself does not terminate, and
/// nothing else in the compiler would notice: the parser would simply keep
/// going until the stack ran out, with no indication of which macro did it.
///
/// Counted per *call site*, not around the transformer call. The recursion is
/// the parser's — `expand` has returned long before the form it produced is
/// read — so a counter bracketing the invocation is never deeper than one. What
/// does track the nesting is the range, because every node a transformer builds
/// inherits the call site's: a macro that expands to a call to itself expands
/// over and over at one range, and that count is the depth.
let private maxDepth = 100

// ---------------------------------------------------------------------------
// The table
// ---------------------------------------------------------------------------

/// One macro the compilation can call.
type MacroBinding =
    { /// The Bjolang name, as written at a call site.
      Name: string
      /// The Bjolang module the transformer was defined in. Used to spell
      /// `Module_Module::helper` for rule 2.
      ModuleName: string
      /// What that module publishes. A template may only name an exported
      /// binding of its own module; anything else has nowhere to resolve to.
      Exports: Set<string>
      Method: MethodInfo }

let private table = Dictionary<string, MacroBinding>()

/// Macros defined by the module currently being parsed.
///
/// They are not in `table` and cannot be: the transformer would have to be
/// compiled before the file defining it has been read. Tracked anyway so that
/// using one says so, rather than failing later with "Unbound variable".
let mutable private localMacros: Set<string> = Set.empty

let setLocalMacros (names: Set<string>) = localMacros <- names

/// Registers the macros an imported assembly publishes.
///
/// Called from `Pipeline` while the module graph is being built — before the
/// importing module is parsed, which is the whole reason dependency discovery
/// moved ahead of parsing.
let register (binding: MacroBinding) = table[binding.Name] <- binding

/// A second spelling of a macro already in the table.
///
/// Answers whether there was one to alias, so that `(:alias ...)` of an
/// ordinary binding can fall through to the type checker. Registered before the
/// module writing the alias is parsed, for the same reason a macro's own name
/// is: the parser has to know a head symbol names a macro at the moment it
/// meets it.
///
/// `ModuleName`, `Exports` and `Method` are the original's — the transformer
/// and the module its templates resolve against do not move.
let alias (newName: string) (oldName: string) : bool =
    match table.TryGetValue oldName with
    | true, binding ->
        table[newName] <- { binding with Name = newName }
        true
    | _ -> false

/// Whether a head symbol names a macro — including one a macro wrote.
///
/// The mark is stripped for the same reason `expand` strips it: a recursive
/// macro's call to itself is written in a template, so it arrives renamed. This
/// is what `parseBody` asks before deciding how to read a form, and a recursive
/// macro that expands to a `def` is read wrongly if the answer is no.
let isMacro (name: string) =
    let known (n: string) = table.ContainsKey n || Set.contains n localMacros
    known name || known (Parser.headName name)

// ---------------------------------------------------------------------------
// Marshalling
// ---------------------------------------------------------------------------

let private toSrcRange (r: Range) =
    SrcRange(r.File, r.Start.Line, r.Start.Column, r.End.Line, r.End.Column)

let private ofSrcRange (sr: SrcRange) (fallback: Range) =
    if sr.IsUnset then
        fallback
    else
        { Start = { Line = sr.StartLine; Column = sr.StartColumn }
          End = { Line = sr.EndLine; Column = sr.EndColumn }
          File = sr.File }

/// The spelling of a punctuation token, and back again.
///
/// Total in both directions on purpose. A comma is an optional argument
/// separator, `(: name type)` is a signature and `...` is a rest pattern, so a
/// macro's input form may contain any of these — and a marshalling that dropped
/// one would hand the transformer a form the programmer did not write.
let private punctuation =
    [ Comma, ","; CommaAt, ",@"; Colon, ":"; Dot, "."; Spread, "..."; Hash, "#"; Quote, "'"; SynQuote, "#'" ]

let private punctSpelling = punctuation |> List.map (fun (t, s) -> t, s) |> Map.ofList
let private punctToken = punctuation |> List.map (fun (t, s) -> s, t) |> Map.ofList

/// Lifts a form the programmer wrote into `Syntax`.
///
/// Everything is marked `CallSite`: this is the input, and the input is what
/// hygiene leaves alone. Note that the marking has to be a field — `Symbol`
/// interns, so the `x` in the input and an `x` a template builds are the same
/// object and identity cannot tell them apart.
let rec private ofSExpr (s: SExpr) : Syn =
    let node: Syn =
        match s with
        | SAtom { Token = Symbol sym } -> Syn.SSym(BjolangRuntime.Symbol.Intern sym)
        | SAtom { Token = QuotedSymbol sym } -> Syn.SDatum(BjolangRuntime.Symbol.Intern sym)
        | SAtom { Token = NumberLit n } -> Syn.SInt n
        | SAtom { Token = StringLit str } -> Syn.SStr str
        | SAtom { Token = CharLit c } -> Syn.SChar(Bjolang.Runtime.BjoChar(uint c))
        // As the symbol it is spelled with: `Syntax` has no boolean node, and
        // `neverRenamed` below already knows these two names.
        | SAtom { Token = BoolLit b } -> Syn.SSym(BjolangRuntime.Symbol.Intern(if b then "#t" else "#f"))
        | SAtom { Token = Keyword k } -> Syn.SKey(BjolangRuntime.Keyword.Intern k)
        | SAtom { Token = t } ->
            match Map.tryFind t punctSpelling with
            | Some spelling -> Syn.SPunct spelling
            | None ->
                failwithf
                    $"Cannot hand %A{t} to a macro at %s{Lexer.formatPos (getRange s)}: it is not a form."
        | SList(items, _) ->
            Syn.SList(SchemeList.SchemeList.FromEnumerable(items |> List.map ofSExpr))

    node.WithRange(toSrcRange (getRange s)).WithOrigin(Origin.CallSite)

/// Identifiers a template may write that must keep their spelling.
///
/// Renaming exists for names that can be *bound*, and none of these can be. The
/// parser matches each of them literally, before resolution gets a chance to
/// strip a mark:
///
///   * `_` is the wildcard, in patterns and in `AlphaRename`.
///   * `&`, `&1`, `&2` … are positional placeholders, in `->` and in `#(...)`.
///   * `#t` and `#f` are the boolean literals.
///
/// Neither a head symbol nor a pattern's constructor needs an entry here:
/// `Parser.headName` strips the mark wherever one is dispatched on, which is
/// what lets a template write `let`, `if` and `(Cons a Nil)` unchanged while a
/// call to the macro module's own helper keeps the mark that resolves it.
let private neverRenamed (name: string) =
    not (AlphaRename.isRenamable name)
    || name = "#t"
    || name = "#f"
    || name.StartsWith "&"

/// Lowers a transformer's result back to a form, renaming as it goes.
///
/// The renaming *is* the hygiene, and it happens here rather than in the
/// transformer: that is what makes it implicit. One memo per invocation, so
/// every occurrence of a template's `tmp` becomes the same fresh name and two
/// invocations of the same macro never share one.
///
/// A node whose range is unset was built by the transformer, and gets the call
/// site's — a constructed node reports where the macro was written, which is
/// the only place a reader can act on.
let rec private toSExpr (memo: Dictionary<string, string>) (callSite: Range) (node: Syn) : SExpr =
    let r = ofSrcRange node.Range callSite
    let atom t = SAtom { Token = t; Range = r }

    match node with
    | :? Syn.SSym as s ->
        let name = s.Item1.Name

        let spelled =
            if node.Origin = Origin.Template && not (neverRenamed name) then
                match memo.TryGetValue name with
                | true, existing -> existing
                | _ ->
                    let fresh = Gensym.fresh name
                    memo[name] <- fresh
                    fresh
            else
                name

        // Back to the token the reader would have produced. Without this a
        // boolean that passed through a macro comes back as a symbol, and a
        // symbol spelled `#t` is not something that reads any more.
        // `neverRenamed` is what guarantees `spelled` is still the original.
        match spelled with
        | "#t" -> atom (BoolLit true)
        | "#f" -> atom (BoolLit false)
        | _ -> atom (Symbol spelled)

    | :? Syn.SDatum as d -> atom (QuotedSymbol d.Item1.Name)
    | :? Syn.SInt as n -> atom (NumberLit n.Item1)
    | :? Syn.SStr as s -> atom (StringLit s.Item1)
    | :? Syn.SChar as c -> atom (CharLit(int c.Item1.Value))
    | :? Syn.SKey as k -> atom (Keyword k.Item1.Name)
    | :? Syn.SPunct as p ->
        match Map.tryFind p.Item1 punctToken with
        | Some t -> atom t
        | None -> failwithf $"A macro produced the punctuation '%s{p.Item1}' at %s{Lexer.formatPos r}, which does not read."
    | :? Syn.SList as l -> SList(l.Item1 |> Seq.map (toSExpr memo callSite) |> List.ofSeq, r)
    | _ -> failwithf $"A macro produced a syntax node the compiler does not know at %s{Lexer.formatPos r}."

// ---------------------------------------------------------------------------
// What the transformer is given
// ---------------------------------------------------------------------------

/// `inject`: an identifier that will *not* be renamed, so it binds at the call
/// site. The one deliberate hole in hygiene, and the only way to make one.
let private inject =
    System.Func<BjolangRuntime.Symbol, Syn>(fun sym -> (Syn.SSym sym).WithOrigin(Origin.CallSite))

/// `compare`: are these two identifiers the same one?
///
/// Base-name equality after mark stripping, and not more. At parse time there
/// is no call-site scope to consult, so `(compare (car clause) #'else)` is true
/// when the caller wrote `else` even if the caller had locally rebound it. That
/// covers the `cond`-like idiom this exists for; it is not denotational
/// equality and does not claim to be.
let private compareIdent =
    System.Func<Syn, Syn, bool>(fun a b ->
        let an = a.IdentifierName
        let bn = b.IdentifierName
        not (isNull an) && not (isNull bn) && Gensym.baseName an = Gensym.baseName bn)

// ---------------------------------------------------------------------------
// Resolution
// ---------------------------------------------------------------------------

/// Rules 2 and 3, applied to whatever rule 1 did not cover.
///
/// `freeNames` is the whole of rule 1: a name the expansion binds is not free,
/// so it keeps its fresh spelling and ordinary scoping has already made it
/// uncapturable. What comes back free is what has to be resolved somewhere
/// else.
///
/// `bound` is what the caller already knows to be bound, and is empty
/// everywhere an expansion lands inside an expression. Declaration position is
/// the exception: a spliced group's binders are not inside anything, so
/// `Parser.boundNames` collects them and hands them over — without which rule 1
/// could not apply to a `(begin (def x 0) (defun (f) x))` at all.
let private resolveIntroduced
    (binding: MacroBinding)
    (memo: Dictionary<string, string>)
    (bound: Set<string>)
    (e: Expr)
    : Expr =
    if memo.Count = 0 then
        e
    else

    // Fresh spelling back to what the template wrote.
    let introduced = memo |> Seq.map (fun kv -> kv.Value, kv.Key) |> Map.ofSeq

    let subst =
        AlphaRename.freeNames bound e
        |> Seq.choose (fun n ->
            match Map.tryFind n introduced with
            | None -> None
            | Some original ->
                if Set.contains original binding.Exports then
                    // Rule 2. Qualified, so a local of the same name at the
                    // call site cannot take it over.
                    Some(n, Naming.qualifiedBinding binding.ModuleName original)
                else
                    // Rule 3. A prelude binding, a data constructor, an
                    // operator, or a special form that reached here as an
                    // identifier — none of which has a module to qualify to.
                    Some(n, original))
        |> Map.ofSeq

    AlphaRename.renameFree subst e

// ---------------------------------------------------------------------------
// Expansion
// ---------------------------------------------------------------------------

/// How many times each macro has expanded at each call site.
///
/// See `maxDepth`: this is what stands in for a depth counter, because the
/// nesting happens in the parser rather than inside the call this module makes.
let private expansions = Dictionary<string * Range, int>()

/// Expands one form, if its head names a macro.
///
/// Installed as `Parser.expandHook`, and reached only after every special form
/// has failed to match — so a macro can never shadow `if`.
let expand (form: SExpr) : Expansion option =
    match form with
    | SList(SAtom { Token = Symbol head } :: _, callSite) ->
        // A macro call written by another macro arrives renamed. Rule 3 again,
        // and the same stripping the parser does for a special form.
        let key =
            if table.ContainsKey head then Some head
            else
                let stripped = Parser.headName head
                if table.ContainsKey stripped then Some stripped else None

        // A macro this very module defines. Not an expansion — its transformer
        // does not exist yet — but saying so beats the "Unbound variable" that
        // would otherwise arrive several passes later.
        if key.IsNone && Set.contains head localMacros then
            failwithf
                $"'%s{head}' is a macro defined in this module, and a macro cannot be used where it is defined, at %s{Lexer.formatPos callSite}. Its transformer runs inside the compiler, so it has to be compiled before whatever uses it is read — which cannot be true of the file it is written in. Move it to a module of its own and import that. An (include ...) will not do: an included file becomes part of this one."

        match key with
        | None -> None
        | Some key ->
            let binding = table[key]

            // A macro *may* expand to a call to itself — that is how a form of
            // any length is taken apart, and each round is one level. This is
            // where it stops being that.
            let seen =
                match expansions.TryGetValue((binding.Name, callSite)) with
                | true, n -> n
                | _ -> 0

            if seen >= maxDepth then
                failwithf
                    $"'%s{binding.Name}' has expanded %d{maxDepth} times at %s{Lexer.formatPos callSite}, which is as far as expansion goes. A transformer that expands to a call to itself on the same input does not terminate; one that recurses on a smaller form does, so check that this one is taking something off."

            expansions[(binding.Name, callSite)] <- seen + 1

            let result =
                try
                    binding.Method.Invoke(null, [| box (ofSExpr form); box inject; box compareIdent |]) :?> Syn
                with :? TargetInvocationException as ex ->
                    // The transformer's own failure, not ours. Unwrapped,
                    // because the reflection frame in the middle says nothing a
                    // reader can use.
                    let inner = if isNull ex.InnerException then ex :> exn else ex.InnerException

                    failwithf
                        $"The macro '%s{binding.Name}' failed at %s{Lexer.formatPos callSite}: %s{inner.Message}"

            let memo = Dictionary<string, string>()
            let expanded = toSExpr memo callSite result

            // The parser has to see through these marks when it dispatches a
            // head symbol, and only these: `x__1` is a name a program may
            // define for itself.
            Parser.noteIntroduced memo.Values

            Some
                { Form = expanded
                  Resolve = resolveIntroduced binding memo }

    | _ -> None

/// Installs the expander into the parser. Idempotent.
let install () =
    Parser.expandHook <- expand
    Parser.isMacroName <- isMacro

// ---------------------------------------------------------------------------
// Scoping
// ---------------------------------------------------------------------------

/// Everything the expander knows, as a value.
///
/// All three fields belong to *one* compilation. Which macros exist is decided
/// by that module's imports under that module's modifiers, so a second module
/// compiled in the same process must not inherit them — the symptom otherwise
/// is not an error but a form silently read as a macro call because some other
/// file imported something that publishes that name.
///
/// `Expansions` is in here for a different reason: it is a runaway counter
/// keyed on a call site, and a call site is a file and a line. Leaking one
/// between compilations cannot make a wrong decision, but it never shrinks, and
/// a long-lived process is exactly where that matters.
type State =
    { Bindings: (string * MacroBinding) list
      Local: Set<string>
      Expansions: ((string * Range) * int) list }

let emptyState =
    { Bindings = []; Local = Set.empty; Expansions = [] }

let snapshot () : State =
    { Bindings = table |> Seq.map (fun kv -> kv.Key, kv.Value) |> List.ofSeq
      Local = localMacros
      Expansions = expansions |> Seq.map (fun kv -> kv.Key, kv.Value) |> List.ofSeq }

let restore (state: State) : unit =
    table.Clear()

    for (name, binding) in state.Bindings do
        table[name] <- binding

    localMacros <- state.Local
    expansions.Clear()

    for (site, count) in state.Expansions do
        expansions[site] <- count
