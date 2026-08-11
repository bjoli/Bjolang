namespace Bjolang

/// How the compiler reports a failure, and how it tells one kind from the other.
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

    /// Prints a failed compilation.
    ///
    /// A diagnostic is the message and nothing else: a stack trace through the
    /// inferencer describes the compiler rather than the program, and there is
    /// nothing in it for whoever wrote the program. A genuine bug keeps its
    /// trace and says which of the two it is.
    let reportFailure (ex: exn) =
        printfn ""
        printfn $"%s{ex.Message}"

        if not (isDiagnostic ex) then
            printfn ""
            printfn "This is a bug in the compiler, not in the program above. Trace:"
            printfn $"%s{ex.StackTrace}"
