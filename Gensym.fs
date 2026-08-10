module Bjolang.Gensym

/// The compiler's *single* source of invented names.
///
/// Three independent counters used to exist — one in `Codegen`, one in
/// `LoopLowering`, one in `Unification` — and each was safe only as long as
/// nothing else invented a name in the same shape. The inliner breaks that
/// assumption on purpose: it splices a body that already contains
/// `LoopLowering`'s names into a function that `Codegen` will later hoist
/// temporaries into. One counter is the only way to keep those apart.
let private counter = ref 0

/// `prefix__N`, with `N` unique across the whole compilation.
///
/// The `__N` suffix is not decoration. `Codegen.sanitizeIdent` is not
/// injective — `a-b` and `asubb` both become `asubb` — so a renamed binder may
/// not rely on its base name to distinguish it. The counter does.
let fresh (prefix: string) : string =
    counter.Value <- counter.Value + 1
    $"%s{prefix}__%d{counter.Value}"

/// The base name a `fresh` name was derived from, for diagnostics.
let baseName (name: string) : string =
    match name.LastIndexOf "__" with
    | -1 -> name
    | i ->
        let suffix = name.Substring(i + 2)
        if suffix.Length > 0 && suffix |> Seq.forall System.Char.IsDigit then
            name.Substring(0, i)
        else
            name

let reset () = counter.Value <- 0
