;;; bjomode-tests.el --- Tests for bjomode.el -*- lexical-binding: t; -*-

;; This Source Code Form is subject to the terms of the Mozilla Public
;; License, v. 2.0. If a copy of the MPL was not distributed with this
;; file, You can obtain one at http://mozilla.org/MPL/2.0/.

;;; Commentary:

;; Indentation and the failure-part face. Run from the checkout:
;;
;;   emacs -Q --batch -l bjomode.el -l bjomode-tests.el \
;;         -f ert-run-tests-batch-and-exit
;;
;; An indentation test is a snippet indented as it should be. The test takes
;; every line's indentation away and indents the result again, so the mode has
;; to arrive at the snippet from nothing rather than leave it be.

;;; Code:

(require 'ert)
(require 'bjo-mode)

(defconst bjo-tests--dir
  (file-name-directory (or load-file-name buffer-file-name))
  "The checkout, where the files of `bjo-tests--corpus' are.")

(defmacro bjo-tests--in-buffer (text &rest body)
  "Evaluate BODY in a `bjo-mode' buffer holding TEXT, without its hooks."
  (declare (indent 1))
  `(with-temp-buffer
     (insert ,text)
     (let ((bjo-mode-hook nil))
       (bjo-mode))
     ,@body))

(defun bjo-tests--reindented (text)
  "TEXT with every line's indentation taken away and indented again."
  (bjo-tests--in-buffer
      (replace-regexp-in-string "^[ \t]+" "" text)
    (indent-region (point-min) (point-max))
    (buffer-string)))

(defmacro bjo-deftest-indent (name doc text)
  "Define test NAME, with DOC, that TEXT indents as it is written."
  (declare (indent 1) (doc-string 2))
  `(ert-deftest ,(intern (format "bjo-indent/%s" name)) ()
     ,doc
     (should (equal (bjo-tests--reindented ,text) ,text))))

;;; ---------------------------------------------------------------------------
;;; Indentation

(bjo-deftest-indent defun
  "A `defun' indents its body two spaces."
  "(defun (double x)
  (println x)
  (* x 2))
")

(bjo-deftest-indent call-alignment
  "A call lines its arguments up under the first."
  "(defun (f x)
  (string-append \"a\"
                 (->str x)
                 \"b\"))
")

(bjo-deftest-indent if
  "Both arms of an `if' are four spaces in."
  "(defun (f x)
  (if (> x 0)
      \"positive\"
      \"not\"))
")

(bjo-deftest-indent let
  "A `let' binds, then its body is two spaces in."
  "(defun (f)
  (let ((a 1)
        (b 2))
    (+ a b)))
")

(bjo-deftest-indent named-let
  "A named `let' has its name and bindings before the body."
  "(defun (count-down n)
  (let loop ((i n)
             (acc Nil))
    (if (= i 0)
        acc
        (loop (- i 1) (Cons i acc)))))
")

(bjo-deftest-indent match
  "`match' arms are a body, and an arm's forms line up after its pattern."
  "(defun (describe xs)
  (match xs
    (Nil \"empty\")
    ((Cons head _) (println head)
                   \"some\")))
")

(bjo-deftest-indent match-guard
  "A guard is part of the pattern: the body is one in, or under a form after it."
  "(defun (sign n)
  (match n
    ((Some x) #:when (> x 0)
     (println x)
     \"positive\")
    ((Some x) #:when (< x 0) (println x)
                             \"negative\")
    (_ \"zero\")))
")

(bjo-deftest-indent def-leave
  "A failure keyword is two in from the `def', and its arms line up."
  "(defun (explain s)
  (def (Ok n) (parse-int s)
    :leave ((Err \"zero\") \"a zero is not a number here\")
           ((Err why) (str \"refused (\" why \")\")))
  (str \"accepted \" (int->string n)))
")

(bjo-deftest-indent def-propagate
  "A failure keyword alone on its line, after a long scrutinee."
  "(defun (read rd)
  (def (Ok v) (read-value rd 1)
    :propagate)
  v)
")

(bjo-deftest-indent def-star
  "A `def*' lines its clauses up, and each clause's failure is two in."
  "(defun (greet key number)
  (def* ((Some found) (lookup key) :leave-with (Err \"no such key\"))
        ((Ok n) (parse-int number)
          :leave ((Err why) (Err why))))
  (Ok (str found \"/\" (int->string n))))
")

(bjo-deftest-indent cond
  "`cond' clauses: a test's body under the test, a `:match' body under it."
  "(defun (classify text env)
  (cond
    ((string-empty? text) \"empty\")
    (:def first (string-first text))
    (:def (Some port) (map-try-ref env \"PORT\")
      :leave-with 8080)
    (:match (Some n) (string->int text)
     (println \"a number\")
     n)
    ((char-upper-case? first)
     \"capital\")
    (else \"word\")))
")

(bjo-deftest-indent defbjouble
  "A `defbjouble' branch, `(#:sync ...)', has a body two spaces in."
  "(defbjouble (port-eof? p)
  (#:sync
    (clr-eof p))
  (#:bjo (clr-eof-await p)))
")

(bjo-deftest-indent vec-elements
  "A vec's elements line up under its first, a list or a string as well."
  "(def parts [(numeric 1234567.891 #:precision 2) \"  \"
            (numeric 0.5 #:precision 3) \"  \"
            (percent 0.125)])
(def words [\"one\" \"two\"
            \"three\"])
(def cells #[1 2
             3])
")

(bjo-deftest-indent let-bindings
  "A `let''s bindings line up under the first, several on a line or not."
  "(defun (f args)
  (let go ((left (vec->list args)) (seed 0)
           (stats #f))
    (go left seed stats))
  (let* ((a 1) (b 2)
         (c 3))
    (+ a b c)))
")

(bjo-deftest-indent comprehension
  "Braces pair like parens, and a comprehension aligns its clauses."
  "(defun (evens xs)
  {(* x 2) (:for x xs)
           (:when (even? x))})
")

(bjo-deftest-indent loop-clauses
  "`loop' aligns its clauses, and its `=>' is two in from the paren."
  "(defun (mean ys)
  (loop (:for y ys)
        (:acc s (summing y))
        (:acc n (counting y))
    => (if (= n 0) 0 (/ s n))))
")

(bjo-deftest-indent loop-result-nested
  "A `=>' goes with its own `loop', inner or outer."
  "(defun (pairs xss)
  (loop (:for xs xss)
        (:acc out (folding Nil
                           (loop (:for x xs)
                                 (:acc inner (listing x))
                             => inner)))
    => out))
")

(bjo-deftest-indent subloop-result
  "A `:subloop''s `=>' is two in from its own paren."
  "(defun (averages rows)
  (loop (:for row rows)
        (:subloop (:for y row)
                  (:acc s (summing y))
          => (avg s))
        (:acc out (listing avg))
    => out))
")

(bjo-deftest-indent seql-result
  "A `seql''s `=>' is two in from its paren, as a `loop''s is."
  "(defun (with-final n)
  (seql (:for i (range 0 n))
        (:yield i)
    => 99))
")

(bjo-deftest-indent arrow-elsewhere
  "A `=>' outside a `loop' is an argument like any other."
  "(defun (f)
  (g 1
     => 2))
")

(bjo-deftest-indent with-return
  "`with-return' names its escape, then has a body."
  "(defun (first-even xs)
  (with-return ret
    (for-each (fun (x) (when (even? x) (ret x))) xs)
    0))
")

(bjo-deftest-indent doc-form
  "A `(:doc ...)' form's clauses are a body, two in."
  "(:doc clamp-all
  (summary \"Clamps every number.\")
  (arg lo \"The least it may answer.\"))
")

(bjo-deftest-indent defun-flag
  "A flag after a `defun''s head leaves the body two in."
  "(defun (ripe-value r) #:inline
  (match r
    ((MkRipe n) n)))
")

(bjo-deftest-indent impl-where
  "An `impl''s where clause leaves the body two in."
  "(impl (Iterable (Enumerated %s)) (where (Iterable %s))
  (type %elem (Tuple int (assoc Iterable elem %s)))
  (defun (iterable-start e) (Tuple 0 e)))
")

(bjo-deftest-indent try
  "A `try' is a body, its keywords as well."
  "(defun (f early)
  (try
    (println \"body\")
    #:finally
    (when early (println \"left\"))))
")

(bjo-deftest-indent with-macro
  "A head that begins with `with-' has a body, after what is on its line."
  "(defun (f)
  (with-scope
    (println \"inside\")
    1)
  (with-fmt current-ellipsis \"...\"
    (println \"also\")))
")

(bjo-deftest-indent character-literal
  "A delimiter in a character literal opens and closes nothing."
  "(defun (f c)
  (if (= c #\\()
      #\\;
      #\\)))
")

;;; ---------------------------------------------------------------------------
;;; Real files, which are indented as the mode indents them

(defconst bjo-tests--corpus
  '("TestFiles/007_named_let.bjo"
    "TestFiles/085_comprehension.bjo"
    "TestFiles/154_lib.bjo"
    "TestFiles/220_def.bjo"
    "TestFiles/226_cond_clauses.bjo"
    "TestFiles/270_loop_subloop_form.bjo"
    "bjo/git.bjo")
  "Files the mode leaves as they are, from every corner of the language.")

(ert-deftest bjo-indent/corpus ()
  "Indenting a file written in the house style changes no line of it."
  (dolist (file bjo-tests--corpus)
    (let ((path (expand-file-name file bjo-tests--dir)))
      (when (file-exists-p path)
        (let ((text (with-temp-buffer
                      (insert-file-contents path)
                      (buffer-string))))
          (bjo-tests--in-buffer text
            (indent-region (point-min) (point-max))
            (should (equal (cons file (buffer-string)) (cons file text)))))))))

;;; ---------------------------------------------------------------------------
;;; The failure part's face

(defun bjo-tests--failure-text (text)
  "The parts of TEXT that `bjo-mode' gives the `bjo-failure' face, in order."
  (bjo-tests--in-buffer text
    (font-lock-ensure)
    (let ((parts nil)
          (pos (point-min)))
      (while (< pos (point-max))
        (let ((next (next-single-property-change pos 'face nil (point-max)))
              (face (get-text-property pos 'face)))
          (when (memq 'bjo-failure (ensure-list face))
            (if (and parts (= (cdar parts) pos))
                (setcdr (car parts) next)
              (push (cons pos next) parts)))
          (setq pos next)))
      (mapcar (lambda (p) (buffer-substring-no-properties (car p) (cdr p)))
              (nreverse parts)))))

;;; Signatures

(defun bjo-tests--face-of (text word)
  "The face font lock gives the first WORD in TEXT."
  (bjo-tests--in-buffer text
    (font-lock-ensure)
    (goto-char (point-min))
    (search-forward word)
    (get-text-property (match-beginning 0) 'face)))

(ert-deftest bjo-signature-faces ()
  "A named signature colours its name; one without a name colours no type as one."
  (should (eq (bjo-tests--face-of "(: add1 (-> int int))" ":") 'font-lock-keyword-face))
  (should (eq (bjo-tests--face-of "(: add1 (-> int int))" "add1") 'font-lock-function-name-face))
  (should (eq (bjo-tests--face-of "(: (-> int int))\n(defun (add1 x) x)" ":") 'font-lock-keyword-face))
  (should (eq (bjo-tests--face-of "(: int)\n(def seven 7)" ":") 'font-lock-keyword-face))
  (should-not (eq (bjo-tests--face-of "(: int)\n(def seven 7)" "int") 'font-lock-function-name-face)))

(ert-deftest bjo-failure/def ()
  "A `def''s failure part has the face, and the binding has not."
  (should (equal (bjo-tests--failure-text
                  "(def (Some x) (lookup k) :leave-with 0)")
                 '(":leave-with 0"))))

(ert-deftest bjo-failure/plain-def ()
  "A `def' that cannot fail has nothing in the face."
  (should (equal (bjo-tests--failure-text "(def x (+ 1 2))") nil)))

(ert-deftest bjo-failure/multi-line ()
  "A failure part over several lines is one part, arms and all."
  (should (equal (bjo-tests--failure-text
                  "(def (Ok n) (parse-int s)
  :leave ((Err e) e)
         (_ 0))")
                 '(":leave ((Err e) e)
         (_ 0)"))))

(ert-deftest bjo-failure/def-star ()
  "Each clause of a `def*' that has a failure part has its own."
  (should (equal (bjo-tests--failure-text
                  "(def* ((Some a) (f) :propagate)
      (b (g))
      ((Ok c) (h) :default 0))")
                 '(":propagate" ":default 0"))))

(ert-deftest bjo-failure/comment ()
  "A `def' in a comment heads nothing."
  (should (equal (bjo-tests--failure-text
                  ";; (def (Some x) y :propagate)\n(f 1)")
                 nil)))

;;; Raw strings

(defun bjo-tests--string-map ()
  "One character per position of the buffer: s inside a string, . outside."
  (syntax-propertize (point-max))
  (mapconcat (lambda (p) (if (nth 3 (syntax-ppss p)) "s" "."))
             (number-sequence (point-min) (1- (point-max))) ""))

(defun bjo-tests--typed (text)
  "A `bjo-mode' buffer's string map after TEXT is typed a character at a time.
The buffer is propertized after each character, as redisplay does while
one types, so a fence placed early has to survive what comes after it."
  (with-temp-buffer
    (let ((bjo-mode-hook nil)) (bjo-mode))
    (dolist (ch (string-to-list text))
      (insert ch)
      (syntax-propertize (point-max)))
    (bjo-tests--string-map)))

(defmacro bjo-tests--strings (name text map)
  "A test that TEXT, loaded and typed, has the string MAP after it.
MAP has an s for each position inside a string and a . for each outside."
  (declare (indent 1))
  `(ert-deftest ,(intern (format "bjo-raw-string/%s" name)) ()
     (should (equal (bjo-tests--in-buffer ,text (bjo-tests--string-map)) ,map))
     (should (equal (bjo-tests--typed ,text) ,map))))

;; The string runs from after the first opening quote up to the last closing
;; one; what follows the string is code.
(bjo-tests--strings quotes-inside
  "(def x \"\"\"hej hej \"hej\" \"\"\") (f y)"
  "........sssssssssssssssssss.......")

(bjo-tests--strings four-quotes
  "(def x \"\"\"\"a \"\"\" in it\"\"\"\") (f y)"
  "........ssssssssssssssssss.......")

(bjo-tests--strings backslash
  "(def x \"\"\"C:\\new\\\"\"\") (f y)"
  "........ssssssssssss.......")

(bjo-tests--strings two-in-a-row
  "(def a \"\"\"x\"\"\")\n(def b \"\"\"y \"z\" \"\"\")\n(f y)"
  "........ssssss..........sssssssssss.......")

;; Over several lines, the closing quotes are on a line without the opening
;; ones. Typed, they are fenced only if propertizing starts at the opening
;; quotes again, which `bjo--extend-to-raw-string' arranges.
(bjo-tests--strings several-lines
  "(println \"\"\"\n  a \"b\" c\n  \"\"\")\n(f y)"
  "..........ssssssssssssssssss.......")

(ert-deftest bjo-raw-string/edited-inside ()
  "A quote typed inside a raw string over several lines, and the closing
quotes deleted and typed again, leave the code after the string code."
  (bjo-tests--in-buffer "(println \"\"\"\n  a b c\n  \"\"\")\n(f y)"
    (syntax-propertize (point-max))
    (goto-char 17)
    (insert "\"")
    (should (equal (bjo-tests--string-map) "..........sssssssssssssssss......."))
    (goto-char (point-min))
    (search-forward "\"\"\")")
    (delete-char -4)
    (syntax-propertize (point-max))
    (dolist (ch (string-to-list "\"\"\")"))
      (insert ch)
      (syntax-propertize (point-max)))
    (should (equal (bjo-tests--string-map) "..........sssssssssssssssss......."))))

(provide 'bjomode-tests)

;;; bjomode-tests.el ends here
