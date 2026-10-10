;; fasta.
(import (chezscheme))
(include "harness.ss")

(define line-length 60)

(define alu
  (string->utf8
   (string-append
    "GGCCGGGCGCGGTGGCTCACGCCTGTAATCCCAGCACTTTGGGAGGCCGAGGCGGGCGGATCACCTGAGGTCAGGAGTTCGAGA"
    "CCAGCCTGGCCAACATGGTGAAACCCCGTCTCTACTAAAAATACAAAAATTAGCCGGGCGTGGTGGCGCGCGCCTGTAATCCCA"
    "GCTACTCGGGAGGCTGAGGCAGGAGAATCGCTTGAACCCGGGAGGCGGAGGTTGCAGTGAGCCGAGATCGCGCCACTGCACTCC"
    "AGCCTGGGCGACAGAGCGAGACTCCGTCTCAAAAA")))

(define im 139968)
(define ia 3877)
(define ic 29573)
(define seed 42)

(define (next-random max)
  (set! seed (fxremainder (fx+ (fx* seed ia) ic) im))
  (fl/ (fl* max (fixnum->flonum seed)) (fixnum->flonum im)))

(define (write-line out line count)
  (bytevector-u8-set! line count 10)
  (put-bytevector out line 0 (fx+ count 1)))

(define (repeat-fasta out source n)
  (define line (make-bytevector (fx+ line-length 1)))
  (define len (bytevector-length source))
  (let go ((left n) (k 0))
    (when (fx> left 0)
      (let ((count (fxmin left line-length)))
        (let fill ((i 0) (k k))
          (when (fx< i count)
            (bytevector-u8-set! line i (bytevector-u8-ref source k))
            (fill (fx+ i 1) (if (fx= (fx+ k 1) len) 0 (fx+ k 1)))))
        (write-line out line count)
        (go (fx- left count) (fxremainder (fx+ k count) len))))))

(define (random-fasta out codes probs n)
  (define line (make-bytevector (fx+ line-length 1)))
  (define last (fx- (flvector-length probs) 1))
  (let go ((left n))
    (when (fx> left 0)
      (let ((count (fxmin left line-length)))
        (do ((i 0 (fx+ i 1))) ((fx= i count))
          (let ((r (next-random 1.0)))
            (bytevector-u8-set! line i
                                (bytevector-u8-ref codes
                                                   (let pick ((j 0))
                                                     (if (and (fx< j last) (fl>= r (flvector-ref probs j)))
                                                         (pick (fx+ j 1))
                                                         j))))))
        (write-line out line count)
        (go (fx- left count))))))

(define (cumulative ps)
  (define acc (make-flvector (flvector-length ps) 0.0))
  (let loop ((i 0) (sum 0.0))
    (when (fx< i (flvector-length ps))
      (let ((sum (fl+ sum (flvector-ref ps i))))
        (flvector-set! acc i sum)
        (loop (fx+ i 1) sum))))
  acc)

(define (run args)
  ;; A warm run runs this twice, from the same seed.
  (set! seed 42)
  (let ((n (string->number (car args)))
        (out (binary-out)))
    (put-bytevector out (string->utf8 ">ONE Homo sapiens alu\n"))
    (repeat-fasta out alu (fx* 2 n))
    (put-bytevector out (string->utf8 ">TWO IUB ambiguity codes\n"))
    (random-fasta out (string->utf8 "acgtBDHKMNRSVWY")
                  (cumulative (flvector 0.27 0.12 0.12 0.27 0.02 0.02 0.02 0.02 0.02 0.02 0.02 0.02 0.02 0.02 0.02))
                  (fx* 3 n))
    (put-bytevector out (string->utf8 ">THREE Homo sapiens frequency\n"))
    (random-fasta out (string->utf8 "acgt")
                  (cumulative (flvector 0.3029549426680 0.1979883004921 0.1975473066391 0.3015094502008))
                  (fx* 5 n))
    (flush-output-port out)))

(bench-main #f run)
