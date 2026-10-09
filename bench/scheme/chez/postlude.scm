;; Replaces the suite's Chez-postlude.scm, which imports in the middle of
;; the program. An R6RS top-level program can only import at its start.
(define (this-scheme-implementation-name)
  "chez")
