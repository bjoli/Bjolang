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

// Reference implementation of the BjoML spawn/channel benchmarks in Go.
//
// The point of comparison is goroutines vs fibers, so every benchmark here is
// shaped identically to its C# counterpart in ../Bench: same counts, same
// channel topology, same amount of work per unit.
//
// All channels are unbuffered, which is the rendezvous semantics BjoML gives.

package main

import (
	"flag"
	"fmt"
	"runtime"
	"sync"
	"sync/atomic"
	"time"
)

// repeat runs a benchmark back-to-back, mirroring --reps in ../Bench and ../hopac.
//
// Go has no tiered JIT, so it does not need the warm-up the .NET suites do — it is
// at full speed on the first iteration. The flag exists so all three suites are
// driven identically and the comparison stays like-for-like; for Go the extra reps
// simply measure run-to-run variance, which is itself worth seeing.
func repeat(reps int, body func()) {
	for i := 0; i < reps; i++ {
		body()
	}
}

func main() {
	reps := flag.Int("reps", 1, "repetitions per benchmark")
	suite := flag.String("suite", "main", "main or varied")
	flag.Parse()

	fmt.Printf("Go %s, GOMAXPROCS=%d, reps=%d\n\n", runtime.Version(), runtime.GOMAXPROCS(0), *reps)

	if *suite == "varied" {
		runVaried(*reps)
		return
	}

	repeat(*reps, spawnStorm)
	repeat(*reps, spawnAndSend)
	repeat(*reps, pingPong)
	repeat(*reps, ring)
	repeat(*reps, selectChoose)
	repeat(*reps, fanOut)
}

// 1. Spawn storm: N goroutines that do nothing but bump a counter and exit.
//    Measures raw spawn + join cost with no communication at all.
func spawnStorm() {
	const n = 1_000_000

	var wg sync.WaitGroup
	var counter int64

	start := time.Now()

	wg.Add(n)
	for i := 0; i < n; i++ {
		go func() {
			atomic.AddInt64(&counter, 1)
			wg.Done()
		}()
	}
	wg.Wait()

	elapsed := time.Since(start)
	fmt.Printf("Spawn storm:      %d goroutines in %v (%.0f ns/spawn), counter=%d\n",
		n, elapsed.Round(time.Millisecond), float64(elapsed.Nanoseconds())/float64(n), counter)
}

// 2. Spawn + one channel send each: N goroutines each send one value to a shared
//    unbuffered channel; the main goroutine receives N of them.
//    Measures spawn cost plus one rendezvous per spawn.
func spawnAndSend() {
	const n = 200_000

	ch := make(chan int)

	start := time.Now()

	for i := 0; i < n; i++ {
		go func(v int) { ch <- v }(i)
	}

	total := 0
	for i := 0; i < n; i++ {
		total += <-ch
	}

	elapsed := time.Since(start)
	fmt.Printf("Spawn+send:       %d goroutines in %v (%.0f ns/op), total=%d\n",
		n, elapsed.Round(time.Millisecond), float64(elapsed.Nanoseconds())/float64(n), total)
}

// 3. Ping-pong: two goroutines bouncing a value over two unbuffered channels.
//    Measures pure handoff latency with no spawning in the loop.
func pingPong() {
	const rounds = 1_000_000

	a := make(chan int)
	b := make(chan int)
	done := make(chan struct{})

	go func() {
		for i := 0; i < rounds; i++ {
			v := <-a
			b <- v * 2
		}
		close(done)
	}()

	start := time.Now()

	go func() {
		for i := 0; i < rounds; i++ {
			a <- i
			<-b
		}
	}()

	<-done
	elapsed := time.Since(start)

	fmt.Printf("Ping-pong:        %d round trips in %v (%.0f ns/round-trip)\n",
		rounds, elapsed.Round(time.Millisecond), float64(elapsed.Nanoseconds())/float64(rounds))
}

// 4. Ring: 1000 goroutines in a circle, a token going round 1000 times.
//    Same shape as the BjoML ring benchmark.
func ring() {
	const numWorkers = 1000
	const numTrips = 1000

	channels := make([]chan int, numWorkers)
	for i := range channels {
		channels[i] = make(chan int)
	}

	var wg sync.WaitGroup
	wg.Add(numWorkers)

	for i := 0; i < numWorkers; i++ {
		workerID := i
		in := channels[workerID]
		out := channels[(workerID+1)%numWorkers]
		isLast := workerID == numWorkers-1

		go func() {
			defer wg.Done()
			for {
				msg := <-in
				if msg == -1 {
					if !isLast {
						out <- -1
					}
					return
				}
				if isLast {
					msg++
					if msg >= numTrips {
						out <- -1
						continue
					}
				}
				out <- msg
			}
		}()
	}

	start := time.Now()
	channels[0] <- 0
	wg.Wait()
	elapsed := time.Since(start)

	fmt.Printf("Ring:             %d messages in %v (%.0f ns/message)\n",
		numWorkers*numTrips, elapsed.Round(time.Millisecond),
		float64(elapsed.Nanoseconds())/float64(numWorkers*numTrips))
}

// 5. Select / Choose: 1,000,000 operations choosing between two channels.
func selectChoose() {
	const rounds = 1_000_000
	a := make(chan int)
	b := make(chan int)
	done := make(chan struct{})

	go func() {
		for i := 0; i < rounds; i++ {
			if i%2 == 0 {
				a <- i
			} else {
				b <- i
			}
		}
		close(done)
	}()

	start := time.Now()
	for i := 0; i < rounds; i++ {
		select {
		case <-a:
		case <-b:
		}
	}
	<-done
	elapsed := time.Since(start)

	fmt.Printf("Select/Choose:    %d ops in %v (%.0f ns/op)\n",
		rounds, elapsed.Round(time.Millisecond),
		float64(elapsed.Nanoseconds())/float64(rounds))
}

// 6. Fan-out: one goroutine spawns N children each doing a fixed slab of CPU work.
//    Measures whether the scheduler spreads children across cores.
func fanOut() {
	const numChildren = 480
	const iterations = 3_000_000

	// Serial reference, so the speedup is measured rather than assumed.
	serialStart := time.Now()
	burn(iterations)
	serialTotal := time.Since(serialStart) * numChildren

	var wg sync.WaitGroup
	wg.Add(numChildren)

	start := time.Now()
	go func() {
		for i := 0; i < numChildren; i++ {
			go func() {
				defer wg.Done()
				burn(iterations)
			}()
		}
	}()
	wg.Wait()
	elapsed := time.Since(start)

	fmt.Printf("Fan-out:          %d children in %v (serial reference %v, speedup %.1fx)\n",
		numChildren, elapsed.Round(time.Millisecond), serialTotal.Round(time.Millisecond),
		float64(serialTotal)/float64(elapsed))
}

var sink float64

func burn(iterations int) {
	acc := 0.0
	for i := 1; i <= iterations; i++ {
		acc += 1.0 / float64(i)
	}
	sink = acc
}
