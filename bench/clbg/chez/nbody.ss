;; n-body. A body is an flvector: x y z vx vy vz mass.
(import (chezscheme))

(define pi 3.141592653589793)
(define solar-mass (fl* 4.0 (fl* pi pi)))
(define days-per-year 365.24)

(define (planet x y z vx vy vz mass)
  (flvector x y z
            (fl* vx days-per-year) (fl* vy days-per-year) (fl* vz days-per-year)
            (fl* mass solar-mass)))

(define-syntax x  (identifier-syntax 0))
(define-syntax y  (identifier-syntax 1))
(define-syntax z  (identifier-syntax 2))
(define-syntax vx (identifier-syntax 3))
(define-syntax vy (identifier-syntax 4))
(define-syntax vz (identifier-syntax 5))
(define-syntax mass (identifier-syntax 6))

(define (make-system)
  (vector
   (planet 0.0 0.0 0.0 0.0 0.0 0.0 1.0)
   (planet 4.84143144246472090e+00 -1.16032004402742839e+00 -1.03622044471123109e-01
           1.66007664274403694e-03 7.69901118419740425e-03 -6.90460016972063023e-05
           9.54791938424326609e-04)
   (planet 8.34336671824457987e+00 4.12479856412430479e+00 -4.03523417114321381e-01
           -2.76742510726862411e-03 4.99852801234917238e-03 2.30417297573763929e-05
           2.85885980666130812e-04)
   (planet 1.28943695621391310e+01 -1.51111514016986312e+01 -2.23307578892655734e-01
           2.96460137564761618e-03 2.37847173959480950e-03 -2.96589568540237556e-05
           4.36624404335156298e-05)
   (planet 1.53796971148509165e+01 -2.59193146099879641e+01 1.79258772950371181e-01
           2.68067772490389322e-03 1.62824170038242295e-03 -9.51592254519715870e-05
           5.15138902046611451e-05)))

(define-syntax ref (syntax-rules () ((_ b f) (flvector-ref b f))))
(define-syntax set (syntax-rules () ((_ b f v) (flvector-set! b f v))))

(define (offset-momentum bodies)
  (let loop ((i 0) (px 0.0) (py 0.0) (pz 0.0))
    (if (fx< i (vector-length bodies))
        (let ((b (vector-ref bodies i)))
          (loop (fx+ i 1)
                (fl+ px (fl* (ref b vx) (ref b mass)))
                (fl+ py (fl* (ref b vy) (ref b mass)))
                (fl+ pz (fl* (ref b vz) (ref b mass)))))
        (let ((sun (vector-ref bodies 0)))
          (set sun vx (fl/ (fl- px) solar-mass))
          (set sun vy (fl/ (fl- py) solar-mass))
          (set sun vz (fl/ (fl- pz) solar-mass))))))

(define (energy bodies)
  (define n (vector-length bodies))
  (let outer ((i 0) (e 0.0))
    (if (fx< i n)
        (let* ((b (vector-ref bodies i))
               (e (fl+ e (fl* 0.5 (fl* (ref b mass)
                                       (fl+ (fl* (ref b vx) (ref b vx))
                                            (fl+ (fl* (ref b vy) (ref b vy))
                                                 (fl* (ref b vz) (ref b vz)))))))))
          (let inner ((j (fx+ i 1)) (e e))
            (if (fx< j n)
                (let* ((b2 (vector-ref bodies j))
                       (dx (fl- (ref b x) (ref b2 x)))
                       (dy (fl- (ref b y) (ref b2 y)))
                       (dz (fl- (ref b z) (ref b2 z))))
                  (inner (fx+ j 1)
                         (fl- e (fl/ (fl* (ref b mass) (ref b2 mass))
                                     (flsqrt (fl+ (fl* dx dx) (fl+ (fl* dy dy) (fl* dz dz))))))))
                (outer (fx+ i 1) e))))
        e)))

(define (advance bodies dt)
  (define n (vector-length bodies))
  (do ((i 0 (fx+ i 1))) ((fx= i n))
    (let ((b (vector-ref bodies i)))
      (do ((j (fx+ i 1) (fx+ j 1))) ((fx= j n))
        (let* ((b2 (vector-ref bodies j))
               (dx (fl- (ref b x) (ref b2 x)))
               (dy (fl- (ref b y) (ref b2 y)))
               (dz (fl- (ref b z) (ref b2 z)))
               (d2 (fl+ (fl* dx dx) (fl+ (fl* dy dy) (fl* dz dz))))
               (mag (fl/ dt (fl* d2 (flsqrt d2))))
               (bm (fl* (ref b mass) mag))
               (b2m (fl* (ref b2 mass) mag)))
          (set b vx (fl- (ref b vx) (fl* dx b2m)))
          (set b vy (fl- (ref b vy) (fl* dy b2m)))
          (set b vz (fl- (ref b vz) (fl* dz b2m)))
          (set b2 vx (fl+ (ref b2 vx) (fl* dx bm)))
          (set b2 vy (fl+ (ref b2 vy) (fl* dy bm)))
          (set b2 vz (fl+ (ref b2 vz) (fl* dz bm)))))))
  (do ((i 0 (fx+ i 1))) ((fx= i n))
    (let ((b (vector-ref bodies i)))
      (set b x (fl+ (ref b x) (fl* dt (ref b vx))))
      (set b y (fl+ (ref b y) (fl* dt (ref b vy))))
      (set b z (fl+ (ref b z) (fl* dt (ref b vz)))))))

(let ((n (string->number (cadr (command-line))))
      (bodies (make-system)))
  (offset-momentum bodies)
  (printf "~,9f\n" (energy bodies))
  (do ((i 0 (fx+ i 1))) ((fx= i n)) (advance bodies 0.01))
  (printf "~,9f\n" (energy bodies)))
