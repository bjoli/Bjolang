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

module Bjolang.Inference

open Bjolang.Lexer
open Bjolang.Ast
open Bjolang.TypedAST
open Bjolang.Unification
open Bjolang.TypeEnv
open Bjolang.Annotations
open Bjolang.Traits
open Bjolang.ForeignTyping
open Bjolang.InferExpr
open Bjolang.CheckDecl

// --- PIPELINE COORDINATION ---
/// Runs Hindley-Milner inference over a parsed program.
///
/// The result still contains high-level `TMatch` nodes: pattern matching is
/// translated straight to C# patterns by the code generator, and trait dispatch
/// is resolved afterwards by `Bjolang.Lowering`.
///
/// `Pipeline.loadModuleGraph` runs every file through `wrapInModule`, so in
/// practice `program` is a list of `DModule`s and the real work happens one
/// level down. The declarations are still handed to `checkDeclGroup` directly
/// rather than assumed to be wrapped, so a bare list type-checks the same way.
/// A module-level value has to have a type C# can write down.
///
/// It compiles to a static field of the module's class, and a static field
/// cannot be generic: there is no class for the type parameter to belong to. So
/// a value whose type is still open once the whole program has been checked has
/// nowhere to live. `(def bleh '())` is the honest example — perfectly sound,
/// `Nil` is a value and generalizing it is fine, it simply cannot be emitted.
///
/// A `defun` is exempt. A generic *method* is ordinary C#, and that is what a
/// polymorphic top-level function becomes.
///
/// Run at the very end rather than at each declaration, because a type left open
/// by its own initializer may still be pinned down by a later one — a
/// `(def/mutable pending Nil)` written to further down the module is settled by
/// the time this looks.
let private checkModuleValuesAreConcrete (registry: TraitRegistry) (decls: TDecl list) : unit =
    let check (form: string) (name: string) (t: HMType) (r: Range) =
        // Both halves of "open": a variable generalization quantified, and a
        // metavariable nothing ever resolved. Either one leaves the code
        // generator with a type it cannot name.
        let isOpen =
            not (List.isEmpty (freeTVars registry t)) || not (List.isEmpty (freeVars registry t))

        if isOpen then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: the type of '%s{name}' is still open, and a module-level %s{form} compiles to a static field — which cannot be generic, because there is no class for the type parameter to belong to. Give it a signature that pins it down, like (: %s{name} (List int)), or make it a function, whose type parameters do have somewhere to live."

    let rec go (ds: TDecl list) =
        for d in ds do
            match d with
            | TModule(_, inner, _) -> go inner
            | TDef(name, _, t, r) -> check "def" name t r
            | TDefMutable(name, _, t, r) -> check "def/mutable" name t r
            | TDefTuple(names, _, t, r) -> check "def" (String.concat ", " names) t r
            // Per binder rather than over the scrutinee: each one is a field of
            // its own, and it is the one whose type is still open that the
            // reader has to pin down.
            | TDefPattern(_, _, binders, r) -> for name, t in binders do check "def" name t r
            | _ -> ()

    go decls

/// Reports a top-level definition that binds over a trait method.
///
/// Legal, and since a method name became an ordinary binding it is also silent:
/// the definition wins for the whole module and nothing says so. What it almost
/// always means is `impl` — somebody writing `(defun (= a b) ...)` is
/// implementing equality rather than defining a function called `=`. The case
/// that prompted this was `sign`, a `Num` method by `#:clr-member`: a program's
/// own `(defun (sign n) ...)` used to be silently dead, and its call sites
/// reported an arity error against the programmer's own line.
///
/// Top level only. A parameter or a `let` is a local decision in a scope its
/// reader can see the whole of — `lib/std/fmt.bjo` binds a `start` — and warning
/// about one would be noise. A module-level binding is visible from everywhere
/// in the file, which is what makes it worth a word.
///
/// A `impl`'s methods are not reached: they sit inside `TImpl`, and this
/// descends through `TModule` and nothing else.
let private warnAboutShadowedMethods (registry: TraitRegistry) (decls: TDecl list) : unit =
    // Only warn the user if they are shadowing a method and *exporting* it. Shadowing a method privately is fine, and warning about it would be unhelpful.
    let exported =
        let names = System.Collections.Generic.HashSet<string>()

        let rec scan (ds: TDecl list) =
            for d in ds do
                match d with
                | TModule(_, inner, _) -> scan inner
                | TExport(ns, _) -> for n in ns do names.Add n |> ignore
                | _ -> ()

        scan decls
        names

    let check (form: string) (name: string) (r: Range) =
        match Map.tryFind name registry.TraitMethods with
        | Some traitName ->
            let reach =
                if exported.Contains name then
                    ", and so does one in any module that imports it"
                else
                    ". It is not exported, so no other module is affected"

            Diagnostics.warn
                $"'%s{name}' is a method of the trait '%s{traitName}', and this %s{form} binds over it at %s{Lexer.formatPos r}. A call to '%s{name}' written in this module reaches the definition rather than dispatching%s{reach}. To implement the method for a type of your own, write (impl (%s{traitName} YourType) (defun (%s{name} ...) ...))."
        | None -> ()

    let rec go (ds: TDecl list) =
        for d in ds do
            match d with
            | TModule(_, inner, _) -> go inner
            | TDefun(name, _, _, _, _, _, _, _, r) -> check "definition" name r
            | TDef(name, _, _, r) -> check "definition" name r
            | TDefMutable(name, _, _, r) -> check "mutable definition" name r
            | _ -> ()

    go decls

/// Every binding whose scheme quantifies a type variable that no *parameter*
/// mentions. See `TraitRegistry.ReturnOnlyGenerics` for what reads it.
///
/// A variable is compared with its quote stripped, because the two spellings
/// are both in use: `Prelude` writes `Scheme(["a"], …, TVar "a")` and a
/// signature's `%a` arrives as `'a`.
let private returnOnlyGenerics (env: Env) : Set<string> =
    let bare (v: string) = v.TrimStart '\''

    let rec typeVars (t: HMType) : Set<string> =
        match t with
        | TVar name -> Set.singleton (bare name)
        | TFun(args, ret, _) -> Set.unionMany (typeVars ret :: List.map typeVars args)
        | TCon(_, args)
        | TTuple args -> args |> List.fold (fun acc a -> Set.union acc (typeVars a)) Set.empty
        | TMeta m ->
            match m.Value with
            | Some inner -> typeVars inner
            | None -> Set.empty
        | TAssoc(_, _, implementor) -> typeVars implementor

    env.Bindings
    |> Map.toSeq
    |> Seq.choose (fun (name, binding) ->
        let (Scheme(vars, _, scheme)) = binding.Scheme

        match Unification.prune env.Registry scheme with
        | TFun(args, _, _) ->
            let inferable = args |> List.fold (fun acc a -> Set.union acc (typeVars a)) Set.empty

            if vars |> List.exists (fun v -> not (Set.contains (bare v) inferable)) then
                Some name
            else
                None
        | _ -> None)
    |> Set.ofSeq

/// Checks declarations that were generated *after* `checkProgram` returned.
///
/// `Monomorphise` builds a specialised copy of a constrained function as source
/// and hands it back here to be checked, because a copy made by substituting
/// into the checked tree would have to rewrite every `HMType` on every node and
/// re-decide `TraitRef.Resolved` at every trait call. Re-checking source cannot
/// miss one, which is the same argument `expandPolymorphicDefuns` makes.
///
/// The whole-program checks `checkProgram` runs afterwards are deliberately not
/// repeated. `checkModuleValuesAreConcrete` and `warnAboutShadowedMethods` are
/// about what was written, and a generated copy would report the same thing
/// twice under a name nobody wrote; `ReturnOnlyGenerics` is already settled for
/// every name a copy could mention.
let checkAddendum (initialEnv: Env) (decls: Decl list) : Env * TDecl list =
    let env, _, typedDecls = checkDeclGroup initialEnv Map.empty decls
    solvePending env
    env, typedDecls

let checkProgram (initialEnv: Env) (program: Decl list) : Env * TDecl list =
    let finalEnv, _, typedDecls = checkDeclGroup initialEnv Map.empty program
    // Anything raised outside a declaration that generalizes still has to be
    // answered for.
    solvePending finalEnv
    checkModuleValuesAreConcrete finalEnv.Registry typedDecls
    warnAboutShadowedMethods finalEnv.Registry typedDecls

    // After everything, because it reads the bindings as they finally are: a
    // definition's scheme is not settled until its group has generalized.
    let finalEnv =
        { finalEnv with
            Registry = { finalEnv.Registry with ReturnOnlyGenerics = returnOnlyGenerics finalEnv } }

    finalEnv, typedDecls
