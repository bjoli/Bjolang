using System.Runtime.CompilerServices;
// `Rune` for the character operations, which classify by codepoint rather than
// by UTF-16 code unit, and `StringBuilder` for the `Stringing` accumulator.
using System.Text;
using Unit = Bjoml.Unit;

public static partial class BjolangRuntime {

    /// The unit value. `Bjoml.Unit` rather than a struct of our own, so it is
    /// already the right type for a `Promise<Unit>` from a bjoroutine with no
    /// useful result.
    ///
    /// Every builtin whose Bjolang signature says `void` returns this instead
    /// of being a C# `void` method, because a callback typed `(-> %a %b)`
    /// becomes `Func<T_a, T_b>` and no `T_b` can stand for `void`.
    /// See `TypeConstants.unitType`.
    ///
    /// Public because Bjolang can now name it: `unit` is a prelude binding, and
    /// `Discard`'s blanket implementation — a function whose entire job is to
    /// answer nothing — needs a way to write the value. There is no other way
    /// to produce one that does not also do something.
    public static readonly Unit unit = default;
    
    
    // Through the dynamic environment rather than `Console` directly, which is
    // what lets `(parameterize ((current-output-port w)) ...)` capture them.
    // `Dyn.Current` starts out holding the console streams.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit display(object o) { Dyn.Current.Out.Write(o); return unit; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit displayln(object o) { Dyn.Current.Out.WriteLine(o); return unit; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit newline() { Dyn.Current.Out.WriteLine(); return unit; }

    // Interop maps a constructed generic type to a mangled name that will not
    // unify with `(Seq string)`, so this wrapper stays while the rest of the
    // whole-file operations live in `std/prelude`.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IEnumerable<string> filesubreadsublinesdivseq(string path) => System.IO.File.ReadLines(path);

    // The same for an arbitrary read procedure: what `file->seq` is built on.
    //
    // A sequence is re-enumerable exactly to the extent that the state it walks
    // is created *inside* the iterator; anything captured from outside is
    // shared by every enumeration. So opening here rather than in a `seql` is
    // what makes each enumeration a fresh read — the trick `File.ReadLines`
    // plays by keeping the path rather than a handle. The `using` is the other
    // half of owning the reader, releasing it on exhaustion and on early
    // disposal alike. `Peek` is `port-eof?`.
    public static IEnumerable<T> filesubreaddivseq<T>(Func<System.IO.TextReader, T> read, string path) {
        using var reader = new System.IO.StreamReader(path);
        while (reader.Peek() != -1) yield return read(reader);
    }

    // `GetDirectoryName` answers null for a root and for a bare filename, and
    // Bjolang has no null to test against — so the sentinel is absorbed here
    // rather than let loose in the program. That is also why this is the one
    // path operation not written in `std/prelude`.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string pathsubdirectory(string path) => System.IO.Path.GetDirectoryName(path) ?? "";

    // The failing read. `ReadLine` reports end of input by returning null, and
    // Bjolang has no null to test against, so the sentinel is converted into an
    // exception right at the boundary rather than let loose in the program.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string readersubreadsubline_BANG(System.IO.TextReader reader) =>
        reader.ReadLine()
        ?? throw new System.IO.EndOfStreamException(
            "read-line: the port is at end of input. Guard with (port-eof? p), or use read-line/opt.");

    // Draining a port into a collection, done here rather than as a Bjolang
    // loop so that the builder is used directly and each line is added once.
    public static SchemeList.SchemeList<string> readersubgtlist(System.IO.TextReader reader) {
        var builder = new SchemeList.SchemeListBuilder<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null) builder.Add(line);
        return builder.ToSchemeList();
    }

    public static Collections.RrbList<string> readersubgtvec(System.IO.TextReader reader) {
        var builder = new Collections.RrbBuilder<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null) builder.Add(line);
        return builder.ToImmutable();
    }

    public static bool @true = true;
    public static bool @false = false;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool eq<T>(T a, T b) => EqualityComparer<T>.Default.Equals(a, b);

    // `equal?` is structural, `eq?` is identity. Identity on a value type would
    // box both operands and always answer false, so it falls back to structural
    // there; the JIT specializes the test away per T.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool equal_QMARK<T>(T a, T b) => EqualityComparer<T>.Default.Equals(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool eq_QMARK<T>(T a, T b) =>
        typeof(T).IsValueType ? EqualityComparer<T>.Default.Equals(a, b) : ReferenceEquals(a, b);
    
    // --- Characters ---
    //
    // A BjoChar is a Unicode scalar value, not a UTF-16 code unit, so these
    // convert against the codepoint. No string indexing here, deliberately:
    // indexing a UTF-16 string by codepoint is O(n) and invites exactly the
    // O(n^2) loop it looks like it avoids. Cursors are the answer.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int charsubgtint(Bjolang.Runtime.BjoChar c) => (int)c.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Bjolang.Runtime.BjoChar intsubgtchar(int i) => new Bjolang.Runtime.BjoChar((uint)i);

    // A `BjoChar` is not a .NET type Bjolang can name — `char` is `TCon("Char")`
    // — so interop cannot resolve `.ToString` on one, and this stays here while
    // the numeric conversions moved to `std/prelude`.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string charsubgtstring(Bjolang.Runtime.BjoChar c) => c.ToString();

    // --- Character classification and case ---
    //
    // R6RS §11.11, and all of it here for the same reason `char->string` is: a
    // `BjoChar` is not a type interop can resolve a method on, so none of this
    // can be a `.NET` call written in `std/prelude`.
    //
    // Every one goes through `Rune`, so the classification is by *codepoint*
    // and an astral character gets a real answer rather than whatever its high
    // surrogate would have said. `char.IsLetter((char)c)` would be both wrong
    // and silently wrong above the BMP.
    //
    // The case operations are invariant-culture on purpose. R6RS specifies the
    // locale-independent Unicode mappings, and a `char-upcase` that answered
    // differently in Turkey would make a program's meaning depend on the
    // machine it runs on.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Rune AsRune(Bjolang.Runtime.BjoChar c) => new Rune(c.Value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Bjolang.Runtime.BjoChar OfRune(Rune r) => new Bjolang.Runtime.BjoChar((uint)r.Value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Bjolang.Runtime.BjoChar charsubupcase(Bjolang.Runtime.BjoChar c) =>
        OfRune(Rune.ToUpperInvariant(AsRune(c)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Bjolang.Runtime.BjoChar charsubdowncase(Bjolang.Runtime.BjoChar c) =>
        OfRune(Rune.ToLowerInvariant(AsRune(c)));

    // Unicode gives a handful of characters a titlecase form distinct from
    // their uppercase one — the Latin digraphs, ǅ against Ǆ. .NET exposes that
    // mapping only through `TextInfo.ToTitleCase` on a string, so this pays a
    // small allocation to reach it; `char-upcase` is the one to use unless the
    // digraphs are the point.
    public static Bjolang.Runtime.BjoChar charsubtitlecase(Bjolang.Runtime.BjoChar c) {
        var titled = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(c.ToString());
        Rune.DecodeFromUtf16(titled, out Rune r, out _);
        return OfRune(r);
    }

    // Simple case folding, for comparing without regard to case. Lowercasing is
    // .NET's nearest offer: it agrees with Unicode's simple fold everywhere
    // except a few characters — ﬁ and ß stay put, where a full fold would
    // expand them — and a full fold cannot be a char-to-char function anyway,
    // which is why R6RS's own `char-foldcase` is specified as the simple one.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Bjolang.Runtime.BjoChar charsubfoldcase(Bjolang.Runtime.BjoChar c) =>
        OfRune(Rune.ToLowerInvariant(AsRune(c)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool charsubalphabetic_QMARK(Bjolang.Runtime.BjoChar c) => Rune.IsLetter(AsRune(c));

    // R6RS reads "numeric" as the Nd/Nl/No categories, which is `Rune.IsNumber`
    // — not `IsDigit`, which is Nd alone and would answer false for ½ or Ⅶ.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool charsubnumeric_QMARK(Bjolang.Runtime.BjoChar c) => Rune.IsNumber(AsRune(c));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool charsubwhitespace_QMARK(Bjolang.Runtime.BjoChar c) => Rune.IsWhiteSpace(AsRune(c));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool charsubuppersubcase_QMARK(Bjolang.Runtime.BjoChar c) => Rune.IsUpper(AsRune(c));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool charsublowersubcase_QMARK(Bjolang.Runtime.BjoChar c) => Rune.IsLower(AsRune(c));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool charsubtitlesubcase_QMARK(Bjolang.Runtime.BjoChar c) =>
        Rune.GetUnicodeCategory(AsRune(c)) == System.Globalization.UnicodeCategory.TitlecaseLetter;

    // The digit's value, or `None` for a character that is not a decimal digit.
    // Narrower than `char-numeric?` deliberately: ½ is numeric and has no digit
    // value, so the two questions get two answers.
    public static Option<int> digitsubvalue(Bjolang.Runtime.BjoChar c) {
        var r = AsRune(c);
        if (Rune.GetUnicodeCategory(r) != System.Globalization.UnicodeCategory.DecimalDigitNumber) {
            return None<int>();
        }
        return Some((int)Rune.GetNumericValue(r));
    }

    // The Unicode general category, as the two-letter symbol R6RS names: `Lu`,
    // `Nd`, `Zs` and the rest. A symbol rather than a string because it is a
    // fixed enumeration meant to be compared, which is what symbols are for.
    public static Symbol charsubgeneralsubcategory(Bjolang.Runtime.BjoChar c) =>
        Symbol.Intern(Rune.GetUnicodeCategory(AsRune(c)) switch {
            System.Globalization.UnicodeCategory.UppercaseLetter        => "Lu",
            System.Globalization.UnicodeCategory.LowercaseLetter        => "Ll",
            System.Globalization.UnicodeCategory.TitlecaseLetter        => "Lt",
            System.Globalization.UnicodeCategory.ModifierLetter         => "Lm",
            System.Globalization.UnicodeCategory.OtherLetter            => "Lo",
            System.Globalization.UnicodeCategory.NonSpacingMark         => "Mn",
            System.Globalization.UnicodeCategory.SpacingCombiningMark   => "Mc",
            System.Globalization.UnicodeCategory.EnclosingMark          => "Me",
            System.Globalization.UnicodeCategory.DecimalDigitNumber     => "Nd",
            System.Globalization.UnicodeCategory.LetterNumber           => "Nl",
            System.Globalization.UnicodeCategory.OtherNumber            => "No",
            System.Globalization.UnicodeCategory.ConnectorPunctuation   => "Pc",
            System.Globalization.UnicodeCategory.DashPunctuation        => "Pd",
            System.Globalization.UnicodeCategory.OpenPunctuation        => "Ps",
            System.Globalization.UnicodeCategory.ClosePunctuation       => "Pe",
            System.Globalization.UnicodeCategory.InitialQuotePunctuation=> "Pi",
            System.Globalization.UnicodeCategory.FinalQuotePunctuation  => "Pf",
            System.Globalization.UnicodeCategory.OtherPunctuation       => "Po",
            System.Globalization.UnicodeCategory.MathSymbol             => "Sm",
            System.Globalization.UnicodeCategory.CurrencySymbol         => "Sc",
            System.Globalization.UnicodeCategory.ModifierSymbol         => "Sk",
            System.Globalization.UnicodeCategory.OtherSymbol            => "So",
            System.Globalization.UnicodeCategory.SpaceSeparator         => "Zs",
            System.Globalization.UnicodeCategory.LineSeparator          => "Zl",
            System.Globalization.UnicodeCategory.ParagraphSeparator     => "Zp",
            System.Globalization.UnicodeCategory.Control                => "Cc",
            System.Globalization.UnicodeCategory.Format                 => "Cf",
            System.Globalization.UnicodeCategory.Surrogate              => "Cs",
            System.Globalization.UnicodeCategory.PrivateUse             => "Co",
            _                                                           => "Cn",
        });

    // --- String cursors ---
    //
    // Thin forwarders onto `Bjolang.Runtime.StringCursor`, where the reasoning
    // and the decoding live. A cursor is an opaque offset into the string's
    // storage; see that file for why there is no way to turn one into an int.
    //
    // This is what replaces `string-ref`. An index-based accessor over UTF-16
    // is O(n) per lookup, so the obvious loop is quadratic; a cursor makes the
    // same loop linear and costs a struct holding an int.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Bjolang.Runtime.StringCursor stringsubcursorsubstart(string s) =>
        Bjolang.Runtime.StringCursor.Start(s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Bjolang.Runtime.StringCursor stringsubcursorsubend(string s) =>
        Bjolang.Runtime.StringCursor.End(s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool stringsubcursorsubend_QMARK(string s, Bjolang.Runtime.StringCursor c) =>
        Bjolang.Runtime.StringCursor.AtEnd(s, c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Bjolang.Runtime.BjoChar stringsubcursorsubref(string s, Bjolang.Runtime.StringCursor c) =>
        Bjolang.Runtime.StringCursor.Ref(s, c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Bjolang.Runtime.StringCursor stringsubcursorsubnext(string s, Bjolang.Runtime.StringCursor c) =>
        Bjolang.Runtime.StringCursor.Next(s, c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Bjolang.Runtime.StringCursor stringsubcursorsubprev(string s, Bjolang.Runtime.StringCursor c) =>
        Bjolang.Runtime.StringCursor.Prev(s, c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string substringdivcursors(string s, Bjolang.Runtime.StringCursor start, Bjolang.Runtime.StringCursor end) =>
        Bjolang.Runtime.StringCursor.Substring(s, start, end);

    // The character count, which is a walk. `string-length` is the storage
    // length and O(1); the two differ for any string with an astral character
    // in it, and the names are meant to be told apart.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int stringsubcount(string s) => Bjolang.Runtime.StringCursor.Count(s);

    // --- StringBuilder ---
    //
    // The accumulator behind the `Stringing` collector, and the same shape as
    // the list and vec builders: `add!` mutates and returns `Unit`, so a loop
    // slot carries the identity that never changes (§8.1).
    //
    // `AppendTo` rather than `Append(c.ToString())` — a `BjoChar` knows how to
    // write itself as one or two UTF-16 units without allocating the string
    // in between.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StringBuilder stringbuildersubempty() => new StringBuilder();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit stringbuildersubadd_BANG(StringBuilder b, Bjolang.Runtime.BjoChar c) {
        c.AppendTo(b);
        return unit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit stringbuildersubaddsubstring_BANG(StringBuilder b, string s) {
        b.Append(s);
        return unit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int stringbuildersublength(StringBuilder b) => b.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string stringbuildersubgtstring(StringBuilder b) => b.ToString();

    // --- Strings ---
    //
    // The string operations proper are `std/prelude`'s, since they are plain
    // .NET calls. This one is here so that the emptiness predicates are one
    // family: `list-empty?`, `map-empty?` and `vec-empty?` are all builtins,
    // and a `string-empty?` written as `(= (string-length s) 0)` in the library
    // would be the odd one out for no reason a caller can see.
    //
    // `Length == 0` rather than `IsNullOrEmpty`: a Bjolang string is never
    // null, and answering true for one would hide a bug at the interop
    // boundary rather than report it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool stringsubempty_QMARK(string s) => s.Length == 0;

    // Vec operations mapped from RrbFun
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbList<T> vecsubempty<T>() where T : notnull => Collections.RrbFun.Empty<T>();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static T vecsubget<T>(Collections.RrbList<T> list, int index) where T : notnull => Collections.RrbFun.Get(list, index);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbList<T> vecsubset<T>(Collections.RrbList<T> list, int index, T value) where T : notnull => Collections.RrbFun.SetItem(list, index, value);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbList<T> vecsubadd<T>(Collections.RrbList<T> list, T item) where T : notnull => Collections.RrbFun.Add(list, item);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbList<T> vecsubinsert<T>(Collections.RrbList<T> list, int index, T item) where T : notnull => Collections.RrbFun.Insert(list, index, item);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbList<T> vecsubremovesubat<T>(Collections.RrbList<T> list, int index) where T : notnull => Collections.RrbFun.RemoveAt(list, index);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbList<T> vecsubpop<T>(Collections.RrbList<T> list) where T : notnull => Collections.RrbFun.Pop(list);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbList<T> vecsubpopsubfirst<T>(Collections.RrbList<T> list) where T : notnull => Collections.RrbFun.PopFirst(list);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbList<T> vecsubslice<T>(Collections.RrbList<T> list, int start, int count) where T : notnull => Collections.RrbFun.Slice(list, start, count);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbList<T> vecsubmerge<T>(Collections.RrbList<T> list, Collections.RrbList<T> other) where T : notnull => Collections.RrbFun.Merge(list, other, false);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbList<T> vecsubmergedivpure<T>(Collections.RrbList<T> list, Collections.RrbList<T> other) where T : notnull => Collections.RrbFun.Merge(list, other, true);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static ValueTuple<Collections.RrbList<T>, Collections.RrbList<T>> vecsubsplit<T>(Collections.RrbList<T> list, int index) where T : notnull => Collections.RrbFun.Split(list, index);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbList<TResult> vecsubmap<T, TResult>(Func<T, TResult> mapper, Collections.RrbList<T> list) where T : notnull where TResult : notnull => Collections.RrbFun.Map(list, mapper);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbList<T> vecsubfilter<T>(Func<T, bool> predicate, Collections.RrbList<T> list) where T : notnull => Collections.RrbFun.Filter(list, predicate);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static TState vecsubfold<T, TState>(Func<TState, T, TState> func, TState seed, Collections.RrbList<T> list) where T : notnull => Collections.RrbFun.Fold(list, seed, func);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static T vecsubreduce<T>(Func<T, T, T> func, Collections.RrbList<T> list) where T : notnull => Collections.RrbFun.Reduce(list, func);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    // `Func<T, Unit>` rather than `Action<T>`: a Bjolang `(-> %a void)` is
    // `(-> %a Unit)`, and only one delegate shape can also be an instantiation
    // of a generic `(-> %a %b)`. The adapter lambda is the price of `for-each`
    // and `map` taking the same function, and the JIT inlines through it.
    public static Unit vecsubforsubeach<T>(Func<T, Unit> action, Collections.RrbList<T> list) where T : notnull {
        Collections.RrbFun.ForEach(list, x => action(x));
        return unit;
    }

    public static Unit vecsubforsubeachdivrange<T>(Func<T, Unit> action, Collections.RrbList<T> list, int index, int count) where T : notnull {
        Collections.RrbFun.ForEach(list, x => action(x), index, count);
        return unit;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool vecsubiter<T>(Func<T, bool> action, Collections.RrbList<T> list) where T : notnull => Collections.RrbFun.Iter(list, action);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    // `vec-length`, not `vec-count`: the underlying member is `Count`, but the
    // Bjolang name follows the language's own vocabulary rather than .NET's.
    public static int vecsublength<T>(Collections.RrbList<T> list) where T : notnull => Collections.RrbFun.Count(list);

    // `Count` is a stored field on an RrbList, so this is the same O(1) test a
    // caller would write — but it is the name the other collections already
    // use, and `vec-empty` is taken by the *constructor*, so the predicate
    // could not be spelled by convention from the outside.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool vecsubempty_QMARK<T>(Collections.RrbList<T> list) where T : notnull => Collections.RrbFun.Count(list) == 0;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool vecsubcontains<T>(Collections.RrbList<T> list, T item) where T : notnull => Collections.RrbFun.Contains(list, item);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbList<T> vecsubcompact<T>(Collections.RrbList<T> list) where T : notnull => Collections.RrbFun.Compact(list);

    // --- VecBuilder Wrappers ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbBuilder<T> vecbuildersubempty<T>() where T : notnull => Collections.RrbBuilderFun.Empty<T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbBuilder<T> vecsubgtvecbuilder<T>(Collections.RrbList<T> list) where T : notnull => Collections.RrbBuilderFun.FromList(list);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit vecbuildersubadd_BANG<T>(Collections.RrbBuilder<T> builder, T item) where T : notnull {
        Collections.RrbBuilderFun.Add(builder, item);
        return unit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit vecbuildersubset_BANG<T>(Collections.RrbBuilder<T> builder, int index, T item) where T : notnull {
        Collections.RrbBuilderFun.SetItem(builder, index, item);
        return unit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T vecbuildersubget<T>(Collections.RrbBuilder<T> builder, int index) where T : notnull => Collections.RrbBuilderFun.Get(builder, index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int vecbuildersublength<T>(Collections.RrbBuilder<T> builder) where T : notnull => Collections.RrbBuilderFun.Count(builder);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Collections.RrbList<T> vecbuildersubgtvec<T>(Collections.RrbBuilder<T> builder) where T : notnull => Collections.RrbBuilderFun.ToImmutable(builder);

    // --- ListBuilder ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchemeList.SchemeListBuilder<T> listbuildersubempty<T>() => new SchemeList.SchemeListBuilder<T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchemeList.SchemeListBuilder<T> listsubgtbuilder<T>(SchemeList.SchemeList<T> list) => list.ToBuilder();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchemeList.SchemeListBuilder<T> listsubgtlistbuilder<T>(SchemeList.SchemeList<T> list) => list.ToBuilder();

    // Answers `Unit`, not the builder. All three builders are classes that
    // mutate in place and whose identity never changes, which is the
    // precondition concurrency-design.md §8.1 asks for before making this
    // change — a transient-style builder that could reallocate would silently
    // lose writes here.
    //
    // It used to hand the builder back so that it could thread through a loop
    // slot like an immutable accumulator. Under §8.2's must-use rule that
    // convenience costs a discard at every other call site, of which there are
    // many more.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit listbuildersubadd_BANG<T>(SchemeList.SchemeListBuilder<T> builder, T item) {
        builder.Add(item);
        return unit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit listbuildersubaddsubrange_BANG<T>(SchemeList.SchemeListBuilder<T> builder, IEnumerable<T> items) {
        builder.AddRange(items);
        return unit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int listbuildersublength<T>(SchemeList.SchemeListBuilder<T> builder) => builder.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchemeList.SchemeList<T> listbuildersubgtlist<T>(SchemeList.SchemeListBuilder<T> builder) =>
        builder.ToSchemeList();

    // --- MapBuilder ---
    //
    // `MapBuilder` rather than `TransientMap`: it appends into a flat buffer
    // and sorts by CHAMP hash once at the end, the fastest bulk path. Also
    // `TransientMap`'s constructor zeroes `_count` even for a non-empty root,
    // so anything but `Empty.ToTransient()` builds a map with a wrong `Count`.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Map.MapBuilder<TK, TV> mapbuildersubempty<TK, TV>() where TK : notnull =>
        new Map.MapBuilder<TK, TV>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit mapbuildersubadd_BANG<TK, TV>(Map.MapBuilder<TK, TV> builder, TK key, TV value) where TK : notnull {
        builder.Add(key, value);
        return unit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Map.Map<TK, TV> mapbuildersubgtmap<TK, TV>(Map.MapBuilder<TK, TV> builder) where TK : notnull =>
        builder.ToImmutable();

    // --- Cursors ---
    //
    // Both collections have an allocation-free *struct* enumerator, but a
    // struct in a Bjolang binding is a value, and `MoveNext` on one copied into
    // a call advances the copy. So a cursor is a small class holding the
    // enumerator as a *field*: one allocation per loop entry, none per element,
    // no boxing.
    //
    // The advancing happens in `done?`, which the `Iterable` protocol allows —
    // called once per iteration, before `current`, and nothing peeks. `next` is
    // then the identity.

    public sealed class VecCursor<T> where T : notnull {
        public Collections.RrbEnumerator<T> E;
        public VecCursor(Collections.RrbList<T> list) { E = list.GetEnumerator(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static VecCursor<T> vecsubcursor<T>(Collections.RrbList<T> list) where T : notnull =>
        new VecCursor<T>(list);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool vecsubcursorsubdone_QMARK<T>(VecCursor<T> cursor) where T : notnull =>
        !cursor.E.MoveNext();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T vecsubcursorsubcurrent<T>(VecCursor<T> cursor) where T : notnull => cursor.E.Current;

    public sealed class MapCursor<TK, TV> where TK : notnull {
        public Map.MapEnumerator<TK, TV> E;
        public MapCursor(Map.Map<TK, TV> map) { E = map.GetEnumerator(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MapCursor<TK, TV> mapsubcursor<TK, TV>(Map.Map<TK, TV> map) where TK : notnull =>
        new MapCursor<TK, TV>(map);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool mapsubcursorsubdone_QMARK<TK, TV>(MapCursor<TK, TV> cursor) where TK : notnull =>
        !cursor.E.MoveNext();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueTuple<TK, TV> mapsubcursorsubcurrent<TK, TV>(MapCursor<TK, TV> cursor) where TK : notnull {
        var kv = cursor.E.Current;
        return new ValueTuple<TK, TV>(kv.Key, kv.Value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T[] makesubarray<T>(int length) => new T[length];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T arraysubref<T>(T[] arr, int index) => arr[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit arraysubset_BANG<T>(T[] arr, int index, T value) { arr[index] = value; return unit; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int arraysublength<T>(T[] arr) => arr.Length;

    /// An optional value, and Bjolang's `(Option %a)`. A struct because it
    /// began as the carrier for an omitted keyword argument: `default` is
    /// `None`, so an unsupplied parameter costs nothing.
    public struct Option<T> : IEquatable<Option<T>> {
        public readonly bool IsSome;
        public readonly T Value;
        public Option(T value) { IsSome = true; Value = value; }
        public static implicit operator Option<T>(T value) => new Option<T>(value);

        /// What `Some` and `None` patterns actually test, and an `int` on
        /// purpose. Matching on `IsSome` directly would give C# two arms that
        /// between them cover a `bool`, so it would rule the generated
        /// match-failure arm unreachable and refuse to compile the switch.
        public int Tag => IsSome ? 1 : 0;

        // Without these, `equal?` on an Option would fall back to ValueType's
        // reflective structural comparison.
        public bool Equals(Option<T> other) =>
            IsSome == other.IsSome
            && (!IsSome || EqualityComparer<T>.Default.Equals(Value, other.Value));

        public override bool Equals(object? obj) => obj is Option<T> other && Equals(other);
        public override int GetHashCode() => IsSome ? HashCode.Combine(true, Value) : 0;
        public override string ToString() => IsSome ? $"(Some {Value})" : "None";
    }

    // --- Option constructors and accessors ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Option<T> Some<T>(T value) => new Option<T>(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Option<T> None<T>() => default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool some_QMARK<T>(Option<T> option) => option.IsSome;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool none_QMARK<T>(Option<T> option) => !option.IsSome;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T optionsubget<T>(Option<T> option) =>
        option.IsSome ? option.Value : throw new InvalidOperationException("option-get on None");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T optionsubgetsubor<T>(Option<T> option, T fallback) =>
        option.IsSome ? option.Value : fallback;

    /// Bjolang's `(Result %e %a)`. A struct for the same reason `Option<T>` is
    /// one: every `#:exceptions` interop call returns one, so it must not
    /// allocate.
    ///
    /// The error comes first, matching the Bjolang spelling and how `Monad` is
    /// implemented for it — the type argument free to move is the trailing one.
    public struct Result<TErr, TOk> : IEquatable<Result<TErr, TOk>> {
        public readonly bool IsOk;
        public readonly TOk OkValue;
        public readonly TErr ErrValue;

        private Result(bool isOk, TOk okValue, TErr errValue) {
            IsOk = isOk;
            OkValue = okValue;
            ErrValue = errValue;
        }

        /// What `Ok` and `Err` patterns test, and an `int` for the same reason
        /// `Option.Tag` is: two arms covering a `bool` between them make C#
        /// rule the generated match-failure arm unreachable.
        public int Tag => IsOk ? 1 : 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Result<TErr, TOk> Ok(TOk value) => new Result<TErr, TOk>(true, value, default!);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Result<TErr, TOk> Err(TErr error) => new Result<TErr, TOk>(false, default!, error);

        public bool Equals(Result<TErr, TOk> other) =>
            IsOk == other.IsOk
            && (IsOk
                ? EqualityComparer<TOk>.Default.Equals(OkValue, other.OkValue)
                : EqualityComparer<TErr>.Default.Equals(ErrValue, other.ErrValue));

        public override bool Equals(object? obj) => obj is Result<TErr, TOk> other && Equals(other);

        public override int GetHashCode() =>
            IsOk ? HashCode.Combine(true, OkValue) : HashCode.Combine(false, ErrValue);

        public override string ToString() => IsOk ? $"(Ok {OkValue})" : $"(Err {ErrValue})";
    }

    // --- Seq (IEnumerable) ---
    //
    // Each of these that returns a sequence is itself an iterator: no work
    // until the result is enumerated, and never more than one element live.
    // That is the point — `(seq-head (seq-map f xs))` calls `f` once, whatever
    // the length of `xs`.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IEnumerable<T> seqsubempty<T>() => Array.Empty<T>();

    public static bool seqsubempty_QMARK<T>(IEnumerable<T> source) {
        foreach (var _ in source) return false;
        return true;
    }

    public static T seqsubhead<T>(IEnumerable<T> source) {
        foreach (var item in source) return item;
        throw new InvalidOperationException("seq-head of an empty sequence");
    }

    public static IEnumerable<T> seqsubtail<T>(IEnumerable<T> source) {
        var seenAny = false;
        foreach (var item in source) {
            if (!seenAny) { seenAny = true; continue; }
            yield return item;
        }
        if (!seenAny) throw new InvalidOperationException("seq-tail of an empty sequence");
    }

    public static IEnumerable<U> seqsubmap<T, U>(Func<T, U> selector, IEnumerable<T> source) {
        foreach (var item in source) yield return selector(item);
    }

    public static IEnumerable<T> seqsubfilter<T>(Func<T, bool> predicate, IEnumerable<T> source) {
        foreach (var item in source) if (predicate(item)) yield return item;
    }

    public static TState seqsubfold<T, TState>(Func<TState, T, TState> folder, TState initial, IEnumerable<T> source) {
        var acc = initial;
        foreach (var item in source) acc = folder(acc, item);
        return acc;
    }

    // The generator answers, for a given state, whether there is another
    // element and what the state after it is. `None` ends the sequence.
    public static IEnumerable<T> seqsubunfold<T, TState>(
        Func<TState, Option<ValueTuple<T, TState>>> generator,
        TState seed) {

        var state = seed;
        while (true) {
            var step = generator(state);
            if (!step.IsSome) yield break;
            yield return step.Value.Item1;
            state = step.Value.Item2;
        }
    }

    public static IEnumerable<T> seqsubtake<T>(IEnumerable<T> source, int count) {
        if (count <= 0) yield break;
        var taken = 0;
        foreach (var item in source) {
            yield return item;
            if (++taken >= count) yield break;
        }
    }

    public static IEnumerable<T> seqsubskip<T>(IEnumerable<T> source, int count) {
        var skipped = 0;
        foreach (var item in source) {
            if (skipped < count) { skipped++; continue; }
            yield return item;
        }
    }

    public static IEnumerable<T> seqsubappend<T>(IEnumerable<T> first, IEnumerable<T> second) {
        foreach (var item in first) yield return item;
        foreach (var item in second) yield return item;
    }

    public static Unit seqsubforsubeach<T>(Func<T, Unit> action, IEnumerable<T> source) {
        foreach (var item in source) action(item);
        return unit;
    }

    public static int seqsublength<T>(IEnumerable<T> source) {
        var count = 0;
        foreach (var _ in source) count++;
        return count;
    }

    /// `start` inclusive, `stop` exclusive.
    public static IEnumerable<int> seqsubrange(int start, int stop) {
        for (var i = start; i < stop; i++) yield return i;
    }

    // --- Seq conversions ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IEnumerable<T> listsubgtseq<T>(SchemeList.SchemeList<T> list) => list;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchemeList.SchemeList<T> seqsubgtlist<T>(IEnumerable<T> source) =>
        new SchemeList.SchemeListBuilder<T>(source).ToSchemeList();

    public static IEnumerable<T> vecsubgtseq<T>(Collections.RrbList<T> vec) where T : notnull {
        var count = vec.Count;
        for (var i = 0; i < count; i++) yield return vec[i];
    }

    public static Collections.RrbList<T> seqsubgtvec<T>(IEnumerable<T> source) where T : notnull {
        var builder = Collections.RrbBuilderFun.Empty<T>();
        foreach (var item in source)  builder.Add(item);
        return builder.ToImmutable();
    }

    // --- List (SchemeList) Wrappers ---

    // The variadic list constructor. `(list 1 2 3)` never reaches this —
    // inference rewrites a saturated direct call into the same TListMake node a
    // quoted literal produces. This is for the value position, `(def f list)`,
    // where there is no call site to rewrite.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchemeList.SchemeList<T> list<T>(params T[] items) => SchemeList.SchemeList.Create<T>(items);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchemeList.SchemeList<T> listsubempty<T>() => SchemeList.SchemeList.Empty<T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchemeList.SchemeList<T> cons<T>(T car, SchemeList.SchemeList<T> cdr) => SchemeList.SchemeList.Cons(car, cdr);

    // Capital-C aliases for backward compatibility with Bjolang's Cons/Nil constructors
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchemeList.SchemeList<T> Cons<T>(T car, SchemeList.SchemeList<T> cdr) => SchemeList.SchemeList.Cons(car, cdr);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchemeList.SchemeList<T> Nil<T>() => SchemeList.SchemeList.Empty<T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T listsubhead<T>(SchemeList.SchemeList<T> list) => SchemeList.SchemeList.Head(list);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchemeList.SchemeList<T> listsubtail<T>(SchemeList.SchemeList<T> list) => SchemeList.SchemeList.Tail(list);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool listsubempty_QMARK<T>(SchemeList.SchemeList<T> list) => SchemeList.SchemeList.IsEmpty(list);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int listsublength<T>(SchemeList.SchemeList<T> list) => SchemeList.SchemeList.Length(list);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchemeList.SchemeList<T> listsubreverse<T>(SchemeList.SchemeList<T> list) => SchemeList.SchemeList.Reverse(list);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchemeList.SchemeList<U> listsubmap<T, U>(Func<T, U> selector, SchemeList.SchemeList<T> list) => SchemeList.SchemeList.Map(list, selector);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SchemeList.SchemeList<T> listsubfilter<T>(Func<T, bool> predicate, SchemeList.SchemeList<T> list) => SchemeList.SchemeList.Filter(list, predicate);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TState listsubfoldl<T, TState>(Func<TState, T, TState> folder, TState initial, SchemeList.SchemeList<T> list) => SchemeList.SchemeList.Fold(list, initial, folder);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TState listsubfoldr<T, TState>(Func<T, TState, TState> folder, TState initial, SchemeList.SchemeList<T> list) => SchemeList.SchemeList.FoldRight(list, initial, folder);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit listsubforsubeach<T>(Func<T, Unit> action, SchemeList.SchemeList<T> list) {
        SchemeList.SchemeList.ForEach(list, x => action(x));
        return unit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T listsubref<T>(SchemeList.SchemeList<T> list, int index) => SchemeList.SchemeList.Item(list, index);

    // No `listsubcount`: `SchemeList.Count` is a C# alias for `Length`, and
    // exporting both gave Bjolang two names for one O(n) walk. `list-length`
    // is the one.

    // --- Map (CHAMP) Wrappers ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Map.Map<TK, TV> mapsubempty<TK, TV>() where TK : notnull => Map.Map<TK, TV>.Empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TV mapsubref<TK, TV>(Map.Map<TK, TV> map, TK key) where TK : notnull => map.Get(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TV mapsubget<TK, TV>(Map.Map<TK, TV> map, TK key) where TK : notnull => map.Get(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TV mapsubgetsubor<TK, TV>(Map.Map<TK, TV> map, TK key, TV fallback) where TK : notnull =>
        map.TryGetValue(key, out var val) ? val : fallback;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Option<TV> mapsubtrysubget<TK, TV>(Map.Map<TK, TV> map, TK key) where TK : notnull =>
        map.TryGetValue(key, out var val) ? new Option<TV>(val) : default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Map.Map<TK, TV> mapsubset<TK, TV>(Map.Map<TK, TV> map, TK key, TV value) where TK : notnull =>
        map.Set(key, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Map.Map<TK, TV> mapsubadd<TK, TV>(Map.Map<TK, TV> map, TK key, TV value) where TK : notnull =>
        map.Add(key, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Map.Map<TK, TV> mapsubremove<TK, TV>(Map.Map<TK, TV> map, TK key) where TK : notnull =>
        map.Remove(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool mapsubcontains_QMARK<TK, TV>(Map.Map<TK, TV> map, TK key) where TK : notnull =>
        map.ContainsKey(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool mapsubhassubkey_QMARK<TK, TV>(Map.Map<TK, TV> map, TK key) where TK : notnull =>
        map.ContainsKey(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int mapsublength<TK, TV>(Map.Map<TK, TV> map) where TK : notnull => map.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool mapsubempty_QMARK<TK, TV>(Map.Map<TK, TV> map) where TK : notnull => map.IsEmpty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Map.Map<TK, TV> mapsubclear<TK, TV>(Map.Map<TK, TV> map) where TK : notnull => map.Clear();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IEnumerable<TK> mapsubkeys<TK, TV>(Map.Map<TK, TV> map) where TK : notnull => map.Keys;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IEnumerable<TV> mapsubvalues<TK, TV>(Map.Map<TK, TV> map) where TK : notnull => map.Values;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Map.Map<TK, TV> mapsubmerge<TK, TV>(Map.Map<TK, TV> map, Map.Map<TK, TV> other) where TK : notnull =>
        map.Merge(other);

    // --- Map higher-order functions ---
    //
    // Every callback here takes the *pair*, as one argument, because a Map's
    // element is its `(Tuple %k %v)` — `Iterable`'s `%elem`, `Foldable`'s
    // `%item`, `map->list`, `map->seq` and the `#map(...)` literal all say so.
    // A trait mentioning one element takes a one-argument callback, so a
    // two-argument function over a key and a value cannot go where one is
    // expected. The pair is a `ValueTuple`, so it costs no allocation.
    // TODO: this should be fixed. Map should have an interface that works with ValueTuples instead of kvp so that we
    // do not have to create valuetuples

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Map.Map<TK, TV> mapsubmergesubwith<TK, TV>(Func<ValueTuple<TK, TV, TV>, TV> resolver, Map.Map<TK, TV> map, Map.Map<TK, TV> other) where TK : notnull =>
        map.Merge(other, (k, a, b) => resolver(new ValueTuple<TK, TV, TV>(k, a, b)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit mapsubforsubeach<TK, TV>(Func<ValueTuple<TK, TV>, Unit> action, Map.Map<TK, TV> map) where TK : notnull {
        map.ForEach((k, v) => action(new ValueTuple<TK, TV>(k, v)));
        return unit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool mapsubiter<TK, TV>(Func<ValueTuple<TK, TV>, bool> action, Map.Map<TK, TV> map) where TK : notnull =>
        map.Iter((k, v) => action(new ValueTuple<TK, TV>(k, v)));

    public static TState mapsubfold<TK, TV, TState>(Func<TState, ValueTuple<TK, TV>, TState> folder, TState initial, Map.Map<TK, TV> map) where TK : notnull {
        var state = initial;
        map.Iter((k, v) => {
            state = folder(state, new ValueTuple<TK, TV>(k, v));
            return true;
        });
        return state;
    }

    public static Map.Map<TK, TV> mapsubfilter<TK, TV>(Func<ValueTuple<TK, TV>, bool> predicate, Map.Map<TK, TV> map) where TK : notnull {
        var tmap = Map.Map<TK, TV>.Empty.ToTransient();
        map.Iter((k, v) => {
            if (predicate(new ValueTuple<TK, TV>(k, v))) {
                tmap.Set(k, v);
            }
            return true;
        });
        return tmap.ToImmutable();
    }

    // Takes the pair and returns the new *value*: the key is what the result is
    // filed under, so letting the mapper move it would make collisions this
    // function has no answer for.
    public static Map.Map<TK, TV2> mapsubmap<TK, TV, TV2>(Func<ValueTuple<TK, TV>, TV2> mapper, Map.Map<TK, TV> map) where TK : notnull {
        var tmap = Map.Map<TK, TV2>.Empty.ToTransient();
        map.Iter((k, v) => {
            tmap.Set(k, mapper(new ValueTuple<TK, TV>(k, v)));
            return true;
        });
        return tmap.ToImmutable();
    }

    // `Functor` is not `Foldable`, and this is the one place a pair will not do.
    // Its `(-> %a %b)` has to replace the element type and hand back the same
    // shape, and the only argument of `(Map %k %v)` free to move is `%v` — so a
    // functorial map over a Map sees the value, with the key riding along. There
    // is no `(Map %k %v)` whose element type is a pair the functor may replace.
    public static Map.Map<TK, TV2> mapsubmapsubvalues<TK, TV, TV2>(Func<TV, TV2> mapper, Map.Map<TK, TV> map) where TK : notnull {
        var tmap = Map.Map<TK, TV2>.Empty.ToTransient();
        map.Iter((k, v) => {
            tmap.Set(k, mapper(v));
            return true;
        });
        return tmap.ToImmutable();
    }

    // --- Map Conversions ---
    public static Map.Map<TK, TV> listsubgtmap<TK, TV>(SchemeList.SchemeList<ValueTuple<TK, TV>> list) where TK : notnull {
        var tmap = Map.Map<TK, TV>.Empty.ToTransient();
        var cur = list;
        while (!SchemeList.SchemeList.IsEmpty(cur)) {
            var head = SchemeList.SchemeList.Head(cur);
            tmap.Set(head.Item1, head.Item2);
            cur = SchemeList.SchemeList.Tail(cur);
        }
        return tmap.ToImmutable();
    }

    public static SchemeList.SchemeList<ValueTuple<TK, TV>> mapsubgtlist<TK, TV>(Map.Map<TK, TV> map) where TK : notnull {
        var pairs = new List<ValueTuple<TK, TV>>(map.Count);
        map.ForEach((k, v) => pairs.Add(new ValueTuple<TK, TV>(k, v)));
        var result = SchemeList.SchemeList.Empty<ValueTuple<TK, TV>>();
        for (int i = pairs.Count - 1; i >= 0; i--) {
            result = SchemeList.SchemeList.Cons(pairs[i], result);
        }
        return result;
    }

    public static Map.Map<TK, TV> vecsubgtmap<TK, TV>(Collections.RrbList<ValueTuple<TK, TV>> vec) where TK : notnull {
        var tmap = Map.Map<TK, TV>.Empty.ToTransient();
        int count = Collections.RrbFun.Count(vec);
        for (int i = 0; i < count; i++) {
            var item = Collections.RrbFun.Get(vec, i);
            tmap.Set(item.Item1, item.Item2);
        }
        return tmap.ToImmutable();
    }

    public static Collections.RrbList<ValueTuple<TK, TV>> mapsubgtvec<TK, TV>(Map.Map<TK, TV> map) where TK : notnull {
        var builder = Collections.RrbBuilderFun.Empty<ValueTuple<TK, TV>>();
        map.ForEach((k, v) => {
            builder = Collections.RrbBuilderFun.Add(builder, new ValueTuple<TK, TV>(k, v));
        });
        return Collections.RrbBuilderFun.ToImmutable(builder);
    }

    public static Map.Map<TK, TV> seqsubgtmap<TK, TV>(IEnumerable<ValueTuple<TK, TV>> source) where TK : notnull {
        var tmap = Map.Map<TK, TV>.Empty.ToTransient();
        foreach (var (k, v) in source) {
            tmap.Set(k, v);
        }
        return tmap.ToImmutable();
    }

    public static IEnumerable<ValueTuple<TK, TV>> mapsubgtseq<TK, TV>(Map.Map<TK, TV> map) where TK : notnull {
        foreach (var kvp in map) {
            yield return new ValueTuple<TK, TV>(kvp.Key, kvp.Value);
        }
    }

    // -----------------------------------------------------------------------
    // The dynamic environment
    // -----------------------------------------------------------------------

    /// <summary>
    /// One immutable snapshot of the dynamic environment.
    ///
    /// The three ports get fields rather than living in the champ with
    /// everything else: the port reads are hot and a field beats a hash lookup,
    /// and the runtime reaches for the output port from C#, where no Bjolang
    /// `Param` is in scope.
    ///
    /// All readonly, so installing an environment and undoing one are both a
    /// single reference assignment — which is what makes `parameterize` cheap
    /// and exception-safe.
    /// </summary>
    public sealed class DynEnv {
        public readonly System.IO.TextWriter Out;
        public readonly System.IO.TextReader In;
        public readonly System.IO.TextWriter Err;

        /// Every parameter that is not one of the three ports, keyed by the
        /// `Param` object's own identity. `Map` hashes with
        /// `EqualityComparer&lt;object&gt;.Default`, which for a `Param` is
        /// reference equality — so two parameters never collide, whatever they
        /// were named.
        public readonly Map.Map<object, object> Vals;

        internal DynEnv(
            System.IO.TextWriter output,
            System.IO.TextReader input,
            System.IO.TextWriter error,
            Map.Map<object, object> vals) {
            Out = output;
            In = input;
            Err = error;
            Vals = vals;
        }

        internal DynEnv WithOut(System.IO.TextWriter w) => new(w, In, Err, Vals);
        internal DynEnv WithIn(System.IO.TextReader r) => new(Out, r, Err, Vals);
        internal DynEnv WithErr(System.IO.TextWriter w) => new(Out, In, w, Vals);
        internal DynEnv WithVal(object key, object value) => new(Out, In, Err, Vals.Set(key, value));
    }

    /// <summary>
    /// Where the current environment lives: BjoML's per-fiber context slot.
    ///
    /// A plain static was fine until `bjo`, and wrong in two directions after
    /// it: a `parameterize` on one fiber would rebind *every* fiber's
    /// environment for as long as its body ran, and a spawned child on a pool
    /// thread would read whatever the variable held when it got there — usually
    /// the parent's restored environment, its `finally` having already run.
    ///
    /// `FiberContext` is the slot for this: one opaque per-thread reference
    /// that BjoML reinstates around every suspension and that `Bjo.Spawn`
    /// captures, which is what gives §4.5's snapshot-at-spawn semantics.
    ///
    /// Not `AsyncLocal`, which rides on `ExecutionContext` and allocates on
    /// every write — the wrong cost model when `parameterize` is meant to be
    /// cheap.
    /// </summary>
    public static class Dyn {
        /// What a thread with no fiber context sees. Immutable and shared, so a
        /// thread that never parameterizes anything costs nothing and still
        /// finds the standard ports.
        private static readonly DynEnv Root = new(
            Console.Out,
            Console.In,
            Console.Error,
            Map.Map<object, object>.Empty);

        public static DynEnv Current {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Bjoml.FiberContext.Current as DynEnv ?? Root;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => Bjoml.FiberContext.Current = value;
        }
    }

    /// <summary>
    /// A parameter: an opaque, typed key into the dynamic environment.
    ///
    /// The key is the object's own identity, so parameters cannot collide and
    /// there is no namespace to share. `T` is what makes the champ — which
    /// stores plain `object` — safe to read back: only `parameterize` at this
    /// same `Param&lt;T&gt;` can write the key, so the cast in
    /// <see cref="parametersubref"/> cannot fail. That cast is the whole of the
    /// unsoundness, and it is unreachable from Bjolang.
    /// </summary>
    public sealed class Param<T> {
        /// 0 = output port, 1 = input port, 2 = error port, -1 = in the champ.
        /// This is why `parameterize` is one form for both storage kinds.
        internal readonly int Slot;

        /// The value seen when nothing has bound this parameter yet. Held on
        /// the parameter rather than seeded into the initial environment, so
        /// that a parameter made at any point still has an answer.
        internal readonly T Initial;

        internal Param(int slot, T initial) {
            Slot = slot;
            Initial = initial;
        }

        public override string ToString() => "#<parameter>";
    }

    /// The output port. Bound to a value, not a nullary function: it is read
    /// with `parameter-ref` like every other parameter.
    public static readonly Param<System.IO.TextWriter> currentsuboutputsubport = new(0, Console.Out);
    public static readonly Param<System.IO.TextReader> currentsubinputsubport = new(1, Console.In);
    public static readonly Param<System.IO.TextWriter> currentsuberrorsubport = new(2, Console.Error);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Param<T> makesubparameter<T>(T initial) => new(-1, initial);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T parametersubref<T>(Param<T> p) {
        var env = Dyn.Current;
        switch (p.Slot) {
            // Safe by construction: only the three parameters declared above
            // carry a slot, and each is declared at the matching port type.
            case 0: return (T)(object)env.Out;
            case 1: return (T)(object)env.In;
            case 2: return (T)(object)env.Err;
            default: return env.Vals.TryGetValue(p, out var v) ? (T)v : p.Initial;
        }
    }

    // The two halves of `parameterize`, which the parser desugars to. Not
    // surface API: called by hand they pair a push with a restore that no
    // `finally` is guarding.
    //
    // The push returns the environment it displaced rather than the runtime
    // keeping a shadow stack, so the saved frame is an ordinary value in an
    // ordinary `let` — a hidden stack would be one more thing to fall out of
    // step with a lazily consumed `Seq`.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DynEnv parametersubpush_BANG<T>(Param<T> p, T value) {
        var prev = Dyn.Current;
        Dyn.Current = p.Slot switch {
            0 => prev.WithOut((System.IO.TextWriter)(object)value!),
            1 => prev.WithIn((System.IO.TextReader)(object)value!),
            2 => prev.WithErr((System.IO.TextWriter)(object)value!),
            _ => prev.WithVal(p, value!)
        };
        return prev;
    }

    /// Restores a whole environment at once, so one `finally` undoes a binding
    /// whichever slot it went to.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit dynsubrestore_BANG(DynEnv saved) { Dyn.Current = saved; return unit; }

    /// <summary>
    /// An interned keyword. All instances of a keyword with the same name share the same reference.
    /// </summary>
    public sealed class Keyword : IEquatable<Keyword>, IComparable<Keyword> {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Keyword> _table = new();

        public string Name { get; }

        private Keyword(string name) {
            Name = name;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Keyword Intern(string name) =>
            _table.GetOrAdd(name, static n => new Keyword(n));

        public bool Equals(Keyword? other) => ReferenceEquals(this, other);
        public override bool Equals(object? obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
        public int CompareTo(Keyword? other) => string.CompareOrdinal(Name, other?.Name);
        public override string ToString() => $":{Name}";
    }

    /// <summary>
    /// An interned symbol. All instances of a symbol with the same name share the same reference.
    /// </summary>
    public sealed class Symbol : IEquatable<Symbol>, IComparable<Symbol> {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Symbol> _table = new();

        public string Name { get; }

        private Symbol(string name) {
            Name = name;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Symbol Intern(string name) =>
            _table.GetOrAdd(name, static n => new Symbol(n));

        public bool Equals(Symbol? other) => ReferenceEquals(this, other);
        public override bool Equals(object? obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
        public int CompareTo(Symbol? other) => string.CompareOrdinal(Name, other?.Name);
        public override string ToString() => Name;
    }

    // --- Keyword & Symbol helpers ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string keywordsubgtstring(Keyword k) => k.Name;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Keyword stringsubgtkeyword(string s) => Keyword.Intern(s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string symbolsubgtstring(Symbol s) => s.Name;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Symbol stringsubgtsymbol(string s) => Symbol.Intern(s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool keyword_QMARK(object? o) => o is Keyword;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool symbol_QMARK(object? o) => o is Symbol;
}

