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

/// The two source-to-source passes that give a definition its second colour.
///
/// Both rewrite a `Decl list` before any of it is checked, and both do it as
/// *source* rather than by copying a checked tree: a twin's parameters change
/// colour, and an `Effect` lives in every type on every node of a body, so a
/// substitution after the fact would be a type-level traversal whose every
/// miss is silent. Re-checking the same source against a repainted signature
/// cannot miss one.
module Bjolang.ColourTwins

open Lexer
open Bjolang.Ast
open Bjolang.TypedAST
open Bjolang.ForeignTyping

/// Every `defun` whose signature declares a `-?->` parameter gets a second
/// definition, generated from the same body and checked at the suspending
/// colour. This is monomorphisation: `-?->` promises two copies, and this
/// is where the one that was not written is made.
///
/// Generated as *source*, before anything is checked, rather than by
/// copying the checked tree. The twin's parameters have to change colour,
/// and an `Effect` lives in every type on every node of a body, so a
/// substitution after the fact would be a new type-level traversal — and
/// every occurrence it missed would be silent, because `Codegen` grounds a
/// stray `EPoly` to `ESync` and emits an ordinary body where a suspending
/// one was meant. Re-checking the same source against a repainted
/// signature cannot miss one. It is also what `defbjouble` already does,
/// with the difference that there the second body is written by hand.
///
/// Both copies are made whether or not the suspending one is used. The set
/// is not knowable from this module alone — an importer's call site is the
/// other place it could be demanded from — and the published `-?->` in the
/// signature is what tells that importer the twin exists.
let expandPolymorphicDefuns (decls: Decl list) : Decl list =
    let parameters (ftype: FType) =
        match ftype with
        | TArrow(mandatory, keywords, restOpt, _, _, _) ->
            mandatory @ (keywords |> List.map snd) @ (restOpt |> Option.toList)
        | _ -> []

    let declaresPoly (ftype: FType) =
        parameters ftype
        |> List.exists (function
            | TApp("-?->", _, _) -> true
            | _ -> false)

    let signatures =
        decls
        |> List.choose (function
            | DSignature(name, ftype, constraints, r) -> Some(name, (ftype, constraints, r))
            | _ -> None)
        |> Map.ofList

    decls
    |> List.collect (fun d ->
        match d with
        // Either definer. A `defbjo` declaring a `-?->` parameter needs the
        // pair for exactly the reason a `defun` does — the two copies take
        // `Func<A,B>` and `Func<A,Fiber<B>>`, which are unrelated C# types
        // — and the outer arrow is not what differs between them, so the
        // twin keeps the colour the original was written with.
        //
        // Skipping `defbjo` here did not reject the declaration, it emitted
        // one body: the parameter was spelled `Func<A,B>` and the call to it
        // awaited, so the program failed in Roslyn with "'int' does not
        // contain a definition for 'GetAwaiter'". A hole rather than a
        // limitation, and the sort that only shows up in generated C#.
        | DDefun(name, args, body, colour, r) ->
            match Map.tryFind name signatures with
            | Some(ftype, constraints, sigRange) when declaresPoly ftype ->
                // A signature of its own, so that the twin is an ordinary
                // definition from here on: `explicitSigs` reads it,
                // `declaredFunctions` binds it, and exporting and metadata
                // need no case for it.
                //
                // The twin is always `Suspending`: a `-?->` instantiated at
                // the suspending colour means the callback awaits, so the
                // body that calls it has to be able to. For an `Ordinary`
                // original that is a recolour; for a `defbjo` it is what it
                // already was.
                [ d
                  DSignature(Naming.suspendingCopy name, suspendingSignature ftype, constraints, sigRange)
                  DDefun(Naming.suspendingCopy name, args, body, Suspending, r) ]
            | _ -> [ d ]
        | _ -> [ d ])

/// Every `defun` that can *reach* a `defbjouble` gets a suspending copy as
/// well, whether or not it says so.
///
/// This is the half of monomorphisation nobody declares. `-?->` is written
/// down because it changes what a procedure accepts; this changes nothing
/// about the type, only which of two bodies a bjoroutine calls, so there is
/// nothing for an author to say and no reason to make them say it. It is
/// what lets a `defun` that mentions no colour suspend when a bjoroutine
/// calls it, which is the whole point of the exercise.
///
/// The graph is over *names*, on the untyped declarations, because
/// generation has to happen before inference — a copy made afterwards would
/// need its effects substituted into every type on every node, which is the
/// mistake `expandPolymorphicDefuns` exists to avoid. `EffectGraph` builds
/// the real graph, and cannot be used here: it needs types to know what a
/// yield point is.
///
/// So this over-approximates, in the two ways that are safe. A free name is
/// counted as a call even when the value is only passed around, and a call
/// *through* a parameter is not counted at all — the same approximation the
/// blocking lint already makes. Both cost at most a copy nobody calls;
/// neither can produce a wrong one, because a copy is only ever *chosen* by
/// `selectDoubles`, which reads the real types.
let expandReachingDefuns (registry: TraitRegistry) (decls: Decl list) : Decl list * Map<string, string> =
    let signatures =
        decls
        |> List.choose (function
            | DSignature(name, ftype, constraints, r) -> Some(name, (ftype, constraints, r))
            | _ -> None)
        |> Map.ofList

    /// The names their signature marked `#:sync`.
    ///
    /// `#:sync` cannot be applied to definitions that explicitly declare their 
    /// own color (e.g., `defbjo` or `defbjouble`), as it would create 
    /// conflicting constraints regarding the generation of a second body.
    let syncOnly =
        let marked =
            decls
            |> List.choose (function
                | DSyncOnly(name, r) -> Some(name, r)
                | _ -> None)

        for (name, r) in marked do
            let definer =
                decls
                |> List.tryPick (function
                    | DDefun(n, _, _, Suspending, _) when n = name -> Some "defbjo"
                    | DDefDouble(n, _, _, _, _) when n = name -> Some "defbjouble"
                    | _ -> None)

            match definer with
            | Some "defbjouble" ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: '%s{name}' is written #:sync, but it is a defbjouble — it has a #:bjo body, written by hand. #:sync says there is to be no second body; delete one of the two."
            | Some d ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: '%s{name}' is written #:sync, but it is defined with %s{d}, which is the suspending colour. #:sync suppresses the copy an ordinary defun would be given; a bjoroutine has no copy to suppress."
            | None -> ()

        marked |> List.map fst |> Set.ofList

    /// Candidates: an ordinary top-level `defun` with a signature, that is
    /// not itself a generated copy and does not already have one.
    ///
    /// `main` is excluded because a copy is a thing a *call site* chooses,
    /// and the entry point is the one definition with no call site inside
    /// the language: the runtime enters it, and the runtime has one colour.
    /// Its copy is therefore always dead code, and it is loud dead code —
    /// the blocking lint has a node for it, so a `main` reaching anything
    /// that parks reports a body nobody wrote and nobody can reach.
    ///
    /// A `#:sync` function is excluded from candidate generation entirely. 
    /// By never entering `reaching`, the async color is prevented from 
    /// propagating up its call graph.
    let candidates =
        decls
        |> List.choose (function
            | DDefun(name, args, body, Ordinary, r) when
                not (Naming.isSuspendingCopy name)
                && name <> "main"
                && not (Set.contains name syncOnly)
                && Map.containsKey name signatures
                && not (Map.containsKey (Naming.suspendingCopy name) signatures)
                ->
                Some(name, (args, body, r))
            | _ -> None)
        |> Map.ofList

    /// What each candidate mentions. Computed once — the fixpoint below
    /// only ever re-tests membership.
    let mentions =
        candidates |> Map.map (fun _ (_, body, _) -> Ast.freeNames Set.empty body)

    /// A `defbjouble` here, or one imported from a dependency. The imported
    /// half is what makes this worth doing at all: the port surface is in
    /// the prelude, so a graph seeded only locally would stop at every call
    /// worth copying.
    let seeds =
        decls
        |> List.choose (function
            | DDefDouble(name, _, _, _, _) -> Some name
            | _ -> None)
        |> Set.ofList
        |> Set.union (registry.DoubleDefs |> Map.toSeq |> Seq.map fst |> Set.ofSeq)

    // Two points, so a round that adds nothing is the fixpoint and no SCC
    // decomposition is needed — the same argument `EffectGraph` makes.
    let mutable reaching = Set.empty
    let mutable changed = true

    while changed do
        changed <- false

        for KeyValue(name, mentioned) in mentions do
            if not (Set.contains name reaching) then
                let hits =
                    mentioned
                    |> Set.exists (fun n -> Set.contains n seeds || Set.contains n reaching)

                if hits then
                    reaching <- Set.add name reaching
                    changed <- true

    let expanded =
        decls
        |> List.collect (fun d ->
            match d with
            | DDefun(name, args, body, Ordinary, r) when Set.contains name reaching ->
                let twin = Naming.suspendingCopy name
                let ftype, constraints, sigRange = Map.find name signatures

                // The *same* signature, unrepainted: `DDefun(..., Suspending,
                // ...)` recolours the outer arrow on its own, and unlike a
                // `-?->` there is no parameter whose colour changes.
                [ d
                  DSignature(twin, ftype, constraints, sigRange)
                  DDefun(twin, args, body, Suspending, r) ]
            | _ -> [ d ])

    expanded, (reaching |> Seq.map (fun n -> n, Naming.suspendingCopy n) |> Map.ofSeq)
