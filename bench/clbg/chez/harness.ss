;; What every program's body does: run the benchmark once, or with --warm as
;; the last argument, twice. Included by each program; run.py compiles them
;; from this directory.
;;
;; A warm run first runs the benchmark with its output thrown away, then runs
;; it again and times only that second run. The time goes to standard error
;; as "warm-seconds <s>". A benchmark that reads standard input is given the
;; same input both times: it is read once, before either run.

;; The binary output port of the run, for the programs that write bytes.
(define bench-binary-out (make-parameter #f))

(define (binary-out)
  (or (bench-binary-out)
      (let ((p (standard-output-port (buffer-mode block))))
        (bench-binary-out p)
        p)))

(define (flush-outputs)
  (flush-output-port (current-output-port))
  (when (bench-binary-out) (flush-output-port (bench-binary-out))))

(define (bench-main reads-input? run)
  (let* ((args (cdr (command-line)))
         (warm? (and (pair? args) (string=? (list-ref args (- (length args) 1)) "--warm")))
         (args (if warm? (list-head args (- (length args) 1)) args)))
    (if (not warm?)
        (begin (run args) (flush-outputs))
        (let* ((read (if reads-input? (get-string-all (current-input-port)) ""))
               (input (if (eof-object? read) "" read))
               (null-bin (open-file-output-port "/dev/null" (file-options no-fail)
                                                (buffer-mode block)))
               (null-text (open-file-output-port "/dev/null" (file-options no-fail)
                                                 (buffer-mode block) (native-transcoder))))
          (parameterize ((current-input-port (open-string-input-port input))
                         (current-output-port null-text)
                         (bench-binary-out null-bin))
            (run args)
            (flush-outputs))
          (let ((t0 (current-time 'time-monotonic)))
            (parameterize ((current-input-port (open-string-input-port input)))
              (run args)
              (flush-outputs))
            (let ((d (time-difference (current-time 'time-monotonic) t0)))
              (format (current-error-port) "warm-seconds ~a\n"
                      (+ (time-second d) (/ (time-nanosecond d) 1e9)))))))))
