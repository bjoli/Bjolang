;; first-15.clj — fetch the urls given, 8 at a time, stop after 15 have landed.
;;
;; The Clojure counterpart of first-15.bjo, in the same shape: one feeder
;; thread, a fixed crew of 8 workers, one collector (the calling thread).
;;
;; What plays the part of `with-cancel` is thread interruption. Every blocking
;; call the crew makes — `SynchronousQueue.take`, `.put` and `HttpClient.send`
;; — throws `InterruptedException`, so interrupting the crew is one call that
;; every fiber in flight hears. The `finally` in `fetch-first` is the scope: it
;; runs whether the collector returned or threw.
;;
;; Run it with:
;;
;;   clojure Examples/fetch15/first-15.clj https://example.com https://...

(import '[java.net URI]
        '[java.net.http HttpClient HttpRequest HttpResponse HttpResponse$BodyHandlers]
        '[java.time Duration]
        '[java.util.concurrent SynchronousQueue])

(def ^HttpClient client
  (-> (HttpClient/newBuilder)
      (.connectTimeout (Duration/ofSeconds 10))
      (.build)))

;; ---------------------------------------------------------------------------
;; One request
;; ---------------------------------------------------------------------------

;; Fetches the text at url. [:ok body] or [:err throwable].
;;
;; `InterruptedException` is rethrown rather than reported as an error: it is
;; the cancellation arriving, and it belongs to the worker's loop.
(defn- fetch-text [url]
  (try
    (let [request (-> (HttpRequest/newBuilder (URI/create url))
                      (.GET)
                      (.build))
          ^HttpResponse response (.send client request (HttpResponse$BodyHandlers/ofString))]
      [:ok (.body response)])
    (catch InterruptedException e (throw e))
    (catch Exception e [:err e])))

;; ---------------------------------------------------------------------------
;; The crew
;; ---------------------------------------------------------------------------

;; A virtual thread running f. Virtual because the crew is sized by how many
;; requests should be in flight rather than by how many threads are affordable,
;; and because every blocking call below releases its carrier thread.
(defn- spawn ^Thread [f]
  (Thread/startVirtualThread f))

;; Take a url, fetch it, hand the answer on, repeat. If fetch-text is an error,
;; go on to the next address.
(defn- worker [^SynchronousQueue jobs ^SynchronousQueue answers]
  (fn []
    (try
      (loop []
        (let [url (.take jobs)]
          (.put answers {:url url :outcome (fetch-text url)})
          (recur)))
      (catch InterruptedException _ nil))))

(defn- hire [jobs answers n]
  (doall (repeatedly n #(spawn (worker jobs answers)))))

;; Push every url at the crew. The queue is a rendezvous, so this parks on send
;; number 9 until a worker frees up.
(defn- feeder [^SynchronousQueue jobs urls]
  (fn []
    (try
      (doseq [url urls]
        (.put jobs url))
      (catch InterruptedException _ nil))))

;; Cancel the crew, then wait for it to actually finish. Without the join
;; `fetch-first` returns while eight sockets are still tearing down.
(defn- reap [threads]
  (doseq [^Thread t threads] (.interrupt t))
  (doseq [^Thread t threads] (.join t)))

;; ---------------------------------------------------------------------------
;; The whole thing
;; ---------------------------------------------------------------------------

;; `seen` counts everything that came back, `kept` only the successes. `seen` is
;; what makes this return if every request failed.
(defn- collect [^SynchronousQueue answers wanted total]
  (loop [seen 0
         kept []]
    (if (or (= (count kept) wanted) (= seen total))
      kept
      (let [answer (.take answers)]
        (if (= :ok (first (:outcome answer)))
          (recur (inc seen) (conj kept answer))
          (recur (inc seen) kept))))))

(defn fetch-first [urls wanted crew-size]
  (let [jobs (SynchronousQueue.)
        answers (SynchronousQueue.)
        crew (hire jobs answers crew-size)
        feed (spawn (feeder jobs urls))]
    (try
      (collect answers wanted (count urls))
      (finally
        (reap (cons feed crew))))))

(defn -main [& urls]
  (doseq [answer (fetch-first (vec urls) 15 8)]
    (println (:url answer))))

;; Loaded as a script rather than through a main-opt, so that it runs on a bare
;; clojure.main with no classpath to set up.
(apply -main *command-line-args*)
