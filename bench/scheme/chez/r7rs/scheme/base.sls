;; (scheme base) for Chez Scheme, enough for the R7RS benchmark sources.
;; Chez ships the R6RS libraries only. This library re-exports the R7RS
;; names that (chezscheme) binds with the R7RS meaning, and defines the
;; ones it lacks. It does not export all of (chezscheme), because a program
;; that defines a name it imports is an error, and the sources define names
;; such as `partition` and `random` that Chez binds.
(library (scheme base)
  (export
    * + - ... / < <= = => > >= _ abs and append apply assoc assq assv begin
    binary-port? boolean=? boolean? bytevector bytevector-copy
    bytevector-copy! bytevector-length bytevector-u8-ref bytevector-u8-set!
    bytevector? caar cadr call-with-current-continuation call-with-port
    call-with-values call/cc car case cdar cddr cdr ceiling char->integer
    char-ready? char<=? char<? char=? char>=? char>? char? close-input-port
    close-output-port close-port complex? cond cons current-error-port
    current-input-port current-output-port define define-syntax define-values
    denominator do dynamic-wind else eof-object eof-object? eq? equal? eqv?
    error even? exact exact-integer-sqrt exact? expt floor flush-output-port
    for-each gcd get-output-string guard if include inexact inexact?
    input-port? integer->char integer? lambda lcm length let let* let*-values
    let-syntax let-values letrec letrec* letrec-syntax list list->string
    list->vector list-copy list-ref list-tail list? make-bytevector make-list
    make-parameter make-string make-vector map max member memq memv min modulo
    negative? newline not null? number->string number? numerator odd?
    open-input-string open-output-string or output-port? pair? parameterize
    peek-char positive? procedure? quasiquote quote quotient raise
    raise-continuable rational? rationalize read-char real? remainder reverse
    round set! set-car! set-cdr! string string->list string->number
    string->symbol string->utf8 string-append string-copy string-copy!
    string-fill! string-for-each string-length string-ref string-set!
    string<=? string<? string=? string>=? string>? string? substring
    symbol->string symbol=? symbol? syntax-error syntax-rules textual-port?
    truncate unless unquote unquote-splicing utf8->string values vector
    vector->list vector-append vector-copy vector-copy! vector-fill!
    vector-for-each vector-length vector-map vector-ref vector-set! vector?
    when with-exception-handler write-char zero? exact-integer? square floor/
    floor-quotient floor-remainder truncate/ truncate-quotient
    truncate-remainder list-set! string-map write-string read-line
    string->vector vector->string define-record-type)
  (import (rename (chezscheme) (define-record-type r6rs:define-record-type)))

  (define (exact-integer? x) (and (integer? x) (exact? x)))
  (define (square x) (* x x))
  (define (floor-quotient n d) (floor (/ n d)))
  (define (floor-remainder n d) (modulo n d))
  (define (floor/ n d) (values (floor-quotient n d) (floor-remainder n d)))
  (define (truncate-quotient n d) (quotient n d))
  (define (truncate-remainder n d) (remainder n d))
  (define (truncate/ n d) (values (quotient n d) (remainder n d)))
  (define (list-set! l k v) (set-car! (list-tail l k) v))
  (define (string-map f s) (list->string (map f (string->list s))))
  (define write-string
    (case-lambda
      ((s) (put-string (current-output-port) s))
      ((s p) (put-string p s))))
  (define read-line
    (case-lambda
      (() (get-line (current-input-port)))
      ((p) (get-line p))))
  (define (string->vector s) (list->vector (string->list s)))
  (define (vector->string v) (list->string (vector->list v)))

  ;; R7RS record syntax, as R6RS record syntax. The constructor must name
  ;; every field, in the order the fields are declared, which is the case
  ;; for every record in the benchmarks used here.
  (define-syntax define-record-type
    (syntax-rules ()
      ((_ type (ctor cfield ...) pred fieldspec ...)
       (r7rs-record-fields type ctor pred (fieldspec ...) ()))))

  (define-syntax r7rs-record-fields
    (syntax-rules ()
      ((_ type ctor pred () (spec ...))
       (r6rs:define-record-type (type ctor pred) (fields spec ...)))
      ((_ type ctor pred ((f a) rest ...) (spec ...))
       (r7rs-record-fields type ctor pred (rest ...) (spec ... (immutable f a))))
      ((_ type ctor pred ((f a m) rest ...) (spec ...))
       (r7rs-record-fields type ctor pred (rest ...) (spec ... (mutable f a m)))))))
