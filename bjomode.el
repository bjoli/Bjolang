;;; bjomode.el --- Major mode for Bjolang -*- lexical-binding: t; -*-

;; Indentation follows Scheme's rules, which is what Bjolang's syntax is:
;;
;;   - a form with a body indents it two spaces;
;;   - `if' indents its arms four;
;;   - anything else lines its arguments up under the first one.
;;
;; The third is the default, so a form only appears in `bjo-indent-forms' below
;; if it has a body. A call, a `loop' clause list and a comprehension all align.

;;; Code:

(require 'lisp-mode)

;; Bound by `calculate-lisp-indent' around the call to `lisp-indent-function',
;; and not declared anywhere public. `scheme.el' reads it the same way.
(defvar calculate-lisp-indent-last-sexp)

(defgroup bjolang nil
  "Major mode for editing Bjolang."
  :group 'languages)

;;; ---------------------------------------------------------------------------
;;; Syntax

(defvar bjo-mode-syntax-table
  (let ((table (make-syntax-table lisp-data-mode-syntax-table)))
    ;; A comprehension is delimited by braces, so they pair like parens: that is
    ;; what makes `C-M-f', `show-paren-mode' and indentation inside one work.
    (modify-syntax-entry ?\{ "(}" table)
    (modify-syntax-entry ?\} "){" table)
    ;; Characters that appear inside Bjolang identifiers. Without these, `\_>'
    ;; matches in the middle of `set!' or `list->vec', and `%a' is not one
    ;; symbol.
    (dolist (ch '(?- ?+ ?* ?/ ?= ?< ?> ?! ?? ?% ?: ?& ?~ ?^))
      (modify-syntax-entry ch "_" table))
    table)
  "Syntax table for `bjo-mode'.")

;; `#\(' is a character literal, not an open paren. Lisp's escape syntax already
;; covers it, but only because the backslash is there; the propertize rule below
;; is what stops `#\;' from starting a comment that swallows the rest of a line.
(defconst bjo--char-literal-re
  "#\\\\\\([][(){};\"'`,]\\)"
  "A character literal whose character would otherwise be a delimiter.")

(defun bjo-syntax-propertize (start end)
  "Mark delimiters inside character literals as punctuation, between START and END."
  (goto-char start)
  (while (re-search-forward bjo--char-literal-re end t)
    (put-text-property (match-beginning 1) (match-end 1)
                       'syntax-table (string-to-syntax "."))))

;;; ---------------------------------------------------------------------------
;;; Indentation

(defconst bjo-indent-forms
  '(;; Body only.
    (seq . 0) (letrec . 0) (export . 0) (re-export . 0)
    (import . 0) (import/extern . 0) (import/class . 0)
    (type . 0) (type-rec . 0) (bjo . 0) (begin . 0)
    ;; Makron biblioteket skriver, som är former för den som skriver bjolang.
    (cond . 0) (time-it . 0)
    ;; One distinguished form, then the body.
    (defun . 1) (defbjo . 1) (defbjouble . 1) (def . 1) (def/mutable . 1)
    (def/trait . 1) (impl . 1) (impl/extern . 1) (def/macro . 1)
    (type/derive . 1)
    (when . 1) (unless . 1) (match . 1) (case . 1) (try . 1)
    (with-open . 1) (parameterize . 1) (parameterize* . 1) (fun . 1)
    (let* . 1) (syntax-match . 1) (when-let . 1) (if-let . 1)
    (with-cancel . 1) (with-deadline . 1) (with-response . 1) (with-run . 1)
    ;; The name and the collector, then the body.
    (let/mono . 2)
    ;; Both arms four spaces in, which is what the third distinguished form
    ;; buys: the second and third line up with each other.
    (if . 3))
  "Forms with a body, and how many forms precede it.
Anything absent aligns its arguments under the first one, which is
what `loop', `seql', `do' and every ordinary call want.")

(defun bjo-let-indent (state indent-point normal-indent)
  "Indent `let', which binds a name first when it is a named let."
  (skip-chars-forward " \t")
  (if (looking-at "[[:alpha:]]")
      ;; (let loop ((x 0)) body) — the name is a distinguished form of its own.
      (lisp-indent-specform 2 state indent-point normal-indent)
    (lisp-indent-specform 1 state indent-point normal-indent)))

(defun bjo-indent-function (indent-point state)
  "Indent a Bjolang form at INDENT-POINT, given parser STATE.

A form whose head has a `bjo-indent-function' property indents by
it; one whose head begins with `def' indents its body; everything
else aligns under its first argument."
  (let ((normal-indent (current-column)))
    (goto-char (1+ (elt state 1)))
    (parse-partial-sexp (point) calculate-lisp-indent-last-sexp 0 t)
    (cond
     ;; En gren med ett nyckelord som huvud har bara kropp, och den dras in två
     ;; steg. `(#:sync ...)' och `(#:bjo ...)' i en `defbjouble' är formen.
     ;;
     ;; `#' är prefixsyntax, så punkten står på `:' här och `:' är en del av en
     ;; symbol — utan steget bakåt läses grenen som om `:bjo' vore dess huvud.
     ((and (elt state 2)
           (save-excursion (backward-prefix-chars) (looking-at "#:")))
      (lisp-indent-specform 0 state indent-point normal-indent))

     ;; The head is not a symbol — a list, as in ((f x) y) — so there is no
     ;; rule to look up and the arguments line up under the first one.
     ;;
     ;; Det är formen varje `match'- och `case'-gren har: `((Cons h t) ...)'
     ;; och `((1 2) ...)' har en lista som huvud, och en gren med flera satser
     ;; radar upp dem under den första. Står ingenting efter huvudet på dess
     ;; rad finns inget att rada upp under, och då gäller huvudet.
     ((and (elt state 2)
           (not (looking-at "\\sw\\|\\s_")))
      (backward-prefix-chars)
      (let ((head-column (current-column))
            (head-line (line-number-at-pos)))
        (condition-case nil
            (progn
              (forward-sexp 1)
              (skip-chars-forward " \t")
              (if (and (= head-line (line-number-at-pos))
                       (not (eolp))
                       (not (looking-at ";")))
                  (current-column)
                head-column))
          (scan-error head-column))))

     (t
      (let* ((function (buffer-substring (point)
                                         (progn (forward-sexp 1) (point))))
             (method (get (intern-soft function) 'bjo-indent-function)))
        (cond
         ((or (eq method 'defun)
              (and (null method)
                   (> (length function) 3)
                   (string-prefix-p "def" function)))
          (lisp-indent-defform state indent-point))
         ((integerp method)
          (lisp-indent-specform method state indent-point normal-indent))
         (method
          (funcall method state indent-point normal-indent))))))))

;;; ---------------------------------------------------------------------------
;;; Font lock

(defconst bjo-special-forms
  '("if" "when" "unless" "match" "case" "else" "let" "let*" "let/mono" "letrec"
    "loop" "seq" "seql" "do" "begin" "try" "with-open" "fun"
    "parameterize" "parameterize*"
    "and" "or" "not" "set!" "cast"
    ;; Makron biblioteket skriver. En som skriver bjolang ser ingen skillnad
    ;; mot det parsern kan, så de står här.
    "cond" "when-let" "if-let" "some->" "time-it" "syntax-match"
    "with-cancel" "with-deadline" "with-response" "with-run" "def/json-type"
    ;; `record' och `struct' står inte här: konstruktion namnger sin typ, så
    ;; `(Point (x 1) (y 2))', och de nakna formerna avvisas av parsern.
    ;; `struct*' är accepterade synonymer för `record*'.
    "record-ref" "record-set" "record-set!"
    "struct-ref" "struct-set" "struct-set!"
    "yield" "yield-from" "syntax-quote"
    "bjo" "bjoroutine" "spawn-evt" "task->event"
    "import" "import/extern" "import/class" "export" "re-export" "include"
    "type" "type-rec" "type/derive"
    ;; Importmodifierare. De står bara inuti en `import', men de är former
    ;; kompilatorn ger en egen betydelse.
    "only" "except" "rename"
    "prefix" "prefix-types" "prefix-defs" "postfix" "postfix-defs")
  "Forms the compiler gives a meaning of their own.")

(defvar bjo-font-lock-keywords
  `(;; (defun (name args) ...) — the name is inside the parameter list.
    ;; `def/macro' is written the same way and names a function too, even though
    ;; the compiler is the only thing that ever calls it.
    ;; `defbjouble' names one too, and writes two bodies under it:
    ;; (defbjouble (name args) (#:sync ...) (#:bjo ...)).
    ("(\\(defun\\|defbjouble\\|defbjo\\|def/macro\\)\\_>\\s-*(\\s-*\\([^ \t\n()]+\\)"
     (1 font-lock-keyword-face)
     (2 font-lock-function-name-face))

    ;; (def/trait (Name %a) ...), (impl (Trait Type) ...),
    ;; (type/derive (Eq) ...) — nästa symbol namnger ett trait eller en typ.
    ("(\\(def/trait\\|impl\\(?:/extern\\)?\\|type/derive\\)\\s-*(\\s-*\\([^ \t\n()]+\\)"
     (1 font-lock-keyword-face)
     (2 font-lock-type-face))

    ;; (def name ...), (def/mutable name ...)
    ("(\\(def\\(?:/mutable\\)?\\)\\_>\\s-+\\([^ \t\n()]+\\)"
     (1 font-lock-keyword-face)
     (2 font-lock-variable-name-face))

    ;; (: name type) — a signature. The colon stands alone, which is what
    ;; distinguishes it from a `:keyword'.
    ("(\\(:\\)\\s-+\\([^ \t\n()]+\\)"
     (1 font-lock-keyword-face)
     (2 font-lock-function-name-face))

    ;; The special forms, and the loop and monad clause keywords.
    (,(concat "(\\s-*" (regexp-opt bjo-special-forms t) "\\_>")
     1 font-lock-keyword-face)

    ;; Arrows, in signatures. `-bjo->' är en bjoroutine, `-?->' en parameter
    ;; som tar en funktion av endera färgen.
    ("\\_<-\\(?:bjo\\|\\?\\)?->\\_>" . font-lock-keyword-face)

    ;; `=>', som ger en loop sitt resultat.
    ("\\_<=>\\_>" . font-lock-keyword-face)

    ;; Booleans and character literals.
    ("#[tf]\\_>" . font-lock-constant-face)
    ("#\\\\\\(?:[][(){};\"'`,]\\|[[:alnum:]]+\\)" . font-lock-constant-face)

    ;; :keyword and #:keyword.
    ("#?:[[:alnum:]_?!*<>=/-]+" . font-lock-builtin-face)

    ;; 'symbol
    ("'[[:alpha:]][[:alnum:]_?!*<>=/-]*" . font-lock-constant-face)

    ;; %a, %elem — a type variable.
    ("%[[:alnum:]_-]+" . font-lock-type-face))
  "Font lock keywords for `bjo-mode'.")

;;; ---------------------------------------------------------------------------
;;; Mode

;;;###autoload
(define-derived-mode bjo-mode lisp-data-mode "Bjolang"
  "Major mode for editing Bjolang source files."
  :group 'bjolang
  :syntax-table bjo-mode-syntax-table
  (setq-local font-lock-defaults '(bjo-font-lock-keywords))
  (setq-local syntax-propertize-function #'bjo-syntax-propertize)
  (setq-local lisp-indent-function #'bjo-indent-function)
  (setq-local comment-start ";")
  (setq-local comment-add 1)
  ;; Spaces, as every existing source uses: a tab renders at whatever width the
  ;; reader's editor says, and alignment under a first argument then only holds
  ;; for whoever wrote it.
  (setq-local indent-tabs-mode nil)
  (pcase-dolist (`(,sym . ,n) bjo-indent-forms)
    (put sym 'bjo-indent-function n))
  (put 'let 'bjo-indent-function #'bjo-let-indent))

;;;###autoload
(add-to-list 'auto-mode-alist '("\\.\\(bjo\\|protobjo\\)\\'" . bjo-mode))

(provide 'bjo-mode)
;;; bjomode.el ends here
