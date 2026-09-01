module Bjolang.Gensym

/// The compiler's *single* source of invented names.
///
/// A single, global counter is necessary because the inliner splices bodies
/// that already contain `LoopLowering` names into functions where `Codegen`
/// will later hoist temporaries. A shared counter guarantees these names
/// remain distinct across passes.
let private counter = ref 0

/// `prefix__N`, with `N` unique across the whole compilation.
///
/// The `__N` suffix is not decoration. `Codegen.sanitizeIdent` is not
/// injective — `a-b` and `asubb` both become `asubb` — so a renamed binder may
/// not rely on its base name to distinguish it. The counter does.
let fresh (prefix: string) : string =
    counter.Value <- counter.Value + 1
    $"%s{prefix}__%d{counter.Value}"

/// The counter's value, and a way to put it back.
///
/// One compilation's invented names must not depend on what was compiled before
/// it in the same process: a module's `.dll` is judged stale by timestamp, so
/// two builds of unchanged source that differ in a `tmp__37` are a cache that
/// starts missing. A process boundary used to guarantee that for free. These
/// are what `Session` brackets an in-process module compilation with instead.
///
/// A REPL entry is the other case and uses the same pair the other way: the
/// counter is *not* put back, so two entries never invent the same name — entry
/// 4's `tmp__12` and entry 9's would otherwise be one C# member in two
/// assemblies, and the one loaded second is the one that wins.
let snapshot () : int = counter.Value

let restore (n: int) : unit = counter.Value <- n

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
