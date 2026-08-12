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
    (type . 0) (type-rec . 0) (bjo . 0)
    ;; One distinguished form, then the body.
    (defun . 1) (defbjo . 1) (def . 1) (def/mutable . 1)
    (def/trait . 1) (def/impl . 1) (def/impl/extern . 1) (def/macro . 1)
    (when . 1) (unless . 1) (match . 1) (try . 1)
    (with-open . 1) (parameterize . 1) (fun . 1) (record . 1)
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
    (if (and (elt state 2)
             (not (looking-at "\\sw\\|\\s_")))
        ;; The head is not a symbol — a list, as in ((f x) y) — so there is no
        ;; rule to look up and the arguments line up under the first one.
        (progn
          (unless (> (save-excursion (forward-line 1) (point))
                     calculate-lisp-indent-last-sexp)
            (goto-char calculate-lisp-indent-last-sexp)
            (beginning-of-line)
            (parse-partial-sexp (point) calculate-lisp-indent-last-sexp 0 t))
          (backward-prefix-chars)
          (current-column))
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
          (funcall method state indent-point normal-indent)))))))

;;; ---------------------------------------------------------------------------
;;; Font lock

(defconst bjo-special-forms
  '("if" "when" "unless" "match" "let" "let/mono" "letrec" "loop" "seq" "seql"
    "do" "try" "with-open" "parameterize" "fun" "and" "or" "not" "set!"
    "cast" "record" "record-get" "record-set!" "yield" "yield-from"
    "bjo" "bjoroutine" "spawn-evt" "task->event"
    "import" "import/extern" "import/class" "export" "re-export" "include"
    "type" "type-rec")
  "Forms the compiler gives a meaning of their own.")

(defvar bjo-font-lock-keywords
  `(;; (defun (name args) ...) — the name is inside the parameter list.
    ;; `def/macro' is written the same way and names a function too, even though
    ;; the compiler is the only thing that ever calls it.
    ("(\\(defun\\|defbjo\\|def/macro\\)\\_>\\s-*(\\s-*\\([^ \t\n()]+\\)"
     (1 font-lock-keyword-face)
     (2 font-lock-function-name-face))

    ;; (def/trait (Name %a) ...), (def/impl (Trait Type) ...)
    ("(\\(def/\\(?:trait\\|impl\\(?:/extern\\)?\\)\\)\\s-*(\\s-*\\([^ \t\n()]+\\)"
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

    ;; Arrows, in signatures.
    ("\\_<-\\(?:bjo\\)?->\\_>" . font-lock-keyword-face)

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
