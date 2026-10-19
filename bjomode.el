;;; bjomode.el --- Major mode for Bjolang -*- lexical-binding: t; -*-

;; This Source Code Form is subject to the terms of the Mozilla Public
;; License, v. 2.0. If a copy of the MPL was not distributed with this
;; file, You can obtain one at http://mozilla.org/MPL/2.0/.
;;
;; As a special exception to the Mozilla Public License, version 2.0, if you
;; compile your application source code and portions of this software are
;; embedded into the generated object code or executable form as a normal
;; consequence of the compilation process (such as inline functions,
;; templates, generics, or macros), you may redistribute such embedded portions
;; in such object code or executable form without complying with the source code
;; availability requirements or notice obligations of Section 3 of the MPL 2.0.

;;; Commentary:

;; Keys:
;;
;;   C-c C-c  run the file (`bjo run'), C-u first for arguments
;;   C-c C-p  run the project around it
;;   C-c C-z  switch to the REPL, starting it; and back again from it
;;   C-M-x    send the definition at point to the REPL, with its signature
;;   C-x C-e  send the form before point
;;   C-c C-r  send the region
;;   C-c C-l  send the buffer
;;
;; The tests are in bjomode-tests.el:
;;
;;   emacs -Q --batch -l bjomode.el -l bjomode-tests.el \
;;         -f ert-run-tests-batch-and-exit
;;
;; Indentation follows Scheme's rules, which is what Bjolang's syntax is:
;;
;;   - a form with a body indents it two spaces;
;;   - `if' indents its arms four;
;;   - anything else lines its arguments up under the first one.
;;
;; And one that is Bjolang's own: a `loop''s or a `:subloop''s `=>' is two
;; spaces in from the paren, out of line with the clauses it follows.
;;
;; The third is the default, so a form only appears in `bjo-indent-forms' below
;; if it has a body. A call, a `loop' clause list, a `def*' clause list and a
;; comprehension all align.

;;; Code:

(require 'lisp-mode)
(require 'seq)

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

(defun bjo--propertize-raw-strings (end)
  "Mark raw strings, three or more quotes around text, up to END.

Emacs reads `\"\"\"a\"\"\"' as three strings, and gets a quote inside one
wrong. So the first opening quote and the last closing one become string
fences, and everything between them is one string. The closing quotes are
the next run of exactly as many as opened it: a run that long cannot be
inside the text, so this finds the right one in any string that compiles."
  (while (re-search-forward "\"\\{3,\\}" end t)
    (let* ((open (match-beginning 0))
           (n (- (match-end 0) open)))
      (unless (or (nth 8 (save-excursion (syntax-ppss open)))
                  (eq (char-before open) ?\\))
        (put-text-property open (1+ open) 'syntax-table (string-to-syntax "|"))
        (when (re-search-forward (format "[^\"]\\(\"\\{%d\\}\\)\\(?:[^\"]\\|\\'\\)" n) nil t)
          (put-text-property (1- (match-end 1)) (match-end 1)
                             'syntax-table (string-to-syntax "|"))
          (goto-char (match-end 1)))))))

(defun bjo--extend-to-raw-string (start end)
  "Start a propertize from the opening quotes of a raw string around START.

Emacs propertizes again from the line of a change. Inside a raw string
over several lines, that line does not hold the opening quotes, and the
closing ones look like quotes inside a string, so they are never fenced and
the rest of the buffer is read as string. A raw string is the one kind of
string with a fence, so a string state of t at START means START is inside
one. Answers (OPEN . END) to begin at its opening quotes, or nil."
  (let ((state (save-excursion (syntax-ppss start))))
    (when (and (eq (nth 3 state) t) (< (nth 8 state) start))
      (cons (nth 8 state) end))))

(defun bjo-syntax-propertize (start end)
  "Mark character literals and raw strings between START and END.
A delimiter in a character literal is punctuation, and a raw string is
fenced; see `bjo--propertize-raw-strings'."
  (goto-char start)
  (while (re-search-forward bjo--char-literal-re end t)
    (put-text-property (match-beginning 1) (match-end 1)
                       'syntax-table (string-to-syntax ".")))
  (goto-char start)
  (bjo--propertize-raw-strings end))

;;; ---------------------------------------------------------------------------
;;; Indentation

(defconst bjo-indent-forms
  '(;; Body only.
    (seq . 0) (letrec . 0) (export . 0) (re-export . 0)
    (import . 0) (import/extern . 0) (import/class . 0)
    (type . 0) (type-rec . 0) (bjo . 0) (begin . 0)
    (spawn . 0) (spawn/daemon . 0) (spawn/detached . 0) (with-shield . 0)
    ;; Makron biblioteket skriver, som är former för den som skriver bjolang.
    (cond . 0) (time-it . 0)
    ;; One distinguished form, then the body.
    (defun . 1) (defbjo . 1) (defbjouble . 1) (def/mutable . 1)
    (def/trait . 1) (impl . 1) (impl/extern . 1) (def/macro . 1) (def/pattern . 1)
    (type/derive . 1)
    (when . 1) (unless . 1) (match . 1) (case . 1) (try . 1)
    (with-open . 1) (parameterize . 1) (parameterize* . 1) (fun . 1)
    (let* . 1) (syntax-match . 1) (when-let . 1) (if-let . 1)
    (with-cancel . 1) (with-deadline . 1) (with-response . 1) (with-run . 1)
    ;; `(with-return ret body ...)' — the escape's name comes first, as the
    ;; name of a named `let' does.
    (with-return . 1)
    ;; The name and the collector, then the body.
    (let/mono . 2)
    ;; `(def pattern scrutinee)', and the failure part the refutable shapes add
    ;; after it, such as `(def pattern scrutinee :leave-with value)' or
    ;; `(def pattern scrutinee :leave arm ...)'.
    ;;
    ;; `defun' rather than a count, because the count differs by shape and the
    ;; indentation does not: a failure part comes after a pattern and a
    ;; scrutinee, the value of a plain `(def name value)' comes after a name
    ;; alone, and both are a body of one form two spaces in.
    (def . defun)
    ;; Both arms four spaces in, which is what the third distinguished form
    ;; buys: the second and third line up with each other.
    (if . 3))
  "Forms with a body, and how many forms precede it.
`defun' is the count that varies: everything after the head is the
body, however many forms it follows. That is what a `def' needs,
which is written three ways and indents the same in all of them.

Anything absent aligns its arguments under the first one, which is
what `loop', `seql', `do', a `def*' clause list and every ordinary
call want.")

(defun bjo-let-indent (state indent-point normal-indent)
  "Indent `let', which binds a name first when it is a named let."
  (skip-chars-forward " \t")
  (if (looking-at "[[:alpha:]]")
      ;; (let loop ((x 0)) body) — the name is a distinguished form of its own.
      (lisp-indent-specform 2 state indent-point normal-indent)
    (lisp-indent-specform 1 state indent-point normal-indent)))

(defun bjo-def-star-indent (_state _indent-point _normal-indent)
  "Indent `def*', whose clauses line up under the first one.

Answering nil is how `calculate-lisp-indent' is told to use the
normal indentation, and `def*' has to say so out loud: its head
begins with `def', which the rule below would otherwise read as a
definition and indent its clauses two spaces in."
  nil)

(defconst bjo--failure-keyword-re
  "#?:\\(?:leave-with\\|leave\\|propagate\\|default\\)\\_>"
  "A keyword that starts a `def' clause's failure part, in either spelling.")

(defun bjo--column-at (pos)
  "The column of POS."
  (save-excursion (goto-char pos) (current-column)))

(defun bjo--clause-elements (open limit)
  "The pattern and what follows it, for the `def' clause opening at OPEN.

A list of start positions up to LIMIT, the pattern's first. Nil
when the list at OPEN is not a clause: a `def', a `cond' `:def',
or a parenthesised clause of a `def*'."
  (save-excursion
    (goto-char open)
    (let ((head-is-def (looking-at "(\\s-*:?def\\_>"))
          (in-def-star (let ((parent (nth 1 (syntax-ppss open))))
                         (and parent
                              (save-excursion
                                (goto-char parent)
                                (looking-at "(\\s-*def\\*\\_>"))))))
      (when (or head-is-def (and in-def-star (eq (char-after open) ?\()))
        (goto-char (1+ open))
        (let ((starts nil))
          (condition-case nil
              (while (progn (forward-comment (buffer-size)) (< (point) limit))
                (push (point) starts)
                (forward-sexp 1))
            (scan-error nil))
          (setq starts (nreverse starts))
          ;; A `def' and a `:def' have their head before the pattern; a
          ;; `def*' clause starts with it. The `def*' head itself is no clause.
          (cond (head-is-def (cdr starts))
                ((and in-def-star
                      (save-excursion
                        (goto-char (car starts))
                        (looking-at "def\\*\\_>")))
                 nil)
                (t starts)))))))

(defun bjo--failure-indent (indent-point state)
  "The column for the line at INDENT-POINT in a `def' clause's failure part.

A failure keyword is two columns in from the clause's open paren,
as a body is, and the arms after `:leave' line up under the first
arm. Nil for any other line, which indents as it would anyway."
  (let ((open (elt state 1)))
    (when open
      (save-excursion
        (goto-char indent-point)
        (skip-chars-forward " \t")
        (let* ((here (point))
               (elements (bjo--clause-elements open (1+ here))))
          (when (cdr elements)
            (let ((leave (seq-find (lambda (p)
                                     (and (< p here)
                                          (save-excursion
                                            (goto-char p)
                                            (looking-at "#?:leave\\_>"))))
                                   elements)))
              (cond
               ((looking-at bjo--failure-keyword-re)
                (+ (bjo--column-at open) 2))
               (leave
                (let ((first-arm (seq-find (lambda (p) (> p leave)) elements)))
                  (bjo--column-at (if (and first-arm (< first-arm here))
                                      first-arm
                                    leave))))))))))))

(defun bjo--result-form-p (open)
  "Whether the list opening at OPEN is a `loop' or a `:subloop'.
Those are the forms whose result a `=>' introduces."
  (save-excursion
    (goto-char open)
    (looking-at "(\\s-*:?\\(?:loop\\|subloop\\)\\_>")))

(defun bjo--result-indent (indent-point state)
  "The column for a `=>' at INDENT-POINT in a `loop' or a `:subloop'.

Two columns in from the form's open paren, out of line with the
clauses, so that where the result starts stands out. Nil for any
other line."
  (let ((open (elt state 1)))
    (when (and open (bjo--result-form-p open))
      (save-excursion
        (goto-char indent-point)
        (skip-chars-forward " \t")
        (when (looking-at "=>\\_>")
          (+ (bjo--column-at open) 2))))))

;; `calculate-lisp-indent' asks `lisp-indent-function' only about a line whose
;; previous form starts on the list's first line. The second arm after a
;; `:leave' follows one that does not, so the failure part is decided here,
;; ahead of it, and in `bjo-mode' buffers only. TAB, `indent-region' and
;; `indent-sexp' all come through this function.
;;
;; Inside a clause the answer is (COLUMN START) rather than a column:
;; `lisp-indent-region' reuses a plain column for every following line at the
;; same depth without asking again, and in a clause the next line may be a
;; failure keyword or an arm that indents differently. The same goes for a
;; `loop', whose `=>' is out of line with the clauses above it.
(defun bjo--calculate-indent (calculate &rest args)
  "Answer the failure part's or the `=>''s column, or call CALCULATE with ARGS."
  (if (not (derived-mode-p 'bjo-mode))
      (apply calculate args)
    (let* ((state (save-excursion (beginning-of-line) (syntax-ppss)))
           (open (nth 1 state))
           (no-reuse (and open
                          (or (bjo--clause-elements open (save-excursion
                                                           (beginning-of-line)
                                                           (point)))
                              (bjo--result-form-p open))))
           (indent (or (save-excursion
                         (beginning-of-line)
                         (or (bjo--failure-indent (point) state)
                             (bjo--result-indent (point) state)))
                       (apply calculate args))))
      (if (and no-reuse (integerp indent))
          (list indent open)
        indent))))

(advice-add 'calculate-lisp-indent :around #'bjo--calculate-indent)

(defun bjo-indent-function (indent-point state)
  "Indent a Bjolang form at INDENT-POINT, given parser STATE.

A form whose head has a `bjo-indent-function' property indents by
it; one whose head begins with `def' indents its body; everything
else aligns under its first argument. A `def' clause's failure part
is not decided here; see `bjo--calculate-indent'."
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
      (let* ((head-column (current-column))
             (function (buffer-substring (point)
                                         (progn (forward-sexp 1) (point))))
             (method (get (intern-soft function) 'bjo-indent-function)))
        (cond
         ;; `(:match pattern value body ...)' is a `cond' clause, and its body
         ;; goes under the keyword: one column in, as an ordinary clause's
         ;; goes under its test. Every other keyword-headed list, a loop
         ;; clause or a `(:doc ...)', is a call or a form like any other.
         ((equal function ":match")
          head-column)
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
    "loop" "seq" "seql" "do" "begin" "try" "with-open" "with-return" "fun"
    "parameterize" "parameterize*"
    "and" "or" "not" "set!" "cast"
    ;; Makron biblioteket skriver. En som skriver bjolang ser ingen skillnad
    ;; mot det parsern kan, så de står här.
    "cond" "when-let" "if-let" "some->" "time-it" "syntax-match"
    "with-cancel" "with-deadline" "with-shield" "with-response" "with-run"
    "def/json-type"
    ;; `record' och `struct' står inte här: konstruktion namnger sin typ, så
    ;; `(Point (x 1) (y 2))', och de nakna formerna avvisas av parsern.
    ;; `struct*' är accepterade synonymer för `record*'.
    "record-ref" "record-set" "record-set!"
    "struct-ref" "struct-set" "struct-set!"
    "yield" "yield-from" "syntax-quote"
    "bjo" "spawn" "spawn/daemon" "spawn/detached"
    "bjoroutine" "spawn-evt" "task->event"
    "import" "import/extern" "import/class" "export" "re-export" "include"
    "type" "type-rec" "type/derive"
    ;; Importmodifierare. De står bara inuti en `import', men de är former
    ;; kompilatorn ger en egen betydelse.
    "only" "except" "rename"
    "prefix" "prefix-types" "prefix-defs" "postfix" "postfix-defs")
  "Forms the compiler gives a meaning of their own.")

;;; The failure part of a `def'
;;
;; A `def' whose pattern may not match changes what the body around it evaluates
;; to, and the only thing that says so is the part that says what it evaluates
;; to *instead*:
;;
;;   (def pattern scrutinee :leave-with value)
;;   (def pattern scrutinee :leave (pattern body ...) ...)
;;   (def pattern scrutinee :propagate)
;;   (def pattern scrutinee :default value)
;;
;; and the same in each clause of a `(def* clause ...)'. The part is small next
;; to the binding it hangs off, so the failure gets a background of its own, and
;; reading straight past one takes an effort.
;;
;; A failure part is a balanced form and is regularly several lines long, which
;; no regexp matches. It is found by scanning from the head instead: the head is
;; what a regexp does match, and what it turns up is handed to font-lock's
;; anchored matching, one part per clause.

(defface bjo-failure
  '((((class color) (min-colors 88) (background light)) :background "#ffd2dd")
    (((class color) (min-colors 88) (background dark)) :background "#5c2734")
    (((class color)) :background "magenta")
    (t :inverse-video t))
  "Face for what a `def' or `def*' clause produces when its pattern does not match."
  :group 'bjolang)

(defvar bjo--failures nil
  "Failure parts of the `def' form being fontified, as (BEG . END) pairs.
Filled in when the form's head matches, and taken one at a time by
`bjo-match-failure' — which is what colours a `def*' clause by
clause.")

(defvar bjo--failure-resume nil
  "Where point goes once a `def' form's failure parts are fontified.
The head's own position, so that the search resumes inside the
form and a `def' written in a `:leave' arm is found in its turn.")

(defun bjo--skip-blanks (limit)
  "Move past whitespace and comments, stopping at LIMIT."
  (forward-comment (buffer-size))
  (when (> (point) limit)
    (goto-char limit)))

(defun bjo--clause-failure (limit)
  "Return the failure part of the clause at point, as a (BEG . END) pair.

Point is on the clause's pattern and LIMIT is where its forms run
out: the closing paren of a `def*' clause, or of the `def' itself.
The answer is nil when the clause has no failure part, which is the
whole of `(def name value)' and of a clause whose pattern cannot fail."
  (condition-case nil
      (save-excursion
        ;; The pattern and the scrutinee, which every clause has.
        (forward-sexp 2)
        (bjo--skip-blanks limit)
        (when (< (point) limit)
          ;; Everything after the scrutinee is the failure part: a keyword
          ;; alone, a keyword and its value, or `:leave' and its arms.
          (let ((beg (point))
                (end nil))
            (while (progn (bjo--skip-blanks limit) (< (point) limit))
              (forward-sexp 1)
              (when (<= (point) limit)
                (setq end (point))))
            (when end
              (cons beg end)))))
    (scan-error nil)))

(defun bjo--form-failures (start)
  "Return the failure parts of the `def' or `def*' form opening at START.
A list of (BEG . END) pairs in source order, one per clause that
has one."
  (save-excursion
    (goto-char start)
    (let ((limit (condition-case nil
                     (progn (forward-sexp 1) (1- (point)))
                   (scan-error nil))))
      (when limit
        (goto-char (1+ start))
        (let ((star (looking-at "def\\*")))
          (forward-sexp 1)
          (bjo--skip-blanks limit)
          (if (not star)
              ;; A `def' is one clause, with its pattern unparenthesised at the
              ;; head and the rest of the form its own.
              (let ((failure (bjo--clause-failure limit)))
                (and failure (list failure)))
            ;; A `def*' is one clause per form, each parenthesised — including
            ;; when there is only one — and each says for itself what a failure
            ;; produces.
            (let ((failures nil))
              (while (< (point) limit)
                (let ((clause-end (condition-case nil
                                      (scan-sexps (point) 1)
                                    (scan-error nil))))
                  (if (null clause-end)
                      (goto-char limit)
                    (when (eq (char-after) ?\()
                      (save-excursion
                        (forward-char 1)
                        (bjo--skip-blanks (1- clause-end))
                        (let ((failure (bjo--clause-failure (1- clause-end))))
                          (when failure
                            (push failure failures)))))
                    (goto-char clause-end)
                    (bjo--skip-blanks limit))))
              (nreverse failures))))))))

(defun bjo--failures-ahead ()
  "Note the failure parts of the `def' just matched, and answer the last end.

Evaluated as the pre-form of the anchored highlighter below, where
what it answers becomes the limit the matcher is given: a failure
part is regularly lines below the head, and the limit is what lets
font-lock reach it. Answering point leaves the limit where it was,
which is what a form with nothing to colour wants."
  (let ((start (match-beginning 0)))
    (setq bjo--failure-resume (point))
    (setq bjo--failures
          ;; A `def' inside a string or a comment heads nothing.
          (unless (nth 8 (syntax-ppss start))
            (bjo--form-failures start)))
    (if bjo--failures
        (cdr (car (last bjo--failures)))
      (point))))

(defun bjo-match-failure (limit)
  "Set the match data around the next failure part, which must end before LIMIT.

The parts were found when the form's head matched, so this only
walks them: font-lock's anchored matching asks for one match per
call, and a `def*' has one per clause."
  (let ((failure (pop bjo--failures)))
    (when (and failure (<= (cdr failure) limit))
      (set-match-data (list (car failure) (cdr failure)))
      (goto-char (cdr failure))
      t)))

;; Bound by `font-lock-default-fontify-region' around the call to each of
;; `font-lock-extend-region-functions', and declared nowhere a caller can see.
(defvar font-lock-beg)

(defun bjo-extend-region ()
  "Extend the region being fontified to the top-level form it begins inside.

A failure part is found from the `def' that heads it, so a region
starting below one would not know it was in one. Answering non-nil
is how font-lock is told the region moved."
  (let ((start (car (nth 9 (syntax-ppss font-lock-beg)))))
    (when (and start (< start font-lock-beg))
      (setq font-lock-beg start)
      t)))

(defvar bjo-font-lock-keywords
  `(;; (defun (name args) ...) — the name is inside the parameter list.
    ;; `def/macro' and `def/pattern' are written the same way and name a
    ;; function too, even though the compiler is the only thing that ever calls
    ;; one.
    ;; `defbjouble' names one too, and writes two bodies under it:
    ;; (defbjouble (name args) (#:sync ...) (#:bjo ...)).
    ("(\\(defun\\|defbjouble\\|defbjo\\|def/macro\\|def/pattern\\)\\_>\\s-*(\\s-*\\([^ \t\n()]+\\)"
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

    ;; (with-return ret ...) — the escape's name. It is applied, `(ret x)', so
    ;; it reads as the function name it is spelled like; the head itself is one
    ;; of `bjo-special-forms' and is coloured by the rule below.
    ("(with-return\\_>\\s-+\\([^ \t\n()]+\\)"
     1 font-lock-function-name-face)

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
    ("%[[:alnum:]_-]+" . font-lock-type-face)

    ;; (def pattern scrutinee ...), (def* clause ...) and a `cond' clause
    ;; (:def pattern scrutinee ...) — the head, and then the failure part of
    ;; every clause that has one, found by scanning from here. `defun', `defbjo'
    ;; and `def/mutable' are other heads and are matched by the rules above:
    ;; `\_>' is what keeps `def' from standing for them.
    ;;
    ;; Last in the list, and appended rather than overriding, so that a failure
    ;; part keeps every colour the rules above gave what is inside it and gains
    ;; only the background.
    ("(\\(def\\*?\\|:def\\)\\_>"
     (1 font-lock-keyword-face)
     (bjo-match-failure (bjo--failures-ahead) (goto-char bjo--failure-resume)
                        (0 'bjo-failure append))))
  "Font lock keywords for `bjo-mode'.")

;;; ---------------------------------------------------------------------------
;;; Mode

;;;###autoload
(define-derived-mode bjo-mode lisp-data-mode "Bjolang"
  "Major mode for editing Bjolang source files."
  :group 'bjolang
  :syntax-table bjo-mode-syntax-table
  (setq-local font-lock-defaults '(bjo-font-lock-keywords))
  ;; A failure part runs over as many lines as it needs, and font-lock works a
  ;; line at a time: this is what marks one as a unit, so that editing a line in
  ;; the middle of a `:leave' redraws the whole of it. `bjo-extend-region' is the
  ;; other half, for a region that begins in a form nothing has fontified yet.
  (setq-local font-lock-multiline t)
  (add-hook 'font-lock-extend-region-functions #'bjo-extend-region nil t)
  (setq-local syntax-propertize-function #'bjo-syntax-propertize)
  (add-hook 'syntax-propertize-extend-region-functions
            #'bjo--extend-to-raw-string nil t)
  (setq-local lisp-indent-function #'bjo-indent-function)
  (setq-local comment-start ";")
  (setq-local comment-add 1)
  ;; Spaces, as every existing source uses: a tab renders at whatever width the
  ;; reader's editor says, and alignment under a first argument then only holds
  ;; for whoever wrote it.
  (setq-local indent-tabs-mode nil)
  (pcase-dolist (`(,sym . ,n) bjo-indent-forms)
    (put sym 'bjo-indent-function n))
  (put 'let 'bjo-indent-function #'bjo-let-indent)
  (put 'def* 'bjo-indent-function #'bjo-def-star-indent))

;;; ---------------------------------------------------------------------------
;;; Running
;;
;; `bjo-run' runs the file with `bjo run FILE', and `bjo-run-project' runs the
;; project around it with `bjo run'. Either way `bjo' decides what is stale and
;; builds it, so there is nothing for Emacs to work out first.
;;
;; The output goes to a comint buffer, which is also the program's stdin: what is
;; typed there is sent a line at a time, and `C-c C-d' sends end of file. The
;; compiler's `at file.bjo:LINE' is a link to the line, as in a compilation.

(require 'comint)
(require 'compile)

(defcustom bjo-program
  (or (executable-find "bjo")
      (expand-file-name "bjo/bjo"
                        (file-name-directory (or load-file-name
                                                 buffer-file-name
                                                 default-directory))))
  "The `bjo' command: on the PATH, or else the one in this checkout."
  :type 'file
  :group 'bjolang)

(defconst bjo-manifest-name "manifest.bjodat"
  "The file that makes the directory it is in a project.")

(defvar bjo-run-arguments-history nil
  "Arguments given to programs run by `bjo-run' and `bjo-run-project'.")

(defvar-local bjo-run--command nil
  "How the program in this buffer was started, as (DIRECTORY ARGS).
What `bjo-run-again' starts it with.")

(add-to-list 'compilation-error-regexp-alist-alist
             '(bjolang "\\_<at \\([^ \t\n:]+\\.\\(?:bjo\\|protobjo\\)\\):\\([0-9]+\\)" 1 2))

(defvar bjo-run-mode-map
  (let ((map (make-sparse-keymap)))
    (define-key map (kbd "C-c C-r") #'bjo-run-again)
    map)
  "Keymap for `bjo-run-mode'.")

(define-derived-mode bjo-run-mode comint-mode "Bjo-Run"
  "Major mode for the output of a Bjolang program, and its input.
\\<bjo-run-mode-map>\\[bjo-run-again] runs it again; \\[comint-send-eof] sends end of file."
  :group 'bjolang
  (setq-local comint-prompt-read-only nil)
  ;; The program runs on a pty, which echoes what is sent to it, and comint has
  ;; already shown it.
  (setq-local comint-process-echoes t)
  (setq-local compilation-error-regexp-alist '(bjolang))
  (compilation-shell-minor-mode 1))

(defun bjo--read-arguments (ask)
  "The program's arguments, as a list: read when ASK, and none otherwise."
  (when ask
    (split-string-and-unquote
     (read-string "Arguments: " (car bjo-run-arguments-history)
                  'bjo-run-arguments-history))))

(defun bjo--save-before-run ()
  "Save the current buffer, and offer to save every other Bjolang buffer.
A file the program imports is compiled from disk, not from its buffer."
  (when (and (buffer-file-name) (buffer-modified-p))
    (save-buffer))
  (save-some-buffers nil (lambda () (derived-mode-p 'bjo-mode))))

(defun bjo--start (name directory args)
  "Run `bjo' with ARGS in DIRECTORY, in the buffer `*bjo: NAME*'."
  (let* ((buffer (get-buffer-create (format "*bjo: %s*" name)))
         (process (get-buffer-process buffer)))
    (when (process-live-p process)
      (if (yes-or-no-p (format "%s is still running; kill it? " name))
          (progn (delete-process process)
                 ;; The sentinel writes into the buffer this is about to clear.
                 (sit-for 0.1))
        (user-error "Still running")))
    (with-current-buffer buffer
      (let ((inhibit-read-only t))
        (erase-buffer))
      (setq default-directory (file-name-as-directory directory))
      (bjo-run-mode)
      (setq bjo-run--command (list directory args))
      (insert (propertize (format "$ bjo %s\n" (combine-and-quote-strings args))
                          'face 'font-lock-comment-face))
      (comint-exec buffer "bjo" (expand-file-name bjo-program) nil args))
    (display-buffer buffer)
    buffer))

(defun bjo-run (&optional ask)
  "Run the current file with `bjo run', its output in a buffer of its own.
The buffer is the program's stdin too. With a prefix argument ASK, read
arguments to pass to the program."
  (interactive "P")
  (let ((file (or (buffer-file-name) (user-error "Not visiting a file"))))
    (bjo--save-before-run)
    (bjo--start (file-name-nondirectory file)
                (file-name-directory file)
                (append (list "run" (file-name-nondirectory file))
                        (bjo--read-arguments ask)))))

(defun bjo-run-project (&optional ask)
  "Build the project around the current file and run its entry.
The project is the nearest directory above with a `manifest.bjodat'.
With a prefix argument ASK, read arguments to pass to the program."
  (interactive "P")
  (let ((root (or (locate-dominating-file default-directory bjo-manifest-name)
                  (user-error "Not in a project: no %s here or above"
                              bjo-manifest-name)))
        (args (bjo--read-arguments ask)))
    (bjo--save-before-run)
    (bjo--start (file-name-nondirectory (directory-file-name root))
                root
                ;; `bjo run .' is how a project's program is given arguments.
                (if args (append (list "run" ".") args) (list "run")))))

(defun bjo-run-again ()
  "Run the program in this buffer again, as it was started."
  (interactive)
  (pcase bjo-run--command
    (`(,directory ,args)
     (bjo--start (substring (buffer-name) 6 -1) directory args))
    (_ (user-error "Nothing was run in this buffer"))))

(define-key bjo-mode-map (kbd "C-c C-c") #'bjo-run)
(define-key bjo-mode-map (kbd "C-c C-p") #'bjo-run-project)

;;; ---------------------------------------------------------------------------
;;; The REPL
;;
;; `bjo repl' in a comint buffer, one per project, or per directory outside one:
;; it is started where `bjo repl' finds the project's packages, and a relative
;; `(import "x.bjo")' in an entry is relative to there. Under Emacs the REPL
;; reads plain lines (it sees INSIDE_EMACS), and a form still open at the end
;; of a line makes it wait for the next.
;;
;; From a source buffer, forms are sent as entries and shown in the REPL as if
;; typed there. A definition is sent with the signature written just above it,
;; which is what an entry needs of a `defun' that does not write its own types.
;; In the REPL buffer, `completion-at-point' asks the REPL itself, through
;; `:complete', so a name an earlier entry defined is offered too.

(defconst bjo-repl-prompt-regexp "^\\(?:bjo\\|\\.\\.\\.\\)> "
  "The REPL's prompts: `bjo> ' for an entry, `...> ' for more of one.")

(defvar-local bjo-repl--source nil
  "The source buffer this REPL was last switched to from.")

(defun bjo-repl--root ()
  "Where the REPL for the current buffer runs: its project, or its directory."
  (let ((dir (if buffer-file-name
                 (file-name-directory buffer-file-name)
               default-directory)))
    (file-name-as-directory
     (expand-file-name (or (locate-dominating-file dir bjo-manifest-name) dir)))))

(defun bjo-repl--buffer-name (root)
  "The name of the REPL buffer for ROOT."
  (format "*bjo repl: %s*" (abbreviate-file-name (directory-file-name root))))

(defvar-local bjo-repl--lines-sent 0
  "How many lines have been sent to the REPL.")

(defvar-local bjo-repl--prompts-seen 0
  "How many prompts the REPL has printed.")

(defun bjo-repl--input-sender (process input)
  "Send INPUT to PROCESS, counting its lines for `bjo-repl--tidy'."
  (setq bjo-repl--lines-sent
        (+ bjo-repl--lines-sent 1 (seq-count (lambda (c) (eq c ?\n)) input)))
  (comint-simple-send process input))

(defun bjo-repl--tidy (output)
  "OUTPUT without the prompts that wait for nothing.

The REPL prints a prompt before each line it reads, and one more to
begin with. So a prompt waits for a line only while there have been
more prompts than lines sent; the rest are for lines that were sent
already, together: the `...> ' of every line of a definition after
the first, and the `bjo> ' of every entry sent at once but the first.
Shown, they would come before the answers, each on the line of the
next."
  (let ((prompt "\\(?:bjo\\|\\.\\.\\.\\)> ")
        (start 0)
        (kept ""))
    (while (string-match prompt output start)
      (setq kept (concat kept (substring output start (match-beginning 0))))
      (setq bjo-repl--prompts-seen (1+ bjo-repl--prompts-seen))
      ;; Only the last prompt of the output can be one that waits.
      (when (and (= (match-end 0) (length output))
                 (> bjo-repl--prompts-seen bjo-repl--lines-sent))
        (setq kept (concat kept (match-string 0 output))))
      (setq start (match-end 0)))
    (concat kept (substring output start))))

(defun bjo-repl--indirect-setup ()
  "Set up the buffer that fontifies and indents what is typed in the REPL.
It is in `bjo-mode', without the mode's hooks: one of them may start a
language server, and this buffer is no file."
  (let ((bjo-mode-hook nil))
    (bjo-mode)))

(defvar bjo-repl-mode-map
  (let ((map (make-sparse-keymap)))
    (define-key map (kbd "C-c C-z") #'bjo-repl-switch-back)
    map)
  "Keymap for `bjo-repl-mode'.")

(define-derived-mode bjo-repl-mode comint-mode "Bjo-REPL"
  "Major mode for a Bjolang REPL.
\\<bjo-repl-mode-map>\\[bjo-repl-switch-back] goes back to the source buffer; \
\\[comint-send-eof] leaves the REPL."
  :group 'bjolang
  :syntax-table bjo-mode-syntax-table
  (setq-local comint-prompt-regexp bjo-repl-prompt-regexp)
  (setq-local comint-prompt-read-only t)
  (setq-local comint-process-echoes nil)
  (setq-local comint-input-sender #'bjo-repl--input-sender)
  (add-hook 'comint-preoutput-filter-functions #'bjo-repl--tidy nil t)
  (add-hook 'completion-at-point-functions #'bjo-repl-completion-at-point nil t)
  (setq-local comment-start ";")
  ;; What is typed is coloured, and indented with TAB, by `bjo-mode' itself.
  (setq-local comint-indirect-setup-function #'bjo-repl--indirect-setup)
  (when (fboundp 'comint-fontify-input-mode)
    (comint-fontify-input-mode 1)))

(defun bjo-repl--process (buffer)
  "The live REPL process of BUFFER, or nil."
  (let ((process (and buffer (get-buffer-process buffer))))
    (and (process-live-p process) process)))

(defun bjo-repl--waiting-p (buffer)
  "Whether the REPL in BUFFER waits at its prompt for an entry.
The prompt may follow output on its line: `(print \"x\")' leaves `xbjo> '."
  (with-current-buffer buffer
    (let ((mark (process-mark (get-buffer-process buffer))))
      (and (> bjo-repl--prompts-seen bjo-repl--lines-sent)
           (save-excursion
             (goto-char mark)
             ;; `pos-bol', not `line-beginning-position': the prompt is a
             ;; field of its own, and that one stops at the field's edge.
             (looking-back "bjo> " (pos-bol)))))))

(defun bjo-repl--start (root)
  "The REPL buffer for ROOT, with the REPL started in it if it was not."
  (let ((buffer (get-buffer-create (bjo-repl--buffer-name root))))
    (unless (bjo-repl--process buffer)
      (with-current-buffer buffer
        (setq default-directory root)
        (bjo-repl-mode)
        ;; A pipe, not a pty: what is sent is not echoed back, and there is
        ;; no line discipline between the REPL and what it reads.
        (let ((process-connection-type nil))
          (comint-exec buffer "bjo-repl" (expand-file-name bjo-program) nil '("repl")))
        ;; The first prompt, so that what is sent next comes after the banner.
        (let ((process (get-buffer-process buffer))
              (deadline (+ (float-time) 30)))
          (while (and (process-live-p process)
                      (< (float-time) deadline)
                      (not (bjo-repl--waiting-p buffer)))
            (accept-process-output process 0.1)))))
    buffer))

(defun bjo-repl ()
  "Switch to the REPL for this buffer's project, starting it if need be."
  (interactive)
  (let ((source (current-buffer))
        (buffer (bjo-repl--start (bjo-repl--root))))
    (with-current-buffer buffer
      (setq bjo-repl--source source))
    (pop-to-buffer buffer)
    (goto-char (point-max))))

(defun bjo-repl-switch-back ()
  "Go back to the source buffer the REPL was last switched to from."
  (interactive)
  (if (buffer-live-p bjo-repl--source)
      (pop-to-buffer bjo-repl--source)
    (user-error "No source buffer to go back to")))

(defun bjo-repl--send (text)
  "Send TEXT to the REPL for this buffer, shown there as if typed.
Anything typed in the REPL and not yet sent is kept, after it."
  (let* ((buffer (bjo-repl--start (bjo-repl--root)))
         (process (or (bjo-repl--process buffer)
                      (user-error "The REPL did not start; see %s" (buffer-name buffer)))))
    (with-current-buffer buffer
      (let* ((mark (process-mark process))
             (pending (buffer-substring mark (point-max))))
        (delete-region mark (point-max))
        (goto-char (point-max))
        (insert (string-trim text))
        (comint-send-input)
        (insert pending)))
    (unless (get-buffer-window buffer 0)
      (display-buffer buffer))))

(defun bjo--forms-in (start end)
  "The top-level forms between START and END, each as its text.
Comments and blank lines between them are left out: on its own a
comment is an entry with nothing in it."
  (save-excursion
    (goto-char start)
    (let ((forms nil))
      (condition-case nil
          (while (progn (forward-comment (buffer-size)) (< (point) end))
            (let ((form-start (point)))
              (forward-sexp 1)
              (push (buffer-substring-no-properties form-start (min (point) end))
                    forms)))
        (scan-error (user-error "The region begins or ends inside a form")))
      (nreverse forms))))

(defun bjo-send-region (start end)
  "Send the forms between START and END to the REPL, each as an entry."
  (interactive "r")
  (let ((forms (bjo--forms-in start end)))
    (if forms
        (bjo-repl--send (string-join forms "\n"))
      (user-error "No form in the region"))))

(defun bjo-send-buffer ()
  "Send every form in the buffer to the REPL, each as an entry."
  (interactive)
  (bjo-send-region (point-min) (point-max)))

(defun bjo-send-definition ()
  "Send the top-level form around point to the REPL.
A signature written just above it, `(: name ...)', goes with it."
  (interactive)
  (save-excursion
    (let ((end (progn (end-of-defun) (point)))
          (start (progn (beginning-of-defun) (point))))
      (let ((signature
             (save-excursion
               (forward-comment (- (buffer-size)))
               (when (eq (char-before) ?\))
                 (let ((sig-end (point)))
                   (backward-sexp 1)
                   (and (looking-at "(:\\s-")
                        (point)
                        (cons (point) sig-end)))))))
        (bjo-send-region (if signature (car signature) start) end)))))

(defun bjo-send-last-sexp ()
  "Send the form before point to the REPL."
  (interactive)
  (bjo-send-region (save-excursion (backward-sexp 1) (point)) (point)))

(defun bjo-repl--complete (process text)
  "What the REPL at PROCESS offers at the end of TEXT, as a list of names."
  (comint-redirect-results-list-from-process
   process (concat ":complete " text) "^\\([^\t\n]+\\)\t" 1))

(defun bjo-repl-completion-at-point ()
  "Complete the word before point from what the REPL says could go there.
Only at the REPL's prompt: in the middle of an entry, a `:complete' line
would be read as more of it."
  (let* ((process (bjo-repl--process (current-buffer)))
         (mark (and process (process-mark process))))
    (when (and mark (>= (point) mark) (bjo-repl--waiting-p (current-buffer)))
      (let* ((end (point))
             (start (max mark (save-excursion (skip-syntax-backward "w_") (point))))
             (text (replace-regexp-in-string
                    "\n" " " (buffer-substring-no-properties mark end)))
             (names (bjo-repl--complete process text)))
        (list start end names :exclusive 'no)))))

(define-key bjo-mode-map (kbd "C-c C-z") #'bjo-repl)
(define-key bjo-mode-map (kbd "C-M-x") #'bjo-send-definition)
(define-key bjo-mode-map (kbd "C-x C-e") #'bjo-send-last-sexp)
(define-key bjo-mode-map (kbd "C-c C-r") #'bjo-send-region)
(define-key bjo-mode-map (kbd "C-c C-l") #'bjo-send-buffer)

;;;###autoload
(add-to-list 'auto-mode-alist '("\\.\\(bjo\\|protobjo\\)\\'" . bjo-mode))

(provide 'bjo-mode)
;;; bjomode.el ends here
