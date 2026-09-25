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

module Bjolang.DeclParser

open Lexer
open Bjolang.Ast
open Bjolang.Hygiene
open Bjolang.TypeSyntax
open Bjolang.Parser

/// What a foreign import clause said, beyond its alias and target.
///
/// A record rather than a wider tuple: the clause has grown three optional
/// pieces and a positional read of five values at two call sites was already
/// hard to get right.
type ForeignImportOptions =
    { ExplicitType: FType option
      Exceptions: string list
      /// `#:async` — the target returns a task, and calling it is a yield
      /// point. The Bjolang type is the task's *result*; `Task` is never a
      /// Bjolang type.
      IsAsync: bool
      /// `#:uncancellable` — do not thread the ambient token into this call.
      /// Required where the method has no `CancellationToken` overload, so that
      /// "this cannot be cancelled" is written where it is decided rather than
      /// discovered when a `choose` leaks work.
      Uncancellable: bool
      /// `#:cancellable` — thread the ambient token into a call that is *not*
      /// `#:async`.
      ///
      /// Needed because a `CancellationToken` parameter is not always optional.
      /// `File.ReadLinesAsync` hands back an `IAsyncEnumerable` rather than a
      /// task, so it is not an `#:async` import, and every one of its overloads
      /// takes a token — which makes it uncallable without this.
      Cancellable: bool
      /// `#:blocking` — calling the target parks the thread it runs on, so a
      /// bjoroutine that reaches it holds a pool thread rather than suspending.
      /// Read by the blocking lint and by nothing else.
      IsBlocking: bool
      /// `#:get` — the target names a property or field, and the alias reads
      /// it.
      IsGet: bool
      /// `#:set` — the target names a property or field, and the alias writes
      /// it.
      ///
      /// One accessor per clause, so a read/write property is two clauses under
      /// two names. A property has no overloads to disambiguate, so nothing is
      /// gained by naming both in one place, and two clauses is what lets a
      /// module import only the read.
      IsSet: bool }

/// One clause of `import/extern` or `import/class`.
///
/// Both forms are spelled the same way — a Bjolang name, then a colon form
/// naming the fully qualified .NET target, its type, and optionally the
/// exceptions the call is allowed to turn into an `Err` — so one reader does
/// for both.
let parseForeignImportClause
    (formName: string)
    (s: SExpr)
    : string * string list * string * ForeignImportOptions * Range =
    let r = getRange s

    let malformed () : 'a =
        failwithf
            $"Syntax error in %s{formName} at %s{Lexer.formatPos r}: expected (alias (: Fully.Qualified.Target type)), the type optionally followed by #:exceptions (ExceptionType ...), #:async, #:blocking, #:uncancellable, #:get or #:set."

    match s with
    // The alias may be written applied — `(Set %a)` — which is how a generic
    // .NET type is named: it is a type constructor, so it is not a type until
    // it has its arguments, and `import/class` is the only form that accepts
    // the applied spelling. `parseTypeDefHead` reads it, so the shape is the
    // one every other type declaration in the language uses.
    | SList([ head; SList(SAtom { Token = Colon } :: SAtom { Token = Symbol clrTarget } :: rest, _) ], _) ->
        let alias, typeParams =
            match head with
            | SAtom { Token = Symbol name } -> name, []
            | SList _ -> parseTypeDefHead head
            | _ -> malformed ()

        // The signature is optional. Given, it is enforced against what
        // reflection finds; omitted, the arguments at each call site decide.
        let explicitType, optionForms =
            match rest with
            | SAtom { Token = Keyword _ } :: _ -> None, rest
            | [] -> None, []
            | t :: tail -> Some(parseType t), tail

        // Order-independent, because there is no reason for it not to be and
        // three positional keywords would be one more thing to remember.
        // `opts` is annotated because `ExternImportSpec` carries these same
        // four labels, so a copy-and-update on it does not say which record is
        // being updated.
        let rec readOptions (opts: ForeignImportOptions) forms =
            match forms with
            | [] -> opts
            | SAtom { Token = Keyword "exceptions" } :: SList(names, _) :: tail ->
                if names.IsEmpty then
                    failwithf
                        $"Syntax error in %s{formName} at %s{Lexer.formatPos r}: #:exceptions names no exception types. Leave it off entirely to let everything propagate."

                let exceptions =
                    names
                    |> List.map (function
                        | SAtom { Token = Symbol n } -> n
                        | bad ->
                            failwithf
                                $"Syntax error in %s{formName} at %s{Lexer.formatPos (getRange bad)}: #:exceptions takes fully qualified .NET exception type names, as in System.IO.IOException.")

                readOptions { opts with Exceptions = exceptions } tail
            | SAtom { Token = Keyword "async" } :: tail -> readOptions { opts with IsAsync = true } tail
            | SAtom { Token = Keyword "uncancellable" } :: tail -> readOptions { opts with Uncancellable = true } tail
            | SAtom { Token = Keyword "cancellable" } :: tail -> readOptions { opts with Cancellable = true } tail
            | SAtom { Token = Keyword "blocking" } :: tail -> readOptions { opts with IsBlocking = true } tail
            | SAtom { Token = Keyword "get" } :: tail -> readOptions { opts with IsGet = true } tail
            | SAtom { Token = Keyword "set" } :: tail -> readOptions { opts with IsSet = true } tail
            | _ -> malformed ()

        let options =
            readOptions
                { ExplicitType = explicitType
                  Exceptions = []
                  IsAsync = false
                  Uncancellable = false
                  Cancellable = false
                  IsBlocking = false
                  IsGet = false
                  IsSet = false }
                optionForms

        alias, typeParams, clrTarget, options, r
    | _ -> malformed ()

/// Rejects the keyword combinations an accessor clause cannot mean.
///
/// Shared by `import/extern` and `import/class` so that the second form's
/// blanket refusal of `#:get` and `#:set` still says the same things about
/// them.
let private checkAccessorOptions (formName: string) (opts: ForeignImportOptions) (r: Range) : unit =
    let where = Lexer.formatPos r

    if opts.IsGet && opts.IsSet then
        failwithf
            $"Syntax error in %s{formName} at %s{where}: a clause is one accessor. Write the read and the write as two clauses under two names — that is also what lets a module import only the read."

    if opts.IsGet || opts.IsSet then
        if opts.IsAsync || opts.Cancellable || opts.Uncancellable then
            failwithf
                $"Syntax error in %s{formName} at %s{where}: #:async, #:cancellable and #:uncancellable describe how a *call* is made, and reading or writing a property is not a call. Nothing about a property can be awaited or cancelled."

        if opts.IsBlocking then
            failwithf
                $"Syntax error in %s{formName} at %s{where}: #:blocking says a *call* parks the thread it runs on, and reading or writing a property is not a call. A property that does real work behind an accessor is better imported as the method it is."

        if not opts.Exceptions.IsEmpty then
            failwithf
                $"Syntax error in %s{formName} at %s{where}: #:exceptions decorates a call, and a property access is not one. An accessor that can fail is guarded with (try ... #:catch (...)) at the place it is read or written."

/// A prefix or postfix has to lex as part of a symbol.
///
/// The name it builds is one the importing module writes by hand, so a prefix
/// containing anything the tokenizer would break on produces a binding nothing
/// can refer to. `/` is the expected separator and is a symbol character; `.`
/// is one too, but is refused anyway — a dot is how a .NET member is spelled,
/// and a name that looks like one but is not would be read as such well before
/// anybody suspected the import.
let private checkAffix (form: string) (affix: string) (r: Lexer.Range) : string =
    if affix = "" then
        failwithf $"Empty string in (%s{form} ...) at %s{Lexer.formatPos r}. A prefix or postfix has to be a non-empty string."

    match affix |> Seq.tryFind (fun c -> not (Lexer.isSymbolChar c) || c = '.') with
    | Some bad ->
        let described =
            if System.Char.IsWhiteSpace bad then
                "whitespace, which cannot appear inside a symbol"
            elif bad = '.' then
                "'.', which is how a .NET member is spelled"
            else
                $"'%c{bad}', which cannot appear inside a symbol"

        failwithf
            $"Invalid prefix \"%s{affix}\" in (%s{form} ...) at %s{Lexer.formatPos r}: it contains %s{described}. A prefix has to lex as part of the name it builds — '/' is the usual separator."
    | None -> affix

/// One import, which is a path wrapped in zero or more modifiers.
///
/// Modifiers nest rather than sit in a list, so that they compose in a stated
/// order: the innermost applies first. They are collected outermost-first here
/// and reversed, so `Modifiers` reads in application order.
let rec private parseImportForm (s: SExpr) : ImportSpec =
    let r = getRange s

    let rec go (s: SExpr) (acc: ImportModifier list) : ImportSpec =
        let names (nodes: SExpr list) =
            nodes
            |> List.map (function
                | SAtom { Token = Symbol n } -> n
                | bad -> failwithf $"Expected a name at %s{Lexer.formatPos (getRange bad)}")

        match s with
        | SAtom { Token = StringLit p } -> { Path = RelativePath p; Modifiers = acc }

        | SList(SAtom { Token = Symbol head } :: inner :: rest, mr) when
            List.contains
                head
                [ "only"; "except"; "prefix"; "postfix"; "prefix-defs"; "postfix-defs"; "prefix-types"
                  "postfix-types"; "rename" ]
            ->
            let modifier =
                match head, rest with
                | "only", args -> Only(names args)
                | "except", args -> Except(names args)
                | "rename", pairs ->
                    pairs
                    |> List.map (function
                        | SList([ SAtom { Token = Symbol from }; SAtom { Token = Symbol to' } ], _) -> from, to'
                        | bad ->
                            failwithf
                                $"Invalid (rename ...) clause at %s{Lexer.formatPos (getRange bad)}. Expected: (old-name new-name)")
                    |> Rename
                | affixForm, [ SAtom { Token = StringLit affix } ] ->
                    let a = checkAffix affixForm affix mr

                    match affixForm with
                    | "prefix" -> Prefix a
                    | "postfix" -> Postfix a
                    | "prefix-defs" -> PrefixDefs a
                    | "postfix-defs" -> PostfixDefs a
                    | "prefix-types" -> PrefixTypes a
                    | _ -> PostfixTypes a
                | affixForm, _ ->
                    failwithf
                        $"Invalid (%s{affixForm} ...) at %s{Lexer.formatPos mr}. Expected: (%s{affixForm} import \"affix\")"

            go inner (modifier :: acc)

        // A bare list of symbols is a module path, and so the innermost thing
        // an import can be. Checked after the modifier heads, which are lists
        // of the same shape.
        | SList(pathNodes, _) when not pathNodes.IsEmpty ->
            { Path = ModulePath(names pathNodes); Modifiers = acc }

        | _ -> failwithf $"Invalid import syntax at %s{Lexer.formatPos r}"

    go s []

/// Literal `(begin ...)` forms flattened out of a list of declaration forms.
///
/// For the two places that read a body of declarations with their own loop
/// rather than through `parseDeclForms` — `def/trait` and `impl` — so that
/// a splice means the same thing inside one of those as it does at the top
/// level. Recursive, so nesting flattens; a `begin` a template wrote is
/// unmarked on the way past, as everywhere else a head is dispatched on.
let rec flattenBegins (forms: SExpr list) : SExpr list =
    forms
    |> List.collect (fun form ->
        match stripHeadMark form with
        | SList(SAtom { Token = Symbol "begin" } :: inner, _) -> flattenBegins inner
        | other -> [ other ])

/// What a declaration is, for an error message that has to say what a macro
/// produced where something else was wanted.
let declKindName (d: Decl) : string =
    match d with
    | DSignature _ -> "a signature"
    | DImport _ -> "an import"
    | DAlias _ -> "an alias"
    | DExport _ -> "an export"
    | DReExport _ -> "a re-export"
    | DModule _ -> "a module"
    | DDef _ -> "a definition"
    | DDefTuple _ -> "a tuple definition"
    | DDefPattern _ -> "a destructuring definition"
    | DDefMutable _ -> "a mutable definition"
    | DDefun _ -> "a function"
    | DDefDouble _ -> "a function with a body per colour"
    | DType _
    | DTypeRec _ -> "a type declaration"
    | DTrait _ -> "a trait"
    | DExtern _ -> "an imported binding"
    | DImportAlias _ -> "an imported spelling"
    | DImportExtern _ -> "a foreign import"
    | DImportClass _ -> "a class import"
    | DInlineImpl _ -> "an inline method body"
    | DMacro _ -> "a macro"
    | DPatternMacro _ -> "a pattern macro"
    | DHashMacro _ -> "a hash macro"
    | DSyncOnly _ -> "a #:sync marker"
    | DImpl _ -> "an implementation"
    | DImplExtern _ -> "an imported implementation"

/// A macro may not introduce an import.
///
/// `Pipeline.importsOf` and `expandIncludes` read the raw S-expressions of a
/// file *before* it is parsed, and they have to: parsing a form whose head is a
/// macro requires that macro's own module to be compiled and loaded already. So
/// an `(import ...)` a macro produces is one the module graph never saw — the
/// dependency is never compiled, never linked, and the user is told about an
/// unbound variable in code they did not write.
let rejectSplicedImports (decls: Decl list) (callSite: Range) : unit =
    for d in decls do
        match d with
        | DImport _ ->
            failwithf
                $"Macro Error at %s{Lexer.formatPos callSite}: a macro cannot introduce an import. The import graph is built from the source forms before any macro runs, because parsing a macro call requires that macro's module to be compiled already — so an import a macro produces is never linked. Write the import in the file that uses the macro."
        | _ -> ()

/// One declaration form, or `None` when nothing here matched it.
///
/// `None` says only that: what it *means* is the caller's to decide.
/// `parseDeclForms` reads it as "then it must be a macro call", and `parseDecl`
/// as an error. Keeping the two apart is what lets a macro in declaration
/// position produce several declarations — a function returning one `Decl` has
/// nowhere to put them.
/// A `defun` or `defbjo`, and the signature it writes for itself.
///
/// A top-level function needs a `(: name type)`: `Inference` refuses every name
/// but `main` without one, and `Exports` refuses to publish one. Writing the
/// types at the parameters and after the argument list says the same thing in
/// one form rather than two —
///
///   (: double (-> int int))        (defun (double (: x int)) : int
///   (defun (double x) (* x 2))       (* x 2))
///
/// — so that is what it becomes. The signature is *synthesized*, not a second
/// mechanism: the arity check, the export rule and the metadata a `.dll`
/// publishes all read a `DSignature` and cannot tell which spelling produced
/// it. `def/macro` writes its transformer's signature the same way.
///
/// The colour is `Ordinary` whichever definer this is, because that is what a
/// hand-written arrow says. `defbjo` is what declares a function may suspend,
/// and `recolour` repaints the declared type before anything unifies with it.
///
/// All or nothing. A return type beside an unannotated parameter is refused
/// rather than half-read: it used to be accepted and silently discarded, which
/// reads as a claim that was checked.
let private parseDefunDecl
    (definer: string)
    (name: string)
    (args: SExpr list)
    (rest: SExpr list)
    (r: Lexer.Range)
    : Decl list =

    let colour = if definer = "defbjo" then Suspending else Ordinary
    let parsedArgs = parseDefunArgs args
    let retAnn, bodyExprs = parseDefunReturn rest
    let defun = DDefun(name, parsedArgs, parseBody bodyExprs r, colour, r)

    match retAnn with
    | None -> [ defun ]
    | Some ret ->
        let where = Lexer.formatPos r

        let refuse (what: string) =
            failwithf
                $"Invalid signature on '%s{name}' at %s{where}: %s{what} Write a (: %s{name} ...) beside the definition instead."

        // A keyword or a rest parameter has nowhere to write its type — the
        // argument grammar gives one a default expression and the other a bare
        // name — so a function taking either cannot describe itself here, and
        // says so rather than producing a signature with a parameter missing.
        for a in parsedArgs do
            match a with
            | MandatoryArg _ -> ()
            | KeywordArg(kw, _) ->
                refuse $"the keyword parameter '#:%s{kw}' has nowhere to write its type."
            | RestArg restName -> refuse $"the rest parameter '%s{restName}' has nowhere to write its type."

        let mandatory =
            parsedArgs
            |> List.choose (function
                | MandatoryArg(argName, ann) -> Some(argName, ann)
                | _ -> None)

        match mandatory |> List.tryFind (snd >> Option.isNone) with
        | Some(bare, _) ->
            failwithf
                $"Invalid signature on '%s{name}' at %s{where}: the return type is written here but the parameter '%s{bare}' has no type. Give every parameter one — (: %s{bare} <type>) — or drop the return type and write a (: %s{name} ...) beside the definition."
        | None ->
            // A trait constraint has nowhere to go in this form either, so a
            // constrained generic still writes its signature separately. The
            // empty list is what an unconstrained one has.
            let argTypes = mandatory |> List.map (snd >> Option.get)
            [ DSignature(name, TArrow(argTypes, [], None, ret, Ordinary, r), [], r); defun ]

let rec tryParseDecl (s: SExpr) : Decl option =
    let r = getRange s
    let s = stripHeadMark s

    match s with
    | SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) ->
        Some(DSignature(name, parseType tType, [], r))
    // (: name type (where (TraitName %var) ...)) — signature with trait constraints
    | SList(SAtom { Token = Colon } :: SAtom { Token = Symbol name } :: tType :: SList(SAtom { Token = Symbol "where" } :: constraintExprs, _) :: _, _) ->
        let constraints =
            constraintExprs |> List.choose (function
                | SList([ SAtom { Token = Symbol traitName }; SAtom { Token = QuotedSymbol varName } ], _) ->
                    Some (traitName, "'" + varName)
                | SList([ SAtom { Token = Symbol traitName }; SAtom { Token = Symbol varName } ], _) ->
                    Some (traitName, varName)
                | _ -> None)
        Some(DSignature(name, parseType tType, constraints, r))

    | SList(SAtom { Token = Symbol "import" } :: imports, _) ->
        Some(DImport(List.map parseImportForm imports, r))

    // (:alias new-name existing-name)
    //
    // `:alias` is one `Keyword` token rather than a colon and a symbol, so this
    // is not the `(: name type)` shape above and does not compete with it.
    | SList(SAtom { Token = Keyword "alias" } :: rest, _) ->
        match rest with
        | [ SAtom { Token = Symbol newName }; SAtom { Token = Symbol oldName } ] ->
            Some(DAlias(newName, oldName, r))
        | _ ->
            failwithf
                $"Invalid (:alias ...) at %s{Lexer.formatPos r}. Expected: (:alias new-name existing-name)"

    // (import/extern (write-line (: System.Console.WriteLine (-> string void))) ...)
    | SList(SAtom { Token = Symbol "import/extern" } :: clauses, _) ->
        if clauses.IsEmpty then
            failwithf $"Syntax error in import/extern at %s{Lexer.formatPos r}: it imports nothing."

        let specs =
            clauses
            |> List.map (fun c ->
                let alias, typeParams, target, opts, cr = parseForeignImportClause "import/extern" c
                checkAccessorOptions "import/extern" opts cr

                // An extern alias is a *binding*, and a binding takes no type
                // parameters: what makes one polymorphic is the signature, and
                // that is where a generic method's arguments are solved from.
                if not typeParams.IsEmpty then
                    failwithf
                        $"Syntax error in import/extern at %s{Lexer.formatPos cr}: '%s{alias}' is written applied to type parameters, and an extern alias is a function rather than a type. A generic method's type arguments come from its declared signature — write them there, as in (-> (Set %%a) %%a (Set %%a))."

                { Alias = alias
                  ClrTarget = target
                  ExplicitType = opts.ExplicitType
                  Exceptions = opts.Exceptions
                  IsAsync = opts.IsAsync
                  Uncancellable = opts.Uncancellable
                  Cancellable = opts.Cancellable
                  IsBlocking = opts.IsBlocking
                  IsGet = opts.IsGet
                  IsSet = opts.IsSet
                  Range = cr })

        Some(DImportExtern(specs, r))

    // (import/class (StreamWriter (: System.IO.StreamWriter (-> string StreamWriter))) ...)
    | SList(SAtom { Token = Symbol "import/class" } :: clauses, _) ->
        if clauses.IsEmpty then
            failwithf $"Syntax error in import/class at %s{Lexer.formatPos r}: it imports nothing."

        let specs =
            clauses
            |> List.map (fun c ->
                let alias, typeParams, target, opts, cr = parseForeignImportClause "import/class" c
                checkAccessorOptions "import/class" opts cr

                // A constructor is never a task. Silently ignoring the flag
                // would mean an import that reads as async and is not.
                if opts.IsAsync || opts.Uncancellable || opts.Cancellable then
                    failwithf
                        $"Syntax error in import/class at %s{Lexer.formatPos cr}: #:async, #:cancellable and #:uncancellable describe how a call is made, and a constructor is not made that way. They belong on an import/extern clause."

                // Constructing is not the part that waits. Whatever a
                // constructor opens, the reads and writes afterwards are where
                // a thread is parked, and those are import/extern clauses.
                if opts.IsBlocking then
                    failwithf
                        $"Syntax error in import/class at %s{Lexer.formatPos cr}: #:blocking marks a call that parks the thread it runs on, and this form declares a type and its constructor. Put it on the import/extern clause for the method that does the waiting."

                // A class is not an accessor. `import/class` declares a type and
                // its constructor, and a property of that type is imported with
                // an `import/extern` clause naming it.
                if opts.IsGet || opts.IsSet then
                    failwithf
                        $"Syntax error in import/class at %s{Lexer.formatPos cr}: #:get and #:set name an accessor for a property or field, and this form declares a type and its constructor. Write the accessor as an import/extern clause."

                { Alias = alias
                  TypeParams = typeParams
                  ClrClass = target
                  ConstructorType = opts.ExplicitType
                  Exceptions = opts.Exceptions
                  Range = cr })

        Some(DImportClass(specs, r))

    | SList(SAtom { Token = Symbol "export" } :: exports, _) ->
        // Parse items like poop-on-you
        let exportNames =
            exports
            |> List.map (function
                | SAtom { Token = Symbol e } -> e
                | _ -> failwithf $"Invalid export item at %s{Lexer.formatPos r}")

        Some(DExport(exportNames, r))

    | SList(SAtom { Token = Symbol "re-export" } :: reExports, _) ->
        let reExportNames =
            reExports
            |> List.map (function
                | SAtom { Token = Symbol e } -> e
                | _ -> failwithf $"Invalid re-export item at %s{Lexer.formatPos r}")

        Some(DReExport(reExportNames, r))

    // `parseDeclForms` rather than `parseDecl`: a nested module is a
    // declaration list like any other, so a `begin` or a macro splices inside
    // one exactly as it does at the top level.
    | SList(SAtom { Token = Symbol "module" } :: SAtom { Token = Symbol name } :: body, _) ->
        Some(DModule(name, List.collect parseDeclForms body, r))

    | SList(SAtom { Token = Symbol "def" } :: SAtom { Token = Symbol name } :: [ expr ], _) ->
        Some(DDef(name, parseExpr expr, r))

    | SList(SAtom { Token = Symbol "def" } :: SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) :: [ expr ], _) ->
        Some(DDef(name, parseExpr expr, r))

    // `(def pattern scrutinee)` — a destructuring binding. Every name the
    // pattern binds becomes a definition of its own, and the pattern has to
    // match every value of the scrutinee's type: a failure part produces the
    // value of the body the form stands in, and the top level has no body.
    // `Exhaustiveness` is what says so, once the scrutinee has a type.
    | SList(SAtom { Token = Symbol "def" } :: binder :: [ scrutinee ], _) when not (isPlainDefBinder binder) ->
        Some(DDefPattern(parsePattern binder, parseExpr scrutinee, r))

    // `:default` never leaves, so it needs no body: the pattern's one name
    // becomes a definition like any other.
    | SList(SAtom { Token = Symbol "def" } :: binder :: scrutinee :: ([ SAtom { Token = Keyword "default" }; _ ] as forms), _) ->
        match parseDefTail r binder scrutinee forms with
        | Defaults(name, bound) -> Some(DDef(name, bound, r))
        | Leaves _ -> failwith "internal error: :default read as a failure part that leaves"

    // A failure part that leaves, written where there is no body to leave.
    //
    // A REPL entry is a top level too. Binding a pattern that may fail at the
    // prompt is a thing to want and is not what this reports on; the REPL would
    // have to decide what the *rest of the session* is, and it does not.
    | SList(SAtom { Token = Symbol "def" } :: _ :: _ :: _ :: _, _) ->
        failwithf
            $"Syntax error at %s{Lexer.formatPos r}: a failure part that leaves gives the value of the body the `def` stands in, and the top level is not a body. Here a pattern has to match every value, or take `:default value` for its one name. A REPL entry is a top level as well."

    | SList(SAtom { Token = Symbol "def" } :: SList(names, _) :: [ expr ], _) ->
        let rawNames =
            names
            |> List.map (function
                | SAtom { Token = Symbol n } -> n
                | SAtom { Token = Comma } -> ""
                | _ -> failwith "Invalid tuple def")
            |> List.filter ((<>) "")
        let tupleNames =
            match rawNames with
            | "Tuple" :: restNames -> restNames
            | _ -> rawNames

        Some(DDefTuple(tupleNames, parseExpr expr, r))

    | SList(SAtom { Token = Symbol "def/mutable" } :: SAtom { Token = Symbol name } :: [ expr ], _) ->
        Some(DDefMutable(name, parseExpr expr, r))

    | SList(SAtom { Token = Symbol "def/mutable" } :: SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) :: [ expr ], _) ->
        Some(DDefMutable(name, parseExpr expr, r))

    | SList(SAtom { Token = Symbol(("defun" | "defbjo") as definer) } :: SList(SAtom { Token = Symbol name } :: args, _) :: rest, _) ->
        // The function only. A definition that carries its own types also makes
        // a signature, and one `Decl` has nowhere to put it — see
        // `tryParseDeclGroup`, which is what every caller that compiles a
        // module goes through.
        parseDefunDecl definer name args rest r
        |> List.tryPick (function
            | DDefun _ as d -> Some d
            | _ -> None)
    | SList(SAtom { Token = Symbol "defbjouble" } :: SList(SAtom { Token = Symbol name } :: args, _) :: clauses, _) ->
        let where = Lexer.formatPos r

        // The two bodies, by keyword rather than by position. Order-independent
        // because there is no reason for it not to be, and because `#:sync`
        // first reads better in some pairs and `#:bjo` first in others.
        let clauseNamed (want: string) =
            clauses
            |> List.tryPick (function
                | SList(SAtom { Token = Keyword k } :: body, cr) when k = want -> Some(parseBody body cr)
                | _ -> None)

        for c in clauses do
            match c with
            | SList(SAtom { Token = Keyword("sync" | "bjo") } :: _, _) -> ()
            | _ ->
                failwithf
                    $"Syntax error in defbjouble '%s{name}' at %s{where}: every clause is (#:sync body...) or (#:bjo body...), and this is neither."

        match clauseNamed "sync", clauseNamed "bjo" with
        | Some syncBody, Some bjoBody -> Some(DDefDouble(name, parseDefunArgs args, syncBody, bjoBody, r))
        | None, _ ->
            failwithf
                $"Syntax error in defbjouble '%s{name}' at %s{where}: it has no (#:sync ...) body. A defbjouble is written when the two colours call *different* .NET methods, so both halves have to be here. If there is only one implementation, this is a defun."
        | _, None ->
            failwithf
                $"Syntax error in defbjouble '%s{name}' at %s{where}: it has no (#:bjo ...) body. A defbjouble is written when the two colours call *different* .NET methods, so both halves have to be here. If there is only one implementation, this is a defun."

    | SList(SAtom { Token = Symbol "type" } :: typeDefs, _) -> Some(DType(List.map parseTypeDef typeDefs, r))

    | SList(SAtom { Token = Symbol "type-rec" } :: typeDefs, _) -> Some(DTypeRec(List.map parseTypeDef typeDefs, r))

    | SList (SAtom { Token = Symbol "def/trait" } ::
             SList (SAtom { Token = Symbol traitName } :: [ implementorSpec ], _) ::
             body, r) ->

        // `(Show %c)` declares an implementor of arity 0 — an interface trait.
        // `(Monad (%m %a))` writes it applied, which is what no C# interface can
        // express and what makes the trait inline-only.
        let implementorVar, holeArity =
            match implementorSpec with
            | SAtom { Token = QuotedSymbol v } -> v, 0
            | SList (SAtom { Token = QuotedSymbol v } :: holeArgs, hr) ->
                if holeArgs.IsEmpty then
                    failwithf
                        $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos hr}: (%%%s{v}) applies the implementor to nothing. Write %%%s{v} instead."
                for a in holeArgs do
                    match a with
                    | SAtom { Token = QuotedSymbol _ } -> ()
                    | _ ->
                        failwithf
                            $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos hr}: the implementor may only be applied to type variables."
                v, holeArgs.Length
            | _ ->
                failwithf
                    $"Syntax error in def/trait '%s{traitName}': expected (%s{traitName} %%c) or (%s{traitName} (%%m %%a))."

        let mutable assocTypes = []
        let mutable signatures = []
        let mutable defaults = []
        let mutable clrConstraint = None
        let mutable clrMembers = []

        for item in flattenBegins body do
            match item with
            // Match: (#:clr-constraint (System.Numerics.INumber %a))
            //
            // A list headed by the keyword rather than a keyword followed by
            // one, so that reading it needs no lookahead and the `def/trait`
            // head — which is matched at exactly two elements — is untouched.
            // `(#:name type)` in an arrow type is the same shape.
            | SList (SAtom { Token = Keyword "clr-constraint" } :: rest, cr) ->
                if clrConstraint.IsSome then
                    failwithf
                        $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos cr}: a trait stands for at most one .NET interface."

                match rest with
                | [ SList (SAtom { Token = Symbol ifaceName } :: ifaceArgs, _) ] ->
                    clrConstraint <- Some(ifaceName, ifaceArgs |> List.map parseType)
                // A non-generic interface may be written bare, since there are
                // no arguments to parenthesize it around.
                | [ SAtom { Token = Symbol ifaceName } ] -> clrConstraint <- Some(ifaceName, [])
                | _ ->
                    failwithf
                        $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos cr}: #:clr-constraint takes one fully qualified .NET interface, applied to the arguments it is implemented at, as in (#:clr-constraint (System.Numerics.INumber %%%s{implementorVar}))."

            // Match: (type 'item)
            | SList (SAtom { Token = Symbol "type" } :: SAtom { Token = QuotedSymbol assocName } :: [], _) ->
                assocTypes <- assocName :: assocTypes

            // Match: (: methodName signatureExpr #:clr-member Abs)
            //
            // Which member of the interface this method is. Only meaningful on
            // a trait that stands for one, and checked against the interface at
            // inference; here it is only read.
            | SList (SAtom { Token = Colon } :: SAtom { Token = Symbol methodName } :: typeExpr
                     :: SAtom { Token = Keyword "clr-member" } :: SAtom { Token = Symbol memberName } :: [], _) ->
                signatures <- (methodName, parseType typeExpr, []) :: signatures
                clrMembers <- (methodName, memberName) :: clrMembers

            // Match: (: methodName signatureExpr (where ...)) — a member with
            // constraints of its own, each associated type pinned by keyword.
            // See `MemberConstraint` and `parseMemberWhere`.
            | SList (SAtom { Token = Colon } :: SAtom { Token = Symbol methodName } :: typeExpr
                     :: SList (SAtom { Token = Symbol "where" } :: constraintExprs, wr) :: [], _) ->
                signatures <- (methodName, parseType typeExpr, parseMemberWhere traitName constraintExprs wr) :: signatures

            // Match: (: methodName signatureExpr)
            | SList (SAtom { Token = Colon } :: SAtom { Token = Symbol methodName } :: typeExpr :: [], _) ->
                signatures <- (methodName, parseType typeExpr, []) :: signatures

            // Match: (defun (methodName args...) body) — a default body, used by
            // any impl that does not write this method itself. The signature is
            // still declared separately: a default supplies the *body*, and the
            // type it is checked at comes from the impl, not from here.
            | SList (SAtom { Token = Symbol "defun" } :: _, _) as defunExpr ->
                defaults <- parseDecl defunExpr :: defaults

            // A suspending default body, for a method the trait declares
            // `-bjo->`. A default is spliced into each impl as source and
            // checked there, so its definer meets the trait's arrow by the same
            // route a hand-written method's does, and needs no check here.
            | SList (SAtom { Token = Symbol "defbjo" } :: _, _) as defbjoExpr ->
                defaults <- parseDecl defbjoExpr :: defaults

            // A macro, which is how a `derive`-style transformer writes a
            // default body. It goes through `parseDeclForms` so that it gets
            // resolution and the bound set on the same terms a top-level splice
            // does; what comes back has to be method bodies, since a trait has
            // nowhere to put anything else.
            | SList(SAtom { Token = Symbol h } :: _, mr) when isMacroName h ->
                for d in parseDeclForms item do
                    match d with
                    | DDefun _ -> defaults <- d :: defaults
                    | other ->
                        failwithf
                            $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos mr}: the macro '%s{h}' produced %s{declKindName other}, and a trait body holds (type ...), (: ...) and (defun ...) — a default method body is the only one of those a macro can write."

            | _ ->
                failwithf
                    $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos (getRange item)}: Expected (type ...), (: ...), (defun ...), (#:clr-constraint ...), a (begin ...) of those, or a macro producing default method bodies."

        let clrSpec =
            clrConstraint
            |> Option.map (fun (ifaceName, ifaceArgs) -> ifaceName, ifaceArgs, List.rev clrMembers)

        Some(DTrait(traitName, implementorVar, holeArity, List.rev assocTypes, List.rev signatures, List.rev defaults, clrSpec, r))

    // Parse: (impl (TraitName (Vec 'a)) (type 'item 'a) (defun (get v i) ...))
    //
    // The trait name is stripped of a rename, as every dispatched-on head is: a
    // template that writes `(impl (Eq ,name) ...)` — which is what a
    // `derive` macro does — constructs the trait name and so has it renamed,
    // and a trait is not a binding for the mark to be resolving.
    | SList (SAtom { Token = Symbol "impl" } ::
             SList (StrippedSymbol traitName :: targetTypeExpr :: [], _) ::
             body, r) ->

        let targetType = parseType targetTypeExpr

        let mutable assocBindings = []
        let mutable constraints = []
        let mutable methodWheres = []
        let mutable methods = []

        for item in flattenBegins body do
            // The clause tags — `type`, `where`, `defun` — are dispatched on
            // exactly as a declaration's head is, and a constructed one arrives
            // renamed. The *method name* inside a `defun` is stripped with them:
            // it has to match what the `def/trait` declared, so it is a
            // selector rather than a binding.
            match stripMethodName (stripHeadMark item) with
            // Match: (type 'item targetType)
            | SList (SAtom { Token = Symbol "type" } :: SAtom { Token = QuotedSymbol assocName } :: boundTypeExpr :: [], _) ->
                assocBindings <- (assocName, parseType boundTypeExpr) :: assocBindings

            // Match: (where (TraitName %var) ...) — a conditional impl. The
            // clause is read exactly as a signature's, so the two spellings
            // cannot drift.
            | SList (SAtom { Token = Symbol "where" } :: constraintExprs, wr) ->
                for c in constraintExprs do
                    match c with
                    | SList ([ StrippedSymbol cTrait; SAtom { Token = QuotedSymbol varName } ], _) ->
                        constraints <- (cTrait, "'" + varName) :: constraints
                    | SList ([ StrippedSymbol cTrait; SAtom { Token = Symbol varName } ], _) ->
                        constraints <- (cTrait, varName) :: constraints
                    | _ ->
                        failwithf
                            $"Syntax error in impl for '%s{traitName}' at %s{Lexer.formatPos wr}: a where clause holds (TraitName %%var) constraints, and the variable must be one the impl's own target names."

            // Match: (: methodName (where ...)) — the impl-side repetition of
            // a member-level where clause, required beside a hand-written
            // body for a constrained member. See `DImpl`.
            | SList (SAtom { Token = Colon } :: SAtom { Token = Symbol methodName }
                     :: SList (SAtom { Token = Symbol "where" } :: constraintExprs, wr) :: [], _) ->
                methodWheres <- (methodName, parseMemberWhere traitName constraintExprs wr) :: methodWheres

            // Match: (defun ...)
            | SList (SAtom { Token = Symbol "defun" } :: _, _) as defunExpr ->
                methods <- parseDecl defunExpr :: methods

            // A method that suspends, which the trait's signature has to have
            // declared `-bjo->`. The definer is checked against that arrow in
            // `DImpl` rather than here: this is the only place an impl method
            // names a colour, and the trait it belongs to is not in scope until
            // inference.
            | SList (SAtom { Token = Symbol "defbjo" } :: _, _) as defbjoExpr ->
                methods <- parseDecl defbjoExpr :: methods

            // The `derive` case: one macro call standing for the methods of a
            // whole implementation. See the matching arm in `def/trait`.
            | SList(SAtom { Token = Symbol h } :: _, mr) when isMacroName h ->
                for d in parseDeclForms item do
                    match d with
                    | DDefun _ -> methods <- d :: methods
                    | other ->
                        failwithf
                            $"Syntax error in impl for '%s{traitName}' at %s{Lexer.formatPos mr}: the macro '%s{h}' produced %s{declKindName other}, and an implementation holds methods."

            | _ ->
                failwithf
                    $"Syntax error in impl for '%s{traitName}' at %s{Lexer.formatPos (getRange item)}: Expected (type ...), (where ...), (defun ...), a (begin ...) of those, or a macro producing methods."

        Some(
            DImpl(
                traitName,
                targetType,
                List.rev assocBindings,
                List.rev constraints,
                List.rev methodWheres,
                List.rev methods,
                r
            )
        )

    // Parse: (impl/extern (Foldable (Vec 'a)) (type 'item 'a))
    //
    // The bodyless counterpart of `impl`, emitted into a library's export
    // metadata so that whoever imports it can resolve the trait's associated
    // types and dispatch to the impl class compiled into that assembly.
    | SList (SAtom { Token = Symbol "impl/extern" } ::
             SList (SAtom { Token = Symbol traitName } :: targetTypeExpr :: [], _) ::
             body, r) ->

        let assocBindings =
            body
            |> List.choose (function
                | SList (SAtom { Token = Symbol "type" } :: SAtom { Token = QuotedSymbol assocName } :: boundTypeExpr :: [], _) ->
                    Some(assocName, parseType boundTypeExpr)
                | SList (SAtom { Token = Symbol "where" } :: _, _) -> None
                | _ -> failwithf $"Syntax error in impl/extern for '%s{traitName}': Expected (type ...) or (where ...).")

        // A conditional impl's `(where ...)` has to cross the module boundary
        // with it: the importing side is where the dictionary for
        // `(->str (List int))` is built, and it cannot build one without knowing
        // that a `(->str int)` goes inside.
        let constraints =
            body
            |> List.collect (function
                | SList (SAtom { Token = Symbol "where" } :: constraintExprs, _) ->
                    constraintExprs
                    |> List.choose (function
                        | SList ([ SAtom { Token = Symbol cTrait }; SAtom { Token = QuotedSymbol varName } ], _) ->
                            Some(cTrait, "'" + varName)
                        | SList ([ SAtom { Token = Symbol cTrait }; SAtom { Token = Symbol varName } ], _) ->
                            Some(cTrait, varName)
                        | _ -> None)
                | _ -> [])

        Some(DImplExtern(traitName, parseType targetTypeExpr, assocBindings, constraints, r))

    // Not a declaration form. It may still be a macro call, which is
    // `parseDeclForms`' business — reached only once every form above has
    // failed to match, so a special form always wins over a macro of the same
    // name.
    | _ -> None

/// One declaration, where there has to be exactly one.
///
/// `Pipeline.importsOf` is the caller this exists for: it reads an `(import
/// ...)` form before anything is expanded, wants the single declaration back,
/// and can never be looking at a macro.
and parseDecl (s: SExpr) : Decl =
    match tryParseDecl s with
    | Some d -> d
    | None -> failwithf $"Unknown declaration at %s{Lexer.formatPos (getRange s)}"

/// Every declaration one form makes, or `None` when nothing matched it.
///
/// `tryParseDecl` answers with one because most callers want one. A `defun`
/// that carries its own parameter and return types makes two — the signature
/// and the function — and anything deciding what a form *declares* has to see
/// both. `parseDeclForms` is one such caller; the REPL, which asks what an
/// entry defines and whether it still needs a signature written for it, is the
/// other.
and tryParseDeclGroup (s: SExpr) : Decl list option =
    match stripHeadMark s with
    | SList(SAtom { Token = Symbol(("defun" | "defbjo") as definer) } :: SList(SAtom { Token = Symbol name } :: args, _) :: rest, _) ->
        Some(parseDefunDecl definer name args rest (getRange s))

    // `(def* (pattern scrutinee) ...)` at the top level *is* its clauses
    // written one after another, so each one is handed back to `tryParseDecl`
    // as the `def` it would have been.
    //
    // Nothing is lost by splicing here. A clause can only fail to match if it
    // carries a failure part, which the top level refuses; with none, the
    // ordering `def*` gives inside a body is the declaration order it has here,
    // and a later clause reads an earlier clause's binders because module-level
    // definitions see one another anyway.
    | SList(SAtom { Token = Symbol "def*" } :: clauseForms, r) ->
        if clauseForms.IsEmpty then
            failwithf
                $"Syntax error at %s{Lexer.formatPos r}: expected (def* (pattern scrutinee) ...). A def* clause is always parenthesised, including when there is only one."

        let decls =
            clauseForms
            |> List.map (fun clause ->
                let cr = getRange clause

                match clause with
                | SList((_ :: _ :: _) as clauseParts, _) ->
                    let asDef = SList(SAtom { Token = Symbol "def"; Range = cr } :: clauseParts, cr)

                    match tryParseDecl asDef with
                    | Some d -> d
                    | None ->
                        failwithf
                            $"Syntax error at %s{Lexer.formatPos cr}: a def* clause is written (pattern scrutinee)."
                | _ ->
                    failwithf
                        $"Syntax error at %s{Lexer.formatPos cr}: a def* clause is written (pattern scrutinee).")

        // The clauses land in one scope here as they do in a body, so a name
        // bound by two of them is refused rather than taken from the later one.
        let bound (d: Decl) =
            match d with
            | DDef(n, _, _) -> [ n ]
            | DDefTuple(ns, _, _) -> ns
            | DDefPattern(pattern, _, _) -> patternBinders pattern
            | _ -> []

        decls
        |> List.collect bound
        |> List.countBy id
        |> List.filter (fun (_, n) -> n > 1)
        |> List.map fst
        |> function
            | [] -> ()
            | repeated ->
                let names = String.concat ", " repeated

                failwithf
                    $"Syntax error at %s{Lexer.formatPos r}: %s{names} is bound by more than one clause of this def*, and all of them are in scope below it. Bind it once."

        Some decls

    // `(: name #:sync ...)`, in the slot `#:opaque` uses on a type: a marker on
    // the declaration rather than any part of the shape.
    //
    // The rest is handed back to `tryParseDecl` with the marker taken out, so
    // every signature form there is — with a `(where ...)` and without — keeps
    // working here without being spelled a second time.
    | SList(SAtom { Token = Colon } as colon :: (SAtom { Token = Symbol name } as named) :: SAtom { Token = Keyword "sync" } :: rest, r) ->
        match tryParseDecl (SList(colon :: named :: rest, r)) with
        | Some signature -> Some [ signature; DSyncOnly(name, r) ]
        | None ->
            failwithf
                $"Invalid signature for '%s{name}' at %s{Lexer.formatPos r}. #:sync goes straight after the name, as in (: %s{name} #:sync (-> ...))."

    | _ -> tryParseDecl s |> Option.map List.singleton

/// The declarations one top-level form expands to.
///
/// Three ways there can be more than one. `def/macro` is not a new binding
/// form: it is a signature the compiler writes, an ordinary `defun`, and a note
/// that the name is a macro. `(begin ...)` splices its contents into the
/// enclosing declaration list. And a macro call in declaration position becomes
/// whatever it expanded to, which is what makes the first two worth having —
/// a transformer can now emit a signature beside its `defun`, or a `def` beside
/// the function that reads it.
///
/// `(begin)` with nothing in it splices to nothing, deliberately: a macro has
/// to be able to decide that this call produces no declarations at all. Body
/// position reads an empty `begin` differently, and says why there.
///
/// The signature `def/macro` writes is not the user's to choose. A transformer
/// is invoked by reflection against a signature the expander has to know
/// exactly, and every way of varying it — a type parameter, a trait constraint,
/// a keyword or rest argument, a `defbjo` colour — changes the emitted C# method
/// (`T_` parameters, leading `_dict_*`, `__kw_*`, a `Fiber<T>` return). Fixing
/// it here is what turns those into a syntax error rather than a
/// `TargetParameterCountException` from inside the compiler.
and parseDeclForms (s: SExpr) : Decl list =
    // Before the match, because a `begin` a template wrote arrives as
    // `begin__37` and would otherwise be read as a macro call to something
    // that does not exist.
    let s = stripHeadMark s

    match s with
    // `def/pattern` is the same declaration in the other table. Its transformer
    // is invoked when the name heads a *pattern*, and what it answers is read
    // by `parsePattern` — so it is tried before the constructor fallback and
    // shadows a constructor of the same name in pattern position.
    //
    // Hygiene is the expander's and needs nothing here, with one consequence
    // worth stating: a binder the template writes arrives renamed, so it is not
    // the name the clause body can read. A pattern macro that binds a user's
    // name has to take that name out of the input form.
    //
    // `def/hash-extend` is the third table. Its transformer `defun` is named
    // `#name` — a spelling no source identifier can have, so the prelude's
    // `#map` sits beside its `map`. `DHashMacro` carries the bare name, which
    // is what the table is keyed on and what an import modifier prefixes.
    | SList(SAtom { Token = Symbol(("def/macro" | "def/pattern" | "def/hash-extend") as definer) } :: SList(head, _) :: body,
            r) ->
        let name, argNames =
            match head with
            | SAtom { Token = Symbol name } :: rest ->
                let args =
                    rest
                    |> List.map (function
                        | SAtom { Token = Symbol a } -> a
                        | bad ->
                            failwithf
                                $"Invalid %s{definer} parameter at %s{Lexer.formatPos (getRange bad)}. A transformer takes exactly three plain parameters: the form, inject and compare.")

                name, args
            | _ ->
                failwithf
                    $"Invalid %s{definer} at %s{Lexer.formatPos r}. Expected (%s{definer} (name form inject compare) body...)"

        if argNames.Length <> 3 then
            failwithf
                $"Invalid %s{definer} '%s{name}' at %s{Lexer.formatPos r}: a transformer takes exactly three parameters — the form, inject and compare — and this one takes %d{argNames.Length}."

        if body.IsEmpty then
            failwithf $"Invalid %s{definer} '%s{name}' at %s{Lexer.formatPos r}: it has no body."

        let transformerName, marker =
            match definer with
            | "def/macro" -> name, DMacro(name, r)
            | "def/pattern" -> name, DPatternMacro(name, r)
            | _ -> "#" + name, DHashMacro(name, r)

        [ DSignature(transformerName, macroTransformerType r, [], r)
          DDefun(
              transformerName,
              argNames |> List.map (fun n -> MandatoryArg(n, None)),
              parseBody body r,
              Ordinary,
              r
          )
          marker ]

    | SList(SAtom { Token = Symbol(("def/macro" | "def/pattern" | "def/hash-extend") as definer) } :: _, r) ->
        failwithf
            $"Invalid %s{definer} at %s{Lexer.formatPos r}. Expected (%s{definer} (name form inject compare) body...)"

    // Each step removes one wrapper, so the form count strictly decreases and
    // arbitrary nesting flattens.
    | SList(SAtom { Token = Symbol "begin" } :: inner, _) -> List.collect parseDeclForms inner

    | other ->
        match tryParseDeclGroup other with
        | Some decls -> decls
        | None ->
            // A macro in declaration position. Unlike the expression and body
            // positions, this one used to drop `Resolve` entirely — so a
            // transformer calling a helper from its own module emitted the
            // helper's fresh spelling and rules 2 and 3 never ran.
            //
            // Nested expansions resolve inside-out, which is what we want: an
            // inner macro's memo names are gensyms the outer macro's map has
            // never heard of, so the outer `Resolve` leaves them alone. The
            // bound set covers the whole flattened group, including anything an
            // inner expansion contributed to it.
            match expandHook other with
            | Some expansion ->
                let decls = parseDeclForms expansion.Form
                rejectSplicedImports decls (getRange other)
                decls |> List.map (mapDeclExprs (expansion.Resolve(boundNames decls)))
            | None -> failwithf $"Unknown declaration at %s{Lexer.formatPos (getRange other)}"

/// Parses a module's top-level forms, collecting a failure per form.
///
/// The forms are independent of one another, and the reader has already decided
/// where each of them ends, so a form that fails to parse costs the
/// declarations it would have produced and nothing else. There is no token
/// stream left to resynchronise — this is a Lisp, and the boundaries are the
/// parentheses.
///
/// What a caller gets back is therefore a partial module whenever anything was
/// dropped. `Diagnostics.hasErrors` is what says so, and the gate in
/// `Pipeline.runFullFrontendPipeline` is what stops the partial result from
/// being type checked.
let parseModule (exprs: SExpr list) : Decl list =
    exprs
    |> List.collect (fun form -> Diagnostics.recover "parse" (Some(getRange form)) [] (fun () -> parseDeclForms form))
