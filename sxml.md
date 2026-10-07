# `(text xml)`: design

A typed SXML/SSAX-style XML library for the Bjolang standard library, phase 1.

Kiselyov's SSAX fold is kept as the reading model: a streaming fold threads a
seed through the document, and building a tree is one particular fold. SXML's
list representation is not kept: the tree is an opaque type, and SXML is only
its printed form. From OCaml, the layering of Xmlm (a pull stream of signals,
then a tree fold where the caller picks the tree type, adjacent character data
always merged) and the small default tree of Ezxmlm.

The core tree knows nothing about HTML. `(text html)`, `(text xml match)`,
`(text xml path)` and `(text xml decode)` are later modules, and can only use
what is exported here, so the view API is complete and cheap.

Only `System.Xml` and `System.Xml.Linq`, only Bjolang plus `import/extern`, no
C# shim, and bjoweb is not touched.

## Modules and files

| Module             | File                        | Holds                                                         |
|--------------------|-----------------------------|---------------------------------------------------------------|
| `(text xml)`       | `lib/text/xml.bjo`          | names, `Node`, `Document`, construction, views, helpers, printer |
| `(text xml read)`  | `lib/text/xml/read.bjo`     | sources, options, errors, signals, `xml-fold`, `xml-tree-fold`, `parse-xml`, `xml-for-each` |
| `(text xml write)` | `lib/text/xml/write.bjo`    | `node->xml`, `document->xml` and their string forms           |

`build_std.sh` builds them in that order, after `(std ports)`, which
`(text xml read)` imports for `ByteInputPort`. Documentation goes in
`Docs/std/xml.org`.

## Decisions

1. **Option values are typed unions with tags.** `#:dtd 'parse` and
   `#:whitespace 'drop` elaborate into the union cases `DtdParse` and
   `DropWhitespace`. This needed bug A below fixed, which it now is.
2. **`node-case` takes one keyword per variant plus `#:else`**, as asked. This
   needed bug B below fixed, which it now is.
3. **The doctype is written with `XmlWriter.WriteRaw`**, after validating its
   parts with `XmlConvert`. `XmlWriter.WriteDocType` needs a C# null for an
   absent public id, system id and internal subset, and Bjolang cannot pass
   null; `""` writes `PUBLIC ""` or `[]`. No escaping is involved, only the
   choice of quote for the system literal.
4. **Node constructors are `xml-element`, `xml-text`, `xml-comment` and
   `xml-pi`.** `text` and `comment` are too common as identifiers, and
   `(std http)` already has a `Text` case.
5. **`document->xml` writes `<?xml version="1.0"?>` by default**
   (`#:declaration? #t`), with no `encoding`: the encoding of the destination
   is the port's business, and a declaration without one is never wrong for
   UTF-8. It is written through `WriteProcessingInstruction "xml"`, which .NET
   turns into the declaration verbatim.
6. **`node=?` ignores positions and attribute order.** XML attributes are
   unordered.
7. **`xml-skip-element!` uses `XmlReader.Skip`**, as asked. The skipped subtree
   is not checked against `#:max-depth`; the reader's own node stack grows with
   its depth. `#:max-characters` still bounds it. Documented.
8. **The seed of `xml-fold`, and the element and text functions of
   `xml-tree-fold`, are positional.** A keyword always has a default, and there
   is none for a value of a generic type.
9. **`#:comments?` is taken by the signal layer, `parse-xml` and
   `xml-for-each`.** The folds take a `#:comment` handler instead, whose
   default ignores comments; a `#:comments?` beside it could only contradict
   it.
10. **`#:dtd 'ignore` loses the doctype.** With `DtdProcessing.Ignore`,
    `XmlReader` does not report the `DocumentType` node at all, so
    `document-doctype` is `None`. Only `'parse` keeps it.
11. **`Result` is written error first**, as Bjolang has it:
    `(Result XmlError Document)`.
12. **I/O errors raise.** A missing file is not an `XmlError`; `parse-xml` on
    `(xml-file "missing.xml")` raises the `IOException`, as `file-read-text`
    does.
13. **`MaxCharactersFromEntities` is fixed at 10 000 000**, .NET's own default,
    set explicitly under `#:dtd 'parse`. Not a keyword.

## `(text xml)`

```scheme
;; ---- Names ----------------------------------------------------------------

;; A namespace URI and a local name. Wraps System.Xml.Linq.XName, which is
;; interned, so equality is reference equality. No prefix is stored: a prefix
;; belongs to serialized text, so writing a parsed document may choose other
;; prefixes than the input had. ->str is Clark notation, {uri}local.
(type (: XmlName #:opaque (Struct (: v XName))))

;; The local name is checked with XmlConvert.VerifyNCName. A bad one is a
;; programming error and raises an ArgumentException (never an XmlException,
;; which the reader's error boundary would take for its own).
(: xml-name (-> string (#:ns string) XmlName))          ;; #:ns "" = no namespace
(: xml-name-local (-> XmlName string))
(: xml-name-ns (-> XmlName string))                     ;; "" = no namespace
(: xml-namespace string)                                ;; http://www.w3.org/XML/1998/namespace

;; ---- Small types ----------------------------------------------------------

;; Line and column as .NET reports them: 1-based, the column of the name.
(type (: XmlPos (Struct (: line int) (: column int))))
(type (: XmlAttrs (Alias (List (Tuple XmlName string)))))
;; Stored so it can be written back, never used to validate.
(type (: Doctype (Record (: name string)
                         (: public-id (Option string))
                         (: system-id (Option string)))))

;; ---- Nodes ----------------------------------------------------------------

;; Element (name, attributes, children, position), text, comment, and
;; processing instruction (target, data). The case names are internal.
(type-rec (: Node #:opaque (Union ...)))

;; The smart constructor enforces the invariants every Node obeys, whoever
;; built it: adjacent text children are merged, empty text children are
;; dropped, and a duplicate attribute name or a namespace declaration (an
;; attribute named xmlns, or in the http://www.w3.org/2000/xmlns/ namespace)
;; raises an ArgumentException. Attributes stay in the order given.
(: xml-element (-> XmlName XmlAttrs (List Node) (#:pos (Option XmlPos)) Node))
(: xml-text (-> string Node))
(: xml-comment (-> string Node))
(: xml-pi (-> string string Node))                      ;; target, data

;; Views. Accessors raise on the wrong variant.
(: element? (-> Node bool))
(: text? (-> Node bool))
(: comment? (-> Node bool))
(: pi? (-> Node bool))
(: element-name (-> Node XmlName))
(: element-attrs (-> Node XmlAttrs))
(: element-children (-> Node (List Node)))
(: element-pos (-> Node (Option XmlPos)))
;; One call, no allocation: Option is a struct and Tuple a ValueTuple, so a
;; match expander can compile (p ...) into (:view element-view (Some ...)).
(: element-view (-> Node (Option (Tuple XmlName XmlAttrs (List Node)))))
(: text-value (-> Node string))
(: comment-value (-> Node string))
(: pi-target (-> Node string))
(: pi-data (-> Node string))

;; One keyword per variant. A variant with no handler goes to #:else, whose
;; default raises, so a caller that writes #:else keeps working when a variant
;; is added. #:else is declared first because the other defaults call it.
(: node-case (-> Node
                 (#:else (-> Node %r))
                 (#:element (-> XmlName XmlAttrs (List Node) (Option XmlPos) %r))
                 (#:text (-> string %r))
                 (#:comment (-> string %r))
                 (#:pi (-> string string %r))
                 %r))

;; ---- Helpers --------------------------------------------------------------

(: attr-ref (-> Node XmlName (Option string)))          ;; None for a non-element
(: element-children-only (-> Node (List Node)))         ;; () for a non-element
(: text-content (-> Node string))                       ;; all descendant text, in order
(: descendants (-> Node (Seq Node)))                    ;; document order, not the node itself
(: node=? (-> Node Node bool))                          ;; ignores positions and attribute order

;; ---- Documents ------------------------------------------------------------

;; Doctype, prolog (comments and PIs before the root), root element, epilog
;; (comments and PIs after it). The constructor raises if the root is not an
;; element or the prolog or epilog holds anything but comments and PIs.
(type (: Document #:opaque (Record ...)))
(: xml-document (-> Node (#:doctype (Option Doctype)) (#:prolog (List Node)) (#:epilog (List Node))
                    Document))
(: document-root (-> Document Node))
(: document-doctype (-> Document (Option Doctype)))
(: document-prolog (-> Document (List Node)))
(: document-epilog (-> Document (List Node)))
(: document=? (-> Document Document bool))

;; impls: (Eq Node) and (Eq Document), so = is node=? and a Node is a safe Map
;; key; (->str XmlName), (->str Node) and (->str Document), the printer.
```

Whitespace is kept: the tree loses no text, and each consumer decides what to
skip.

**Every function that walks a tree uses an explicit stack**, never recursion per
nesting level: the writer, the printer, `text-content`, `descendants`,
`node=?` and `eq-hash`. A user can build a very deep tree by hand, and a
`StackOverflowException` cannot be caught in .NET. The `Eq` implementation
matters for the same reason: without it `=` would be the C# record's
generated, recursive `Equals`.

`xml-element` checks duplicates by comparing names, and switches to a set for
many attributes, so a document with thousands of attributes on one element is
not quadratic.

### Printer

SXML style, for the REPL and for debugging. Phase 1 does not promise that it
reads back.

```scheme
(p (@ (class "x")) "hi" (b "!"))
({http://www.w3.org/2005/Atom}entry (@ ({http://www.w3.org/1999/xlink}href "#a")) ...)
(*COMMENT* " text ")
(*PI* xml-stylesheet "href='a.css'")
(*TOP* (*DOCTYPE* html) (*PI* ...) (root ...))
```

Strings are written with `"` and `\` escaped, and newline, tab and return as
`\n`, `\t` and `\r`.

## `(text xml read)`

Each layer is built on the one below it, and only the signal layer calls
`XmlReader`. That keeps its quirks in one place, and is where an async variant
over `XmlReader.ReadAsync` will be added later.

```scheme
;; ---- Options --------------------------------------------------------------

(type (: DtdMode (Union (: DtdProhibit #:tag prohibit)
                        (: DtdIgnore #:tag ignore)
                        (: DtdParse #:tag parse))))
(type (: WhitespaceMode (Union (: KeepWhitespace #:tag keep)
                               (: DropWhitespace #:tag drop))))

;; Every entry point takes these, with these defaults:
;;   (#:dtd DtdMode)                'prohibit
;;   (#:max-depth int)              512; deeper is an XmlError
;;   (#:max-characters long)        0 = no limit (MaxCharactersInDocument)
;;   (#:whitespace WhitespaceMode)  'keep
;; and the signal layer, parse-xml and xml-for-each also take
;;   (#:comments? bool)             #f

;; ---- Errors ---------------------------------------------------------------

(type (: XmlErrorKind (Union (: Malformed #:tag malformed)
                             (: DtdProhibited #:tag dtd-prohibited)
                             (: TooDeep #:tag too-deep)
                             (: LimitExceeded #:tag limit-exceeded))))
;; line and column are 0 when neither .NET nor the reader knows them.
(type (: XmlError (Record (: kind XmlErrorKind) (: message string) (: line int) (: column int))))

;; ---- Sources --------------------------------------------------------------

;; What a source is turned into. Opaque; users implement XmlSource for their
;; own types by calling xml-input on a source that already has one.
(type (: XmlInput #:opaque (Union ...)))
(def/trait (XmlSource %s)
  (: xml-input (-> %s XmlInput)))
;; impls: string (the XML text itself), TextInputPort, Stream, ByteInputPort
;; (through its AsStream, so XmlReader detects the encoding), and XmlFile.
(type (: XmlFile #:opaque (Record (: path string))))
(: xml-file (-> string XmlFile))

;; ---- 1. Signals, pulled ---------------------------------------------------

(type (: XmlSignal (Union (: XmlStart XmlName XmlAttrs (Option XmlPos))
                          XmlEnd
                          (: XmlData string)
                          (: XmlComment string)
                          (: XmlPI string string)
                          (: XmlDoctype Doctype))))
(type (: XmlSignals #:opaque (Record ...)))

;; Opens the source, calls proc, closes what it opened. The XmlSignals value
;; is valid only inside proc.
(: call-with-xml-signals (-> %s (-> XmlSignals %a)
                             (#:dtd DtdMode) (#:max-depth int) (#:max-characters long)
                             (#:whitespace WhitespaceMode) (#:comments? bool)
                             %a)
   (where (XmlSource %s)))
;; None at the end of the document. After an error, the same error again.
(: xml-next! (-> XmlSignals (Result XmlError (Option XmlSignal))))
;; Right after an XmlStart: skips that element's subtree, its XmlEnd included.
;; Anywhere else it raises.
(: xml-skip-element! (-> XmlSignals (Result XmlError Unit)))

;; ---- 2. The SSAX fold -----------------------------------------------------

;; Every handler defaults to passing the seed through, so with none given the
;; seed comes back unchanged.
(: xml-fold (-> %s %seed
                (#:new-level (-> XmlName XmlAttrs (Option XmlPos) %seed %seed))
                (#:finish (-> XmlName XmlAttrs (Option XmlPos) %seed %seed %seed)) ;; parent-seed, seed
                (#:text (-> string %seed %seed))
                (#:comment (-> string %seed %seed))
                (#:pi (-> string string %seed %seed))
                (#:doctype (-> Doctype %seed %seed))
                (#:dtd DtdMode) (#:max-depth int) (#:max-characters long)
                (#:whitespace WhitespaceMode)
                (Result XmlError %seed))
   (where (XmlSource %s)))

;; ---- 3. The tree fold (Xmlm's input_tree) ---------------------------------

;; The caller chooses the tree type. A #:comment or #:pi handler answers None
;; to leave the node out, which is what the defaults do. The result is the
;; root element's value; what is outside the root is not folded.
(: xml-tree-fold (-> %s
                     (-> XmlName XmlAttrs (Option XmlPos) (List %a) %a)   ;; element
                     (-> string %a)                                       ;; text
                     (#:comment (-> string (Option %a)))
                     (#:pi (-> string string (Option %a)))
                     (#:dtd DtdMode) (#:max-depth int) (#:max-characters long)
                     (#:whitespace WhitespaceMode)
                     (Result XmlError %a))
   (where (XmlSource %s)))

;; ---- 4. The whole document ------------------------------------------------

;; Built on xml-fold with the Node constructors. PIs are always kept, comments
;; when #:comments? says so, in the prolog and epilog as in the tree.
(: parse-xml (-> %s
                 (#:dtd DtdMode) (#:max-depth int) (#:max-characters long)
                 (#:whitespace WhitespaceMode) (#:comments? bool)
                 (Result XmlError Document))
   (where (XmlSource %s)))

;; ---- 5. Burst mode --------------------------------------------------------

;; Builds a Node for each outermost element with this name, calls f on it and
;; drops it. Memory is one matching element, not the document.
(: xml-for-each (-> %s XmlName (-> Node void)
                    (#:dtd DtdMode) (#:max-depth int) (#:max-characters long)
                    (#:whitespace WhitespaceMode) (#:comments? bool)
                    (Result XmlError Unit))
   (where (XmlSource %s)))
```

### Signals

- `<br/>` gives `XmlStart` then `XmlEnd`: consumers never see
  `IsEmptyElement`. It is read *before* moving to the attributes, which
  changes it.
- Adjacent Text, CDATA, Whitespace and SignificantWhitespace nodes become one
  `XmlData`, even across a comment the reader was told to ignore.
- Whitespace outside the root element is not data, and the XML declaration is
  not a signal.
- Attributes in `http://www.w3.org/2000/xmlns/` are filtered out.
- `'drop` drops merged data that is whitespace only (space, tab, CR, LF). That
  includes the space in `<b>x</b> <i>y</i>`, so the text reads `xy`.
- `XmlReader.Depth` is checked against `#:max-depth` at each element: a
  document with 512 nested elements is accepted by default, and one with 513
  is `too-deep`.
- `#:comments? #f` sets `IgnoreComments`, so comments are never read.

### Errors

`XmlException` is caught only at the boundary, around the reader calls, and
turned into a `Result`; `XmlReader.Create` is inside it, because it reads
eagerly and can itself throw. Exceptions raised by user handlers pass through
unchanged, because no `try` surrounds a handler call. A `try` compiles to a
closure and a delegate, so there is one per signal, not one per reader call.

The kinds, and how each is recognized:

| Kind             | From                                                                 |
|------------------|----------------------------------------------------------------------|
| `too-deep`       | the depth check above                                                |
| `dtd-prohibited` | a DOCTYPE under `'prohibit`; under `'parse`, an external DTD or entity refused by `XmlResolver.ThrowingResolver` (an `XmlException` wrapping one) |
| `limit-exceeded` | `MaxCharactersInDocument` or `MaxCharactersFromEntities`             |
| `malformed`      | every other `XmlException`                                           |

The DTD and limit errors are plain `XmlException`s with nothing but their
message to tell them apart, and .NET throws them without line information. The
message is compared, exactly, with the one .NET gives for a tiny reference
document, computed when an error needs classifying. There is no substring test,
because a malformed document can quote those words back in its own error. Those
errors have line and column 0: .NET gives them no position, and by the time
they arrive the reader has reset its own `IXmlLineInfo` to 0 as well.

Streaming limitation, documented: a fold that fails at node N has already run
its handlers for nodes 1 to N−1.

### Safety

- `'prohibit` is the default. `'parse` sets `XmlResolver` to
  `XmlResolver.ThrowingResolver`, so external entities and DTDs are never
  resolved, and `MaxCharactersFromEntities` to 10 000 000.
- The loop that runs once per signal is a named `let`, which compiles to a C#
  `while (true)` loop, so a flat document with a million elements does not grow
  the stack.
- `xml-fold` keeps open elements on an explicit stack, a list of frames.

### Resources

Whoever opens a resource closes it. An `XmlFile` is opened through the
prelude's `FS` effect (`open-read-stream`), so a fake filesystem answers for
it, and closed when the call returns. A port or stream given by the caller is
left open (`CloseInput` false).

## `(text xml write)`

```scheme
(type (: XmlWriteError (Record (: message string))))

(: node->xml (-> Node TextOutputPort
                 (#:indent? bool) (#:prefixes (List (Tuple string string)))
                 (Result XmlWriteError Unit)))
(: node->xml-string (-> Node
                        (#:indent? bool) (#:prefixes (List (Tuple string string)))
                        (Result XmlWriteError string)))
(: document->xml (-> Document TextOutputPort
                     (#:indent? bool) (#:prefixes (List (Tuple string string)))
                     (#:declaration? bool)
                     (Result XmlWriteError Unit)))
(: document->xml-string (-> Document
                            (#:indent? bool) (#:prefixes (List (Tuple string string)))
                            (#:declaration? bool)
                            (Result XmlWriteError string)))
```

- `System.Xml.XmlWriter` does all escaping. The one piece of hand-written
  markup is the doctype (decision 3).
- **Namespaces.** `#:prefixes` is an alist from prefix to URI. A hinted prefix
  is declared once, on the root, for the namespaces the tree uses. Otherwise:
  - An element uses a prefix already in scope for its namespace, then a hinted
    one, then a default namespace declaration. An element in no namespace
    inside a default namespace gets `xmlns=""`.
  - An attribute in a namespace always needs a prefix, because the default
    namespace does not apply to attributes. It uses one in scope, then a
    hinted one, then a generated `ns1`, `ns2` and so on, declared on its
    element.
  - The `xml` prefix (`xml:lang`, `xml:space`) is predeclared and never
    declared.
  - A hint whose prefix is not an NCName, or is `xml` or `xmlns`, raises.
- **Invalid content is an `XmlWriteError`, not a crash.** `XmlWriter` refuses
  invalid characters such as U+0000 with an `ArgumentException`, which becomes
  an `XmlWriteError`. Some invalid content it silently changes instead, so
  these are checked before calling it:
  - a comment containing `--` or ending in `-` (.NET writes `- -`);
  - a PI target that is not an NCName, or is `xml` in any case (.NET drops a
    PI named `xml` without a word);
  - PI data containing `?>` (.NET writes `? >`).

  I/O exceptions from the port pass through. Output already written before an
  error stays written.
- **Indentation.** `#:indent?` defaults to `#f`. Indenting is done by hand with
  `WriteWhitespace`, with `XmlWriterSettings.Indent` off: an element with a text
  child gets no added whitespace inside it, nor does anything below it.
  `XmlWriter`'s own rule decides only when it meets the text, and has already
  indented the elements before it by then.
- **No HTML rules here**: no void elements, raw text elements or boolean
  attributes. Those belong in `(text html)`.

## .NET members, and how they are reached

All checked by compiling and running against `net10.0`:

- **Names:** `XName.Get`, `.LocalName`, `.NamespaceName`, `.ToString`;
  `XmlConvert.VerifyNCName`, `VerifyName`, `VerifyPublicId`, `VerifyXmlChars`.
- **Reader settings:** the `XmlReaderSettings` setters `DtdProcessing`,
  `XmlResolver` (set-only; `#:set` works), `MaxCharactersFromEntities`,
  `MaxCharactersInDocument`, `CloseInput`, `IgnoreComments`; the enum
  `DtdProcessing`; the static `XmlResolver.ThrowingResolver` (.NET 7 and
  later).
- **Reader:** `XmlReader.Create` over a `TextReader` or a `Stream`; `Read`,
  `NodeType` (compared as `(cast int ...)`), `LocalName`, `NamespaceURI`,
  `Name`, `Value`, `Depth`, `IsEmptyElement`, `MoveToNextAttribute`,
  `MoveToElement`, `Skip`, `GetAttribute`; `Dispose` through `with-open`.
- **Line information:** `IXmlLineInfo.LineNumber` and `LinePosition`, reached
  by `(:is System.Xml.IXmlLineInfo li)`, since `XmlReader` does not implement
  it statically; `XmlException.LineNumber`, `LinePosition` and
  `InnerException`.
- **Byte ports:** `BjoByteInputPort.AsStream`.
- **Writer:** `XmlWriter.Create` over a `TextWriter` or a .NET `StringBuilder`
  (the prelude's `StringBuilder` is a different type); `XmlWriterSettings`
  setters; `WriteStartElement`, `WriteEndElement`, `WriteAttributeString`,
  `WriteString`, `WriteComment`, `WriteProcessingInstruction`,
  `WriteWhitespace`, `WriteRaw`, `LookupPrefix`, `Flush`, `Dispose`.

Members that can return null (`GetAttribute`, `LookupPrefix`,
`InnerException`) are wrapped as `Option` at the boundary with
`(match (cast Object x) ((:is System.String s) (Some s)) (_ None))`, the idiom
`(std http)` uses.

## Compiler findings

Fixed, as the first part of this work:

- **A.** A literal passed by keyword was not checked against the keyword
  parameter's type: `(f #:dtd 'parse)` was a type error, `Mode` against
  `Symbol`. Keyword arguments now get what positional ones get. Test:
  `250_keyword_literals.bjo`.
- **B.** A type variable that among the parameters only keyword ones mention
  gave C# CS0411 at the call, because no type arguments were written and C#
  does not infer through the `Option` a generic keyword parameter is. Such
  bindings are now in `TraitRegistry.KeywordOnlyGenerics`, and their calls
  write type arguments out. Keyword arguments also no longer switch off the
  guess for a call whose positional arguments are all lambdas. Test:
  `251_keyword_only_generics.bjo`, across a module boundary too.
- **Found while fixing B.** A keyword parameter named like a module-level
  function was emitted as a call to that function inside the function
  declaring it, since keyword parameters are not alpha-renamed. Fixed in
  codegen; covered by the same test.
- **Found while writing the tests.** A top-level function's keyword *default*
  was inferred on its own too, so `(defun (f #:dtd 'prohibit) ...)` failed as
  A did. It is now checked against the declared keyword type.
- **D.** A `fun` whose body ended in a call to a void `import/extern` member
  (`#:set` included) got the body's `System.Void` for its result and was
  emitted as an `Action`, which C# would not pass where a `(-> ... void)`, a
  `Func<..., Unit>`, was wanted: CS0126 inline, CS1503 bound first. A local
  function with no return type written did the same. Both now return the unit;
  only a lambda whose expected type comes from a real .NET `Action` parameter
  stays one. Test: `259_lambda_results.bjo`.
- **F.** A lambda's body was inferred without the result type its context
  expects, so `(fun (x) 'fast)` was a `Symbol` where a `(-> bool Mode)` was
  wanted. The body is now checked against the expected result, as a
  `defun`'s is. Same test.

Still open, with the workaround the library uses:

- **C.** A separate signature's keyword parameters are matched to the
  `defun`'s by position, not by name; a different order gives a misleading
  type error. Write them in the same order.
- **E.** A `def/trait` whose method signature names a type declared below the
  trait does not resolve it: "srclib/Box2 against Box2". Declare the type
  first.
- A keyword default may name only the parameters before it, so `#:else` comes
  first in `node-case`.
- A .NET parameter of delegate type `Action` cannot be declared in an
  `import/extern` signature: `void` there means the unit, so `(-> %T void)` is
  a `Func` and does not match. Not needed by this library.

## Status

Implemented as described above. The tests are `TestFiles/252_xml_nodes.bjo`,
`253_xml_signals.bjo`, `254_xml_fold.bjo`, `255_xml_parse.bjo`,
`256_xml_for_each.bjo`, `257_xml_write.bjo` and `258_xml_print.bjo`, and the
whole suite passes. The million-element fold and the 100 000-deep documents run
in well under a second each.

Open questions, which are also written in the `Open:` list at the head of the
module each belongs to:

- **`ThrowingResolver` refuses every DOCTYPE that names an external DTD** under
  `'parse`, an XHTML one for instance, so such a document can only be read
  under `'ignore`, which loses the doctype (decision 10). Without it, .NET 10's
  default resolver skips an external DTD silently and expands an external
  entity to nothing, which would keep the doctype but quietly change content.
  Kept as specified: refusing loudly is the safer default.
- **`xml-skip-element!` and `#:max-depth`** (decision 7).
- **`#:whitespace 'drop` and `xml:space="preserve"`.** `'drop` drops
  whitespace-only text everywhere, even inside `xml:space="preserve"`. The
  writer does respect it when indenting.
- **Performance.** A `try` compiles to a closure and a delegate, so the signal
  layer allocates two small objects per signal. A `try` emitted as a statement
  would remove that; it is a compiler change, not a library one.

## Order of work, and tests

Each step with its tests in `TestFiles/`:

1. **Names and Node.**
   - The constructor merges adjacent text and drops empty text, in trees built
     by user code too.
   - Duplicate attributes and namespace declarations raise.
   - `node=?`, `text-content`, `descendants` and `node-case`.
   - A tree 100 000 deep through `node=?`, `text-content` and `=`.
2. **Signals.**
   - `<br/>` gives Start then End, and an empty element.
   - `a<![CDATA[b]]>c` is one text, `abc`.
   - `&amp;` and `&#233;` are expanded.
   - No `xmlns` attributes, and namespaced elements and attributes get the
     right `XmlName`.
   - `xml-skip-element!` on empty and non-empty elements.
   - A DOCTYPE with the defaults is `dtd-prohibited`.
   - Depth 513 is `too-deep`.
   - Malformed XML has the right line and column.
3. **`xml-fold`.**
   - A flat document of 1 000 000 elements counted with an `int` seed.
   - Depth 100 000 under `#:max-depth 200000`.
   - A handler that raises gives the original exception, not an `XmlError`.
4. **`xml-tree-fold` and `parse-xml`.**
   - `'drop`, including the space between inline elements.
   - `#:comments?`, the prolog and the epilog.
   - The doctype under `'parse`.
   - Billion laughs under `'parse` is `limit-exceeded`, quickly.
   - Depth 100 000.
   - Every source: string, port, stream (with an encoding declared in the
     bytes), byte port, file, and a file under `with-fake-fs`.
5. **`xml-for-each`.** Only outermost matches are visited, and nested ones
   arrive inside them.
6. **The writer.**
   - Parse, write, parse again is `node=?` to the first; prefixes may differ.
   - An attribute in a namespace gets a prefix, and `xml:lang` is not
     declared.
   - `#:prefixes` hints, and generated `ns1`.
   - U+0000, `--` in a comment, a PI named `xml`, and `?>` in PI data are
     `XmlWriteError`s.
   - Indentation never enters mixed content.
   - Depth 100 000. Only elements that declare a namespace keep a scope
     frame, so looking up a prefix costs the number of declaring ancestors, not
     the depth; with a frame per element, writing 100 000 levels was quadratic
     and took ten seconds.
7. **The printer.** The SXML form, Clark notation, and depth 100 000.

## Out of scope, and not blocked

- `(text xml match)`: sxml-match patterns, elaborated at compile time into
  `:view`s over `element-view`, the predicates and the accessors.
- `(text xml path)`: typed SXPath combinators over the same views and
  `descendants`.
- `(text xml decode)`: decoders from `Node` to records, with error paths and
  the positions in `element-pos`.
- `(text html)`: HTML serialization, `node->html`, and a phantom-typed DSL over
  `Node`.
- Async parsing: a `defbjouble` over `XmlReader.ReadAsync` in the signal
  layer only.
- HTML parsing, XML Schema, XSLT, XML 1.1 and DTD validation.
