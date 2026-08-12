using System.Text;

namespace Bjolang.Runtime;

/// <summary>
/// Where an identifier came from, which is what decides whether hygiene renames
/// it.
/// </summary>
/// <remarks>
/// This has to be a field rather than something recovered from object identity.
/// <see cref="global::BjolangRuntime.Symbol"/> interns, so the <c>x</c> a macro
/// received in its input and the <c>x</c> it built in a template are the same
/// reference — the classic implicit-renaming trick of comparing against the
/// input form cannot work here.
///
/// <see cref="Template"/> is the default, deliberately: everything a transformer
/// constructs is renamed unless it says otherwise, and the only way to say
/// otherwise is <c>inject</c>.
/// </remarks>
public enum SyntaxOrigin
{
    /// Built by the transformer. Renamed apart from the call site.
    Template = 0,
    /// Taken from the input form, or produced by `inject`. Left alone.
    CallSite = 1,
}

/// <summary>
/// A source range, flattened out of the compiler's <c>Lexer.Range</c> so that
/// runtime and compiler can pass one across the reflection boundary.
/// </summary>
/// <remarks>
/// <c>File</c> is carried because <c>include</c> splices files together, so a
/// line number alone is ambiguous.
/// </remarks>
public readonly record struct SrcRange(
    string? File,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn)
{
    /// A range nothing has filled in yet. The expander replaces these with the
    /// macro call's own range, which is what makes a constructed node report
    /// where it was written rather than nowhere.
    public bool IsUnset => File is null;
}

/// <summary>
/// A piece of syntax: what a macro transformer receives and returns.
/// </summary>
/// <remarks>
/// Shaped exactly like a union the compiler would generate — an abstract record
/// with nested sealed positional records — so that pattern matching and
/// construction need no special cases in <c>Codegen</c>.
///
/// <see cref="Range"/> and <see cref="Origin"/> are deliberately *not* primary
/// constructor parameters. C# synthesizes <c>Deconstruct</c> only for those, so
/// keeping these two out of it is what lets a Bjolang pattern be written
/// <c>(SList items)</c> without mentioning either.
/// </remarks>
public abstract record Syntax
{
    private Syntax() { }

    /// Where this came from. Unset on anything a transformer built; the expander
    /// fills those in with the macro call's range.
    public SrcRange Range { get; init; }

    /// Whether hygiene renames identifiers under this node.
    public SyntaxOrigin Origin { get; init; }

    /// An identifier. Subject to renaming when <see cref="Origin"/> is
    /// <see cref="SyntaxOrigin.Template"/>.
    public sealed record SSym(global::BjolangRuntime.Symbol Item1) : Syntax;

    /// A quoted symbol written as data — `'foo` inside a template. Never
    /// renamed: it is a value, not a reference to a binding.
    public sealed record SDatum(global::BjolangRuntime.Symbol Item1) : Syntax;

    /// A numeric literal, as written. Text rather than a number for the same
    /// reason `Parser.EInt` is: the same node carries `1` and `1.5`, and which
    /// one it is has not been decided yet.
    public sealed record SInt(string Item1) : Syntax;

    public sealed record SStr(string Item1) : Syntax;

    public sealed record SChar(BjoChar Item1) : Syntax;

    public sealed record SKey(global::BjolangRuntime.Keyword Item1) : Syntax;

    /// A parenthesized form. A real `(List Syntax)`, so `map`, `fold` and the
    /// existing trailing-rest pattern work on macro input with nothing added.
    public sealed record SList(global::SchemeList.SchemeList<Syntax> Item1) : Syntax;

    /// A punctuation token that survives reading: `,`, `,@`, `:`, `.` or `...`.
    ///
    /// These are not identifiers and not data, but they do reach a macro. A
    /// comma is an optional argument separator, `(: name type)` is a signature,
    /// and `...` is a rest pattern — so an input form can contain any of them,
    /// and dropping one would silently change what the macro was handed.
    /// Carrying the spelling is what makes the round trip total.
    public sealed record SPunct(string Item1) : Syntax;

    /// This node with a different range. The expander's fill-in step; F# has no
    /// `with` expression for a C# record.
    public Syntax WithRange(SrcRange range) => this with { Range = range };

    /// This node with a different origin. How `inject` marks an identifier as
    /// belonging to the call site.
    public Syntax WithOrigin(SyntaxOrigin origin) => this with { Origin = origin };

    /// The name of an identifier or a quoted symbol, or null for anything else.
    /// Used by `compare`, which is defined on identifiers only.
    public string? IdentifierName => this switch
    {
        SSym s => s.Item1.Name,
        SDatum d => d.Item1.Name,
        _ => null,
    };

    /// Renders back to something that reads as source.
    ///
    /// `sealed` so the nested cases do not each synthesize a record `ToString`
    /// of their own, which would print field names rather than syntax.
    public sealed override string ToString()
    {
        var sb = new StringBuilder();
        Render(sb, this);
        return sb.ToString();
    }

    private static void Render(StringBuilder sb, Syntax node)
    {
        switch (node)
        {
            case SSym s:
                sb.Append(s.Item1.Name);
                break;
            case SDatum d:
                sb.Append('\'').Append(d.Item1.Name);
                break;
            case SInt n:
                sb.Append(n.Item1);
                break;
            case SStr s:
                sb.Append('"').Append(s.Item1.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                break;
            case SChar c:
                sb.Append("#\\").Append(c.Item1.ToString());
                break;
            case SKey k:
                sb.Append("#:").Append(k.Item1.Name);
                break;
            case SPunct p:
                sb.Append(p.Item1);
                break;
            case SList l:
                sb.Append('(');
                var first = true;
                for (var cursor = l.Item1; cursor is global::SchemeList.Cons<Syntax> cons; cursor = cons.Cdr)
                {
                    if (!first) sb.Append(' ');
                    first = false;
                    Render(sb, cons.Car);
                }
                sb.Append(')');
                break;
        }
    }
}
