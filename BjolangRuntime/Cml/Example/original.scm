
(use-modules (fibers) (fibers channels) (ice-9 match))(define (server in out)

(let lp ()

(match (pk 'server-received (get-message in))

('ping! (put-message out 'pong!))

('sup (put-message out 'not-much-u))

(msg (put-message out (cons 'wat msg))))

(lp)))(define (client in out)

(for-each (lambda (msg)

(put-message out msg)

(pk 'client-received (get-message in)))

'(ping! sup)))(run-fibers

(lambda ()

(let ((c2s (make-channel))

(s2c (make-channel)))

(spawn-fiber (lambda () (server c2s s2c)))

(client s2c c2s)))) 