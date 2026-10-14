/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 *
 * As a special exception to the Mozilla Public License, version 2.0, if you
 * compile your application source code and portions of this software are
 * embedded into the generated object code or executable form as a normal
 * consequence of the compilation process (such as inline functions,
 * templates, generics, or macros), you may redistribute such embedded portions
 * in such object code or executable form without complying with the source code
 * availability requirements or notice obligations of Section 3 of the MPL 2.0.
 */

// A sharded key-value service under load: the Go twin of
// bench/service/bjolang/service.bjo and bench/service/hopac/Program.fs.
//
// Where the other suites each time one shape, this mixes the ones a service
// has, at the proportions one might:
//
//   - 16 shard servers, each owning 1024 slots of a 16384-key space (key k
//     lives in shard k % 16, slot k / 16). A shard loops on a choice between
//     its request channel and its quit channel. Serving a request costs
//     crunch(20) of work; a get answers the slot, a put replaces it and
//     answers the old value. The answer goes back on the request's own reply
//     channel.
//   - 1000 client connections of 1000 requests each, all at once. A client
//     draws its keys from its own generator, s = (s*1103515245 + 12345) mod
//     2^31 starting at s = client+1, key = s mod 16384, and "decodes" each
//     request with crunch(100) before sending it.
//   - Request i of a client is a multi-get when i mod 10 = 9: four keys, k to
//     k+3, fetched by four spawned tasks that each make an ordinary request
//     and send what they got on a results channel the client reads four
//     times. Otherwise a put when i mod 5 = 0, a get otherwise. 20% of the
//     single requests are puts.
//   - Every request, single or within a multi-get, makes a fresh unbuffered
//     reply channel, and waits for the answer under a one-second deadline:
//     a choice between the reply and a timer. The deadline never fires; it is
//     there because a service would have one.
//   - Every eighth request of a client has its latency taken, decode to
//     answer.
//
// Every channel is unbuffered. Slots start at 7k and puts write 7k, so every
// answer is known and the checksum (answers, plus each decode's result mod 3,
// plus the shards' work) is the same in all three languages and every rep.
//
// A rep starts the shards, then times the clients from the first spawn to the
// last join, in wall time and in process CPU time; the latencies go to
// p50/p99/p99.9. One rep is a warm-up and is not printed.
//
// That is a closed loop: a client sends its next request as soon as it has
// the answer to the last, so the machine is always saturated and a slower
// runtime has every request wait longer. With a rate R (requests a second,
// over all clients) the loop is open instead. The schedule starts 100 ms
// after the clients are spawned, at t0, and request i of client c is due at
// t0 + (i*clients + c)/R. The wall time is then taken from t0. A client that
// is early sleeps until then, in whole milliseconds rounded up, as the three
// runtimes' timers all can; one that is late sends at once. With 10000
// clients a client sends every 10000/R seconds, which at a million a second
// is 10 ms, well above the timers' resolution.
//
// Two latencies are taken of each sampled request. The latency (p50, p99,
// p99.9) counts from the due time: what a caller of the service sees, with
// a late wake-up, a wait in a run queue and a GC pause while the client
// slept all in it. It also has the sleep's rounding to whole milliseconds in
// it, the same in all three, so below about 2 ms it says little. The service
// time (svc) counts from when the client ran, and leaves all of that out.
// It used to be the only one, and it made a runtime whose delays fall while
// clients sleep look better than it is. In a closed loop the two are the same.
//
// The requests due in the schedule's first 200 ms are not sampled. Every rep
// starts its clients afresh, and the first request of each found the heap
// and the scheduler cold: in Go, every client's first request took until
// about 50 ms into the schedule, which at 10000 clients is 8% of the samples.
// A service that runs for longer than a rep has no such moment.
//
//	go run . -reps 5 [-clients 1000] [-requests 1000] [-rate R]

package main

import (
	"flag"
	"fmt"
	"runtime"
	"slices"
	"sync"
	"syscall"
	"time"
)

const (
	shards        = 16
	slotsPerShard = 1024
	keys          = shards * slotsPerShard
	decodeWork    = 100
	serveWork     = 20
	deadline      = time.Second
	opGet         = 0
	opPut         = 1
	lead          = 100 * time.Millisecond
	warm          = 200 * time.Millisecond
)

type Req struct {
	op, key, value int
	reply          chan int
}

func crunch(n, seed int) int {
	x := seed
	for i := 0; i < n; i++ {
		x = (x*31 + i) % 1000003
	}
	return x
}

func shard(id int, reqs <-chan Req, quit <-chan int, work *int64, done *sync.WaitGroup) {
	defer done.Done()
	slots := make([]int, slotsPerShard)
	for j := range slots {
		slots[j] = (j*shards + id) * 7
	}
	var w int64
	for {
		select {
		case r := <-reqs:
			w += int64(crunch(serveWork, r.key))
			slot := r.key / shards
			old := slots[slot]
			if r.op == opPut {
				slots[slot] = r.value
			}
			r.reply <- old
		case <-quit:
			*work = w
			return
		}
	}
}

// One request and its answer, or false at the deadline.
func request(chans []chan Req, op, key, value int) (int, bool) {
	reply := make(chan int)
	chans[key%shards] <- Req{op, key, value, reply}
	t := time.NewTimer(deadline)
	select {
	case v := <-reply:
		t.Stop()
		return v, true
	case <-t.C:
		return 0, false
	}
}

type clientOut struct {
	checksum int64
	samples  []int64 // from the due time
	svc      []int64 // from when the client ran
	timeouts int
}

// When request i of client c is due, measured from the rep's start.
func due(c, i, clients, rate int) time.Duration {
	return time.Duration(int64(i*clients+c) * 1_000_000_000 / int64(rate))
}

func client(c, clients, requests, rate int, start time.Time, chans []chan Req) clientOut {
	s := int64(c + 1)
	var checksum int64
	samples := make([]int64, 0, requests/8+1)
	svc := make([]int64, 0, requests/8+1)
	timeouts := 0
	results := make(chan int)

	for i := 0; i < requests; i++ {
		sampled := i%8 == 0
		// The due time, and when the client ran.
		var at, t0 time.Time
		if rate > 0 {
			offset := due(c, i, clients, rate)
			sampled = sampled && offset >= warm
			at = start.Add(offset)
			if wait := time.Until(at); wait > 0 {
				time.Sleep((wait + time.Millisecond - 1).Truncate(time.Millisecond))
				t0 = time.Now()
			} else {
				t0 = at
			}
		} else if sampled {
			t0 = time.Now()
			at = t0
		}

		s = (s*1103515245 + 12345) % 2147483648
		key := int(s % keys)
		d := crunch(decodeWork, key)

		var answer int
		if i%10 == 9 {
			for j := 0; j < 4; j++ {
				k := (key + j) % keys
				go func() {
					v, ok := request(chans, opGet, k, 0)
					if !ok {
						v = -1
					}
					results <- v
				}()
			}
			for j := 0; j < 4; j++ {
				if v := <-results; v < 0 {
					timeouts++
				} else {
					answer += v
				}
			}
		} else {
			op := opGet
			if i%5 == 0 {
				op = opPut
			}
			v, ok := request(chans, op, key, key*7)
			if ok {
				answer = v
			} else {
				timeouts++
			}
		}

		checksum += int64(answer) + int64(d%3)
		if sampled {
			done := time.Now()
			samples = append(samples, int64(done.Sub(at)))
			svc = append(svc, int64(done.Sub(t0)))
		}
	}
	return clientOut{checksum, samples, svc, timeouts}
}

func cpuMs() int64 {
	var ru syscall.Rusage
	syscall.Getrusage(syscall.RUSAGE_SELF, &ru)
	return (ru.Utime.Nano() + ru.Stime.Nano()) / 1_000_000
}

func rep(n, clients, requests, rate int) {
	chans := make([]chan Req, shards)
	quits := make([]chan int, shards)
	works := make([]int64, shards)
	var shardsDone sync.WaitGroup
	shardsDone.Add(shards)
	for i := 0; i < shards; i++ {
		chans[i] = make(chan Req)
		quits[i] = make(chan int)
		go shard(i, chans[i], quits[i], &works[i], &shardsDone)
	}
	runtime.GC()

	var mem runtime.MemStats
	runtime.ReadMemStats(&mem)
	a0 := mem.TotalAlloc
	gc0, pause0 := mem.NumGC, mem.PauseTotalNs
	// In an open loop the schedule starts a moment after the clients are
	// spawned, so that spawning them is not a backlog, and the wall time is
	// taken from there.
	t0 := time.Now()
	if rate > 0 {
		t0 = t0.Add(lead)
	}
	c0 := cpuMs()
	outs := make([]clientOut, clients)
	var wg sync.WaitGroup
	wg.Add(clients)
	for c := 0; c < clients; c++ {
		go func() {
			outs[c] = client(c, clients, requests, rate, t0, chans)
			wg.Done()
		}()
	}
	wg.Wait()
	wall := time.Since(t0).Milliseconds()
	cpu := cpuMs() - c0
	runtime.ReadMemStats(&mem)
	alloc := mem.TotalAlloc - a0
	gcs, pause := mem.NumGC-gc0, (mem.PauseTotalNs-pause0)/1_000_000

	for i := 0; i < shards; i++ {
		quits[i] <- 0
	}
	shardsDone.Wait()

	var checksum int64
	timeouts := 0
	var samples, svc []int64
	for _, o := range outs {
		checksum += o.checksum
		timeouts += o.timeouts
		samples = append(samples, o.samples...)
		svc = append(svc, o.svc...)
	}
	for _, w := range works {
		checksum += w
	}
	slices.Sort(samples)
	slices.Sort(svc)
	at := func(s []int64, per, of int) int64 { return s[len(s)*per/of] }

	if n > 0 {
		fmt.Printf("rep %d wall_ms %d cpu_ms %d p50_ns %d p99_ns %d p999_ns %d timeouts %d checksum %d alloc_b %d gcs %d pause_ms %d svc_p50_ns %d svc_p99_ns %d svc_p999_ns %d\n",
			n, wall, cpu, at(samples, 50, 100), at(samples, 99, 100), at(samples, 999, 1000),
			timeouts, checksum, alloc, gcs, pause,
			at(svc, 50, 100), at(svc, 99, 100), at(svc, 999, 1000))
	}
}

func main() {
	reps := flag.Int("reps", 5, "measured reps, after one warm-up")
	clients := flag.Int("clients", 1000, "client connections")
	requests := flag.Int("requests", 1000, "requests per client")
	rate := flag.Int("rate", 0, "requests a second over all clients; 0 for a closed loop")
	flag.Parse()

	fmt.Printf("go %s GOMAXPROCS=%d clients=%d requests=%d rate=%d\n",
		runtime.Version(), runtime.GOMAXPROCS(0), *clients, *requests, *rate)
	for n := 0; n <= *reps; n++ {
		rep(n, *clients, *requests, *rate)
	}
}
