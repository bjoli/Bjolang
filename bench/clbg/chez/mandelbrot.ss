;; mandelbrot.
(import (chezscheme))

(define (inside? cr ci)
  (let go ((i 0) (zr 0.0) (zi 0.0) (tr 0.0) (ti 0.0))
    (if (and (fx< i 50) (fl<= (fl+ tr ti) 4.0))
        (let* ((zi (fl+ (fl* 2.0 (fl* zr zi)) ci))
               (zr (fl+ (fl- tr ti) cr)))
          (go (fx+ i 1) zr zi (fl* zr zr) (fl* zi zi)))
        (fl<= (fl+ tr ti) 4.0))))

(let* ((n (string->number (cadr (command-line))))
       (row-bytes (fxquotient (fx+ n 7) 8))
       (bitmap (make-bytevector (fx* row-bytes n) 0))
       (size (fixnum->flonum n)))
  (do ((y 0 (fx+ y 1))) ((fx= y n))
    (let ((ci (fl- (fl/ (fl* 2.0 (fixnum->flonum y)) size) 1.0)))
      (let go ((x 0) (bits 0) (at (fx* y row-bytes)))
        (cond
          ((fx= x n)
           (unless (fx= 0 (fxremainder n 8))
             (bytevector-u8-set! bitmap at (fxand 255 (fxsll bits (fx- 8 (fxremainder n 8)))))))
          (else
           (let* ((cr (fl- (fl/ (fl* 2.0 (fixnum->flonum x)) size) 1.5))
                  (bits (fx+ (fxsll bits 1) (if (inside? cr ci) 1 0))))
             (if (fx= 7 (fxremainder x 8))
                 (begin
                   (bytevector-u8-set! bitmap at bits)
                   (go (fx+ x 1) 0 (fx+ at 1)))
                 (go (fx+ x 1) bits at))))))))
  (let ((out (standard-output-port)))
    (put-bytevector out (string->utf8 (format "P4\n~a ~a\n" n n)))
    (put-bytevector out bitmap)
    (flush-output-port out)))
