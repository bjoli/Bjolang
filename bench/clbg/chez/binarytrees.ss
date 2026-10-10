;; binary-trees. A node is a pair of its children; the empty tree is '().
(import (chezscheme))
(include "harness.ss")

(define (bottom-up depth)
  (if (fx> depth 0)
      (cons (bottom-up (fx- depth 1)) (bottom-up (fx- depth 1)))
      (cons '() '())))

(define (check t)
  (if (null? t)
      0
      (fx+ 1 (fx+ (check (car t)) (check (cdr t))))))

(define (run args)
  (let* ((n (string->number (car args)))
         (min-depth 4)
         (max-depth (max (+ min-depth 2) n))
         (stretch-depth (+ max-depth 1)))
    (printf "stretch tree of depth ~a\t check: ~a\n" stretch-depth (check (bottom-up stretch-depth)))
    (let ((long-lived (bottom-up max-depth)))
      (do ((depth min-depth (fx+ depth 2))) ((fx> depth max-depth))
        (let ((iterations (fxsll 1 (fx+ (fx- max-depth depth) min-depth))))
          (let loop ((i 0) (sum 0))
            (if (fx< i iterations)
                (loop (fx+ i 1) (fx+ sum (check (bottom-up depth))))
                (printf "~a\t trees of depth ~a\t check: ~a\n" iterations depth sum)))))
      (printf "long lived tree of depth ~a\t check: ~a\n" max-depth (check long-lived)))))

(bench-main #f run)
