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
    // Bjolang has no null to test against — so the sentinel is turned into the
    // `None` that means the same thing. That is also why this is the one path
    // operation not written in `std/prelude`.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Option<string> pathsubdirectory(string path) =>
        System.IO.Path.GetDirectoryName(path) is { Length: > 0 } dir ? Some(dir) : None<string>();

    // The failing read. `ReadLine` reports end of input by returning null, and
    // Bjolang has no null to test against, so the sentinel is converted into an
    // exception right at the boundary rather than let loose in the program.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string readersubreadsubline_BANG(System.IO.TextReader reader) =>
        reader.ReadLine()
        ?? throw new System.IO.EndOfStreamException(
            "read-line: the port is at end of input. Guard with (port-eof? p), or use read-line/opt.");

    // The failing char read.
    //
    // A Bjolang `char` is a Unicode scalar and `TextReader.Read` answers a
    // UTF-16 code unit, so this is not a cast: a character outside the BMP
    // arrives as two units and has to be put back together here. Otherwise
    // `read-char` would be the one traversal in the language that hands out
    // half a character, which is exactly what walking a `string` goes to the
    // trouble of not doing.
    //
    // That is also why there is no `peek-char`. `Peek` gives one unit of
    // lookahead, one is not always a character, and a peek that could not
    // promise the same answer `read-char` is about to give would be a trap.
    // Buying it needs a pushback buffer, and a port is deliberately the bare
    // .NET object. `port-eof?` answers the question peeking is usually asked
    // for, and a parser that needs more wants the whole text and a
    // `StringCursor`.
    public static Bjolang.Runtime.BjoChar readersubreadsubchar_BANG(System.IO.TextReader reader) {
        var first = reader.Read();
        if (first < 0)
            throw new System.IO.EndOfStreamException(
                "read-char: the port is at end of input. Guard with (port-eof? p), or use read-char/opt.");

        var unit = (char)first;
        if (!char.IsSurrogate(unit)) return new Bjolang.Runtime.BjoChar((uint)first);

        // A low surrogate first, or a high one with nothing after it, is text
        // that was already broken before it got here. Saying so beats
        // `BjoChar`'s constructor reporting an out-of-range codepoint, which
        // names neither the port nor the read.
        if (!char.IsHighSurrogate(unit))
            throw new InvalidOperationException(
                "read-char: the port holds an unpaired low surrogate, which is not a character.");

        var second = reader.Read();
        if (second < 0 || !char.IsLowSurrogate((char)second))
            throw new InvalidOperationException(
                "read-char: the port holds a high surrogate with no low surrogate after it, which is not a character.");

        return new Bjolang.Runtime.BjoChar((uint)char.ConvertToUtf32(unit, (char)second));
    }

    // The counterpart, and not a `Write((char)c)` for the same reason: an
    // astral character is two UTF-16 units and both have to go out.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit writersubwritesubchar_BANG(System.IO.TextWriter writer, Bjolang.Runtime.BjoChar c) {
        c.WriteTo(writer);
        return unit;
    }

    // What a string output port has accumulated.
    //
    // A builtin for the same reason the failing read is one: the .NET answer for
    // the wrong receiver is not a failure but a *value*. `TextWriter` does not
    // override `ToString`, so asking a file port would hand back
    // "System.IO.StreamWriter" and never say a word — and a port is one type to
    // every caller by design, so the type checker cannot rule the question out.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string writersubgtstring(System.IO.TextWriter writer) =>
        writer is System.IO.StringWriter sw
            ? sw.ToString()
            : throw new InvalidOperationException(
                "get-output-string: this port is a "
                + writer.GetType().Name
                + ", not one from (open-output-string). Only a string port accumulates text to hand back.");

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
    
    // .NET's own equality, which the `Eq` implementations in `std/prelude` are
    // written in terms of. No top-level `eq` here: a trait method named `=`
    // mangles to `eq`, and `using static BjolangRuntime` would put a second one
    // of those in scope of every generated file.
    //
    // `Equals` rather than `==`: this is what a hash-based collection asks, so
    // it is reflexive on `NaN` where `==` is not, and it is the only form that
    // compiles at an unconstrained `T`.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool clrsubequals<T>(T a, T b) => EqualityComparer<T>.Default.Equals(a, b);

    // The hash that goes with it. Reads through the same comparer so that the
    // two cannot disagree about a type .NET treats specially.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int clrsubhash<T>(T a) => a is null ? 0 : EqualityComparer<T>.Default.GetHashCode(a);

    // What an `eq-hash` written over several fields folds with. Order matters,
    // so `(hash-combine (hash x) (hash y))` and its transpose differ.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int hashsubcombine(int a, int b) => HashCode.Combine(a, b);

    // The answer an `eq-hash` gives when there is no honest one: the type has a
    // mutable field, so hashing it would break the law that a key's hash does
    // not change, and hashing only the immutable fields would break the law
    // that equal values hash alike. A mutable record is simply not a key.
    //
    // Typed `int` rather than generic because `eq-hash` returns `int` and a
    // generic return has no argument to infer itself from. Loud rather than
    // silent for the reason the whole design turns on: the alternative is a
    // `Map` that quietly loses the entry the next time the field is written.
    public static int unhashable(string typeName) =>
        throw new InvalidOperationException(
            $"{typeName} has a mutable field, so it has no stable hash: it cannot be a Map or Set key. "
            + "Compare it with = instead, or write an Eq implementation whose eq-hash reads only the immutable fields.");

    // `eq?` is identity. On a value type there is no identity to ask after —
    // boxing would make every answer false — so it falls back to structural
    // equality there; the JIT specializes the test away per T.
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
    public static T vecsubref<T>(Collections.RrbList<T> list, int index) where T : notnull => Collections.RrbFun.Get(list, index);

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
    public static T vecbuildersubref<T>(Collections.RrbBuilder<T> builder, int index) where T : notnull => Collections.RrbBuilderFun.Get(builder, index);

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

    // --- Cursors ---
    //
    // A `Vec` has an allocation-free *struct* enumerator, but a struct in a
    // Bjolang binding is a value, and `MoveNext` on one copied into a call
    // advances the copy. So a cursor is a small class holding the enumerator as
    // a *field*: one allocation per loop entry, none per element, no boxing.
    //
    // The collections with a project of their own carry their own cursor —
    // `Map.MapCursor`, `Set.SetCursor` — and bind to it through `import/class`.
    // This one is here because `Vec`'s type is a builtin.
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

    /// <summary>
    /// A position in a walk of a `Seq`.
    ///
    /// Not for the reason `VecCursor` exists — an `IEnumerator&lt;T&gt;` is
    /// already a class — but for two others.
    ///
    /// **A `Seq` has no cheap tail.** `IEnumerable` gives out enumerators and
    /// nothing else, so "the rest of this sequence" can only be spelled as "the
    /// source, minus a prefix", and walking with one costs a fresh enumeration
    /// per element: quadratic, and it re-runs whatever the generator does on
    /// the way. Holding the enumerator makes a walk linear and pulls each
    /// element once, which is the only reading under which a `Seq` over a port
    /// or a file behaves the way that test file says it does.
    ///
    /// **The enumerator is disposable.** `file->seq` owns a file handle and
    /// `port->seq` a reader, and both are released by disposing the enumerator.
    /// `Done` therefore disposes as soon as the walk is exhausted, which is
    /// what `foreach` does at the same point.
    ///
    /// A walk abandoned part-way — a `:break` — does not reach that, and the
    /// handle waits for the collector. Closing that would need the `Iterable`
    /// protocol to have a notion of a walk being over, which today it has not:
    /// there is no hook between the last `done?` and leaving the loop.
    /// </summary>
    public sealed class SeqCursor<T> {
        /// Null once exhausted, which is what makes a second `Done` after the
        /// end safe rather than a `MoveNext` on a disposed enumerator.
        private IEnumerator<T>? _e;

        public SeqCursor(IEnumerable<T> source) { _e = source.GetEnumerator(); }

        public bool Done() {
            var e = _e;
            if (e is null) return true;
            if (e.MoveNext()) return false;
            _e = null;
            e.Dispose();
            return true;
        }

        /// Only ever reached after a `Done` that answered false, which is the
        /// same contract `VecCursor` is read under.
        public T Current => _e!.Current;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SeqCursor<T> seqsubcursor<T>(IEnumerable<T> source) => new SeqCursor<T>(source);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool seqsubcursorsubdone_QMARK<T>(SeqCursor<T> cursor) => cursor.Done();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T seqsubcursorsubcurrent<T>(SeqCursor<T> cursor) => cursor.Current;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T[] makesubarray<T>(int length) => new T[length];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T arraysubref<T>(T[] arr, int index) => arr[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Unit arraysubset_BANG<T>(T[] arr, int index, T value) { arr[index] = value; return unit; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int arraysublength<T>(T[] arr) => arr.Length;

    // The collection-to-rest-array conversions behind `apply`. Neither is
    // reachable from Bjolang source: `apply` is an intrinsic and builds the
    // call to one of these itself, so there is no prelude binding to spell.
    //
    // An `Array` collection needs no entry here at all — it is passed straight
    // through as the rest argument, which is the whole reason `apply` is worth
    // having over rebuilding the call by hand.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T[] vecsubgtarray<T>(Collections.RrbList<T> list) where T : notnull {
        var arr = new T[Collections.RrbFun.Count(list)];
        Collections.RrbFun.CopyTo(list, arr, 0);
        return arr;
    }

    // Two walks, one allocation. Going via a `List<T>` would be one walk and
    // two allocations, and the second is the one that costs — the array has to
    // exist to be the rest parameter either way.
    public static T[] listsubgtarray<T>(SchemeList.SchemeList<T> list) {
        var arr = new T[SchemeList.SchemeList.Length(list)];
        var cur = list;

        for (int i = 0; i < arr.Length; i++) {
            arr[i] = SchemeList.SchemeList.Head(cur);
            cur = SchemeList.SchemeList.Tail(cur);
        }

        return arr;
    }

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
    public static T optionsubref<T>(Option<T> option) =>
        option.IsSome ? option.Value : throw new InvalidOperationException("option-ref on None");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T optionsubrefsubor<T>(Option<T> option, T fallback) =>
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

    /// `(raise e)` — throw, keeping the stack trace the exception already has.
    ///
    /// The counterpart of `try`, which turns the failures it names into values.
    /// This is how one gets back out: a handler that has decided it cannot deal
    /// with what it caught, and `with-cancel`, which has to fire `(Failed e)`
    /// on the way past without swallowing the exception it fired for.
    ///
    /// `ExceptionDispatchInfo` rather than `throw e`, which would reset
    /// `StackTrace` to this line and lose where the failure actually came from.
    ///
    /// Generic in its *return* type, and so a value anywhere: `raise` never
    /// returns, and typing it `void` would keep it out of the one position that
    /// needs it — a `match` arm whose siblings produce something. C# cannot
    /// infer that argument from anything, so `Codegen` writes it out.
    public static T raise<T>(Exception e) {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
        return default!;   // unreachable: Throw() does not return
    }

    /// Why a scope was cancelled, and Bjolang's `CancelReason`. The payload of
    /// every cancellation token: a token used to say only *that* you had been
    /// cancelled.
    ///
    /// Builtin rather than declared in `prelude.bjo` because the runtime has to
    /// *construct* reasons — the nack in `spawn-evt`, the deadline watcher — and
    /// it is compiled below the generated code, so it cannot name a type the
    /// code generator emits.
    ///
    /// Shaped exactly like a union the compiler would generate, as `Syntax` is,
    /// so that patterns and construction take the ordinary union paths.
    ///
    /// Closed: a library cannot add a case. `(Requested "circuit-breaker")` is
    /// the escape hatch, and it is deliberately an untyped string — naming who
    /// asked is a diagnostic, not a value anything dispatches on.
    public abstract record CancelReason {
        private CancelReason() { }

        /// Someone asked to stop, and the string names who.
        public sealed record Requested(string Item1) : CancelReason;

        /// A time limit fired. What `with-deadline` raises.
        public sealed record Deadline : CancelReason;

        /// The scope owning the token returned normally. What `with-cancel`
        /// raises on the way out, so that a child handed the token stops rather
        /// than outliving the scope that made it.
        public sealed record ScopesubEnded : CancelReason;

        /// The scope body, or a sibling, threw.
        public sealed record Failed(Exception Item1) : CancelReason;

        /// `sealed` for the reason `Syntax.ToString` is: without it each nested
        /// case synthesizes a record `ToString` of its own, which prints field
        /// names rather than the Bjolang spelling.
        public sealed override string ToString() => this switch {
            Requested r => $"(Requested \"{r.Item1}\")",
            Deadline => "(Deadline)",
            ScopesubEnded => "(Scope-Ended)",
            Failed f => $"(Failed {f.Item1.GetType().Name})",
            _ => "(CancelReason)",
        };
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

    /// Runs the enumerator forward rather than counting past elements it has
    /// already yielded, so the per-element test disappears once the prefix is
    /// gone — and a `count` past the end stops at the end instead of walking
    /// what is left.
    public static IEnumerable<T> seqsubdrop<T>(IEnumerable<T> source, int count) {
        using var e = source.GetEnumerator();
        for (var i = 0; i < count; i++)
            if (!e.MoveNext()) yield break;
        while (e.MoveNext()) yield return e.Current;
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

    // -----------------------------------------------------------------------
    // The dynamic environment
    // -----------------------------------------------------------------------

    /// <summary>
    /// One immutable snapshot of the dynamic environment.
    ///
    /// Three parameters get fields rather than living in the champ with
    /// everything else, and they are the three that are *read* hottest — not
    /// the three that go together in a manual. A field beats a node access, and
    /// the runtime reaches for the output port and the cancel token from C#,
    /// where no Bjolang `Param` is in scope.
    ///
    /// So the error port is not among them: nothing in the runtime writes to it
    /// and no loop reads it, which makes it a cold parameter like any other. The
    /// field it vacated went to the ambient cancel token, which a
    /// `(:until-cancelled)` loop reads once per iteration.
    ///
    /// All readonly, so installing an environment and undoing one are both a
    /// single reference assignment — which is what makes `parameterize` cheap
    /// and exception-safe.
    /// </summary>
    public sealed class DynEnv {
        public readonly System.IO.TextWriter Out;
        public readonly System.IO.TextReader In;

        /// The ambient cancellation token, and null when nothing has bound one.
        ///
        /// Null rather than the root token, so that this field means what a
        /// champ miss means and `Dyn.Root` needs to know nothing about
        /// `Concurrency.cs`: were the root token seeded here instead, the two
        /// classes' static initializers would have to run in an order neither
        /// of them states.
        public readonly Bjoml.Promise<CancelReason>? Cancel;

        /// Every parameter that is not one of the three ports, keyed by the
        /// parameter's <see cref="Param{T}.Id"/> rather than by the `Param`
        /// object itself.
        ///
        /// An `int` key is its own hash, so a read costs a field load where an
        /// identity key cost a call to `RuntimeHelpers.GetHashCode` — a
        /// sync-block probe that also *assigns* the hash the first time it is
        /// asked. `ParamIds` then hands out ids that place themselves: see the
        /// id encoding there for which parameters land in a root slot of their
        /// own.
        ///
        /// The champ is kept in preference to a flat vector because its nodes
        /// are sized by occupancy: rebinding one parameter copies a node as
        /// wide as the number of parameters *currently bound*, not as wide as
        /// the number that exist. A program pays for what it overloads.
        public readonly Map.Map<int, object> Vals;

        internal DynEnv(
            System.IO.TextWriter output,
            System.IO.TextReader input,
            Bjoml.Promise<CancelReason>? cancel,
            Map.Map<int, object> vals) {
            Out = output;
            In = input;
            Cancel = cancel;
            Vals = vals;
        }

        internal DynEnv WithOut(System.IO.TextWriter w) => new(w, In, Cancel, Vals);
        internal DynEnv WithIn(System.IO.TextReader r) => new(Out, r, Cancel, Vals);
        internal DynEnv WithCancel(Bjoml.Promise<CancelReason> c) => new(Out, In, c, Vals);
        internal DynEnv WithVal(int id, object value) => new(Out, In, Cancel, Vals.Set(id, value));
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
            StdIn,
            null,
            Map.Map<int, object>.Empty);

        public static DynEnv Current {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Bjoml.FiberContext.Current as DynEnv ?? Root;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => Bjoml.FiberContext.Current = value;
        }
    }

    /// <summary>
    /// Where a parameter's champ key comes from, and why the key is shaped the
    /// way it is.
    ///
    /// The champ files a key under its hash, and an `int` hashes to itself, so
    /// an id is not just a name for a parameter — it is a *position* in the
    /// trie, and handing out ids is deciding how deep each parameter sits. The
    /// root branches 32 ways on the low five bits, so those five bits are the
    /// whole of the budget:
    ///
    /// - **Hot ids are 0..30.** Each has the low bits to itself, so it occupies
    ///   a root slot no other parameter can ever be filed under, and reading it
    ///   is one node access however many parameters the program has. There are
    ///   31 rather than 32 because the last slot is spoken for.
    ///
    /// - **Cold ids are `31 + (k &lt;&lt; 5)`.** Every one of them ends in the
    ///   same five bits, so the whole cold population hangs off root slot 31
    ///   and cannot push a hot parameter down a level. A cold parameter is not
    ///   *slow*: while only one of them is bound the champ keeps it inline at
    ///   root slot 31, and it is a second simultaneously-bound cold parameter
    ///   that first costs anyone a second node access.
    ///
    /// `make-parameter` spends hot ids and then falls through to cold ones, so
    /// exhausting the budget costs a pointer chase rather than hitting a wall —
    /// there is no capacity to declare and no cliff to stay clear of.
    /// `make-cold-parameter` skips the queue in the other direction, for a
    /// parameter its author knows is not read in a loop.
    ///
    /// Widening the root would widen the hot region for free: with 64-way
    /// branching these become 0..62 and `63 + (k &lt;&lt; 6)`, and nothing else
    /// here changes.
    /// </summary>
    internal static class ParamIds {
        /// One less than the root's branching factor. The odd slot out is the
        /// cold wing.
        internal const int HotSlots = 31;

        /// The shift that reaches the root's second five bits, which is where
        /// cold ids differ from one another.
        private const int ColdShift = 5;

        /// The largest `k` for which `31 + (k << 5)` is still positive. Past it
        /// the shift would wrap and alias two parameters onto one id, which
        /// would not fail — each would silently read the other's binding.
        private const int MaxCold = (int.MaxValue - HotSlots) >> ColdShift;

        private static int _hot = -1;
        private static int _cold = -1;

        internal static int NextHot() {
            var n = System.Threading.Interlocked.Increment(ref _hot);
            // Unsigned, because the counter goes on climbing after the budget
            // is spent: a wrapped negative would otherwise read as in range and
            // be handed out as an id some parameter already holds.
            return (uint)n < HotSlots ? n : NextCold();
        }

        internal static int NextCold() {
            var k = System.Threading.Interlocked.Increment(ref _cold);
            if ((uint)k > MaxCold)
                throw new InvalidOperationException(
                    $"More than {MaxCold} parameters have been created; the dynamic environment has no key space left.");
            return HotSlots + (k << ColdShift);
        }
    }

    /// <summary>
    /// A parameter: an opaque, typed key into the dynamic environment.
    ///
    /// Two parameters cannot collide, because <see cref="ParamIds"/> hands each
    /// one an id no other will get. `T` is what makes the champ — which stores
    /// plain `object` — safe to read back: only `parameterize` at this same
    /// `Param&lt;T&gt;` can write the id, so the cast in
    /// <see cref="parametersubref"/> cannot fail. That cast is the whole of the
    /// unsoundness, and it is unreachable from Bjolang.
    /// </summary>
    public sealed class Param<T> {
        /// 0 = output port, 1 = input port, 2 = ambient cancel token,
        /// -1 = in the champ. This is why `parameterize` is one form for both
        /// storage kinds.
        internal readonly int Slot;

        /// The champ key, and meaningless for a port. `readonly`, so the JIT
        /// can hoist it out of a loop that reads the same parameter repeatedly.
        internal readonly int Id;

        /// The value seen when nothing has bound this parameter yet. Held on
        /// the parameter rather than seeded into the initial environment, so
        /// that a parameter made at any point still has an answer.
        internal readonly T Initial;

        internal Param(int slot, int id, T initial) {
            Slot = slot;
            Id = id;
            Initial = initial;
        }

        public override string ToString() => "#<parameter>";
    }

    /// Standard input, buffered.
    ///
    /// **One instance, and that is the whole reason it is a field.** `Dyn.Root`
    /// and `current-input-port` both need standard input, and two `BjoPort`s
    /// over one `Console.In` would each be holding characters the other had
    /// already taken — the buffer that makes `port-eof?` free is also a claim
    /// on input nobody else may read.
    ///
    /// Wrapped rather than left bare because stdin is the port every program
    /// touches, and an unbuffered one is the port whose eof question always
    /// costs a syscall. Nothing is read here: `Wrap` only allocates.
    ///
    /// Standard *output* is deliberately not wrapped. A writer has no eof
    /// problem to solve, and a buffer in front of the console would only delay
    /// output past the point a program crashed.
    public static readonly System.IO.TextReader StdIn = Bjolang.Runtime.BjoPort.Wrap(Console.In);

    /// The output port. Bound to a value, not a nullary function: it is read
    /// with `parameter-ref` like every other parameter.
    ///
    /// A field parameter takes no id, so it spends none of the 31 hot slots and
    /// a program's own parameters get the whole budget.
    public static readonly Param<System.IO.TextWriter> currentsuboutputsubport = new(0, -1, Console.Out);
    public static readonly Param<System.IO.TextReader> currentsubinputsubport = new(1, -1, StdIn);

    /// The error port, which is a cold parameter and not a field.
    ///
    /// It is the one standard port the runtime never writes to itself, and
    /// nothing reads it in a loop — a program reaches for it when something has
    /// already gone wrong. So it earns neither a field nor one of the 31 slots
    /// that stay cheap however many parameters exist.
    public static readonly Param<System.IO.TextWriter> currentsuberrorsubport =
        new(-1, ParamIds.NextCold(), Console.Error);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Param<T> makesubparameter<T>(T initial) => new(-1, ParamIds.NextHot(), initial);

    /// `(make-cold-parameter initial)`. A parameter that gives up its claim on
    /// a root slot of its own.
    ///
    /// Same semantics as `make-parameter` in every respect — this says only
    /// that the parameter is not read in a loop, and so should not spend one of
    /// the 31 ids that are cheap to read no matter what else the program does.
    /// Worth reaching for when a parameter is created per-request or
    /// per-connection rather than once at module level, since those are the
    /// ones that would otherwise crowd out the parameters that are read hot.
    ///
    /// See <see cref="ParamIds"/> for what it costs: nothing at all while it is
    /// the only cold parameter bound, and one extra node access after that.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Param<T> makesubcoldsubparameter<T>(T initial) => new(-1, ParamIds.NextCold(), initial);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T parametersubref<T>(Param<T> p) {
        var env = Dyn.Current;
        switch (p.Slot) {
            // Safe by construction: only the three parameters that carry a slot
            // reach these, and each is declared at the matching type.
            case 0: return (T)(object)env.Out;
            case 1: return (T)(object)env.In;
            // Null is "nothing has bound one", the same thing a champ miss
            // means, and it falls back the same way.
            case 2: return env.Cancel is { } tok ? (T)(object)tok : p.Initial;
            default: return env.Vals.TryGetValue(p.Id, out var v) ? (T)v : p.Initial;
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
            2 => prev.WithCancel((Bjoml.Promise<CancelReason>)(object)value!),
            _ => prev.WithVal(p.Id, value!)
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

    // --- Syntax ---

    /// `(syntax->string s)`. Renders a piece of syntax back to something that
    /// reads as source, which is what a macro's error message wants to quote.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string syntaxsubgtstring(Bjolang.Runtime.Syntax s) => s.ToString();

    /// `(syntax-file s)` and `(syntax-line s)`. The range is otherwise opaque:
    /// a macro has no business constructing one, and the expander fills in the
    /// call site's range for everything a transformer builds.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string syntaxsubfile(Bjolang.Runtime.Syntax s) => s.Range.File ?? "<unknown>";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int syntaxsubline(Bjolang.Runtime.Syntax s) => s.Range.StartLine;

    /// `(syntax-ident=? a b)`. Are these two the same identifier?
    ///
    /// The value-level `compare`: the same test, available to code rather than
    /// only to a transformer's third parameter, which is what a `syntax-match`
    /// pattern compiles a literal `'name` to. A quoted symbol counts as an
    /// identifier, so a pattern's `'name` matches an input that wrote either
    /// `name` or `'name` — one names a thing, and which spelling it was reached
    /// by is not something a pattern should have to know.
    ///
    /// Base names, so a hygiene mark does not hide an identifier from the macro
    /// it was handed to: a form built by one macro and passed to another arrives
    /// renamed.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool syntaxsubidenteq_QMARK(Bjolang.Runtime.Syntax a, Bjolang.Runtime.Syntax b)
    {
        var an = a.IdentifierName;
        var bn = b.IdentifierName;
        return an is not null && bn is not null && BaseName(an) == BaseName(bn);
    }

    /// The name a hygiene mark was added to. Mirrors the compiler's
    /// `Gensym.baseName`, which is where the `__N` suffix comes from.
    private static string BaseName(string name)
    {
        var i = name.LastIndexOf("__", StringComparison.Ordinal);
        if (i < 0 || i + 2 == name.Length) return name;

        for (var j = i + 2; j < name.Length; j++)
            if (!char.IsAsciiDigit(name[j]))
                return name;

        return name[..i];
    }

    /// `(syntax-error form message)`. How a transformer rejects its input.
    ///
    /// Types as a `Syntax` so it can stand in a `match` arm beside the arms that
    /// return one. It never does return: the expander catches this, unwraps the
    /// reflection frame, and reports it against the macro's call site.
    public static Bjolang.Runtime.Syntax syntaxsuberror(Bjolang.Runtime.Syntax form, string message) =>
        throw new InvalidOperationException($"{message} — in {form}");

    /// What `,@` compiles to: append, on the children of one template form.
    ///
    /// Monomorphic, and named for the one thing it is for, rather than being a
    /// general `list-append`. `std/prelude` already publishes one of those, and
    /// a builtin of the same name is ambiguous to C# at
    /// every call site — both are in scope through `using static`. A splice
    /// also must not depend on the prelude having been imported: `lib/` is
    /// compiled without it.
    public static SchemeList.SchemeList<Bjolang.Runtime.Syntax> syntaxsubsplice(
        SchemeList.SchemeList<Bjolang.Runtime.Syntax> a,
        SchemeList.SchemeList<Bjolang.Runtime.Syntax> b)
    {
        if (a.IsEmpty) return b;
        var result = b;
        foreach (var item in SchemeList.SchemeList.Reverse(a)) result = SchemeList.SchemeList.Cons(item, result);
        return result;
    }
}

