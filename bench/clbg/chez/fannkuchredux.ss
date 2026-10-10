;; fannkuch-redux.
(import (chezscheme))

(define (count-flips perm1 perm)
  (define n (fxvector-length perm1))
  (do ((i 0 (fx+ i 1))) ((fx= i n))
    (fxvector-set! perm i (fxvector-ref perm1 i)))
  (let flip ((flips 0))
    (let ((k (fxvector-ref perm 0)))
      (if (fx= k 0)
          flips
          (begin
            (let rev ((i 0) (j k))
              (when (fx< i j)
                (let ((t (fxvector-ref perm i)))
                  (fxvector-set! perm i (fxvector-ref perm j))
                  (fxvector-set! perm j t)
                  (rev (fx+ i 1) (fx- j 1)))))
            (flip (fx+ flips 1)))))))

(define (rotate! perm1 r)
  (let ((perm0 (fxvector-ref perm1 0)))
    (do ((i 0 (fx+ i 1))) ((fx= i r))
      (fxvector-set! perm1 i (fxvector-ref perm1 (fx+ i 1))))
    (fxvector-set! perm1 r perm0)))

(define (next-perm! perm1 count r)
  (define n (fxvector-length perm1))
  (let go ((r r))
    (if (fx= r n)
        n
        (begin
          (rotate! perm1 r)
          (fxvector-set! count r (fx- (fxvector-ref count r) 1))
          (if (fx> (fxvector-ref count r) 0)
              r
              (go (fx+ r 1)))))))

(define (fannkuch n)
  (let ((perm1 (make-fxvector n 0))
        (perm (make-fxvector n 0))
        (count (make-fxvector n 0)))
    (do ((i 0 (fx+ i 1))) ((fx= i n)) (fxvector-set! perm1 i i))
    (let go ((r n) (max-flips 0) (checksum 0) (perm-count 0))
      (let fill ((r r))
        (when (fx> r 1)
          (fxvector-set! count (fx- r 1) r)
          (fill (fx- r 1))))
      (let* ((flips (count-flips perm1 perm))
             (max-flips (fxmax max-flips flips))
             (checksum (if (fxeven? perm-count) (fx+ checksum flips) (fx- checksum flips)))
             (r (next-perm! perm1 count 1)))
        (if (fx= r n)
            (values checksum max-flips)
            (go r max-flips checksum (fx+ perm-count 1)))))))

(let ((n (string->number (cadr (command-line)))))
  (let-values (((checksum max-flips) (fannkuch n)))
    (printf "~a\nPfannkuchen(~a) = ~a\n" checksum n max-flips)))
