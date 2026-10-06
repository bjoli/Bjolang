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

// Twin of bench/bjolang/cmlbench.bjo: same rows, same counts, same topology,
// same timed regions, and the same report (minimum of five reps, median beside
// it, bytes and collections per op).
//
// Run with: go run . -suite cmlbench
//
// Every `sync` in the Bjolang suite races the scope's cancellation token,
// because `main` runs in a scope. A Go program that can be stopped does the
// same thing by hand: each channel operation selects against ctx.Done(). So the
// rows that have a token in Bjolang are run twice here, plain and with
// ctx.Done(). The plain row is what Go costs without cancellation; the ctx row
// is the like-for-like one.
//
// Go has no scope. The nested-scope row's twin is what a scope is written as in
// Go: a context.WithCancel derived from the parent, a WaitGroup for the
// children, and cancel() when they are done.

package main

import (
	"context"
	"fmt"
	"runtime"
	"sort"
	"sync"
	"time"
)

type cmlRep struct {
	ns    time.Duration
	bytes uint64
	gcs   uint32
}

// cmlMeasured settles the heap, as the Bjolang twin's `settle` does, and then
// times body, counting what it allocated and how many collections ran.
func cmlMeasured(body func()) cmlRep {
	runtime.GC()
	var before, after runtime.MemStats
	runtime.ReadMemStats(&before)
	start := time.Now()
	body()
	elapsed := time.Since(start)
	runtime.ReadMemStats(&after)
	return cmlRep{elapsed, after.TotalAlloc - before.TotalAlloc, after.NumGC - before.NumGC}
}

func cmlReport(name string, ops int, reps []cmlRep) {
	ns := make([]float64, len(reps))
	bs := make([]float64, len(reps))
	gc := make([]int, len(reps))
	for i, r := range reps {
		ns[i] = float64(r.ns.Nanoseconds()) / float64(ops)
		bs[i] = float64(r.bytes) / float64(ops)
		gc[i] = int(r.gcs)
	}
	sort.Float64s(ns)
	sort.Float64s(bs)
	sort.Ints(gc)
	mid := len(reps) / 2
	fmt.Printf("%-24s%8.0f%10.0f   (median %.0f, gc %d/rep)\n", name, ns[0], bs[0], ns[mid], gc[mid])
}

func cmlReps(n int, body func() cmlRep) []cmlRep {
	out := make([]cmlRep, n)
	for i := range out {
		out[i] = body()
	}
	return out
}

// ---------------------------------------------------------------------------
// 1. Ring — 1000 nodes, 1000 trips, a million rendezvous.
// ---------------------------------------------------------------------------

// ringNode is the Bjolang ring-node: pass the message on, the last node counts
// a trip, and -1 travels round once to stop everybody.
func cmlRingNode(in, out chan int, isLast bool, trips int) {
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
			if msg >= trips {
				out <- -1
				continue
			}
		}
		out <- msg
	}
}

// cmlRingNodeCtx is the same node with every operation racing ctx.Done(), which is
// what each `sync` in the Bjolang node does with the scope's token.
func cmlRingNodeCtx(ctx context.Context, in, out chan int, isLast bool, trips int) {
	send := func(v int) bool {
		select {
		case out <- v:
			return true
		case <-ctx.Done():
			return false
		}
	}
	for {
		var msg int
		select {
		case msg = <-in:
		case <-ctx.Done():
			return
		}
		if msg == -1 {
			if !isLast {
				send(-1)
			}
			return
		}
		if isLast {
			msg++
			if msg >= trips {
				if !send(-1) {
					return
				}
				continue
			}
		}
		if !send(msg) {
			return
		}
	}
}

func cmlMakeRing(workers int) []chan int {
	chs := make([]chan int, workers)
	for i := range chs {
		chs[i] = make(chan int)
	}
	return chs
}

// The nodes start outside the timed region, the kick-off is sent from a
// goroutine of its own, and the timed region ends when every node has
// finished: the Bjolang row joins each handle, this waits on a WaitGroup.
func cmlRingJoined(workers, trips int, ctx context.Context) cmlRep {
	chs := cmlMakeRing(workers)
	var wg sync.WaitGroup
	wg.Add(workers)
	for i := 0; i < workers; i++ {
		in, out, last := chs[i], chs[(i+1)%workers], i == workers-1
		go func() {
			defer wg.Done()
			if ctx == nil {
				cmlRingNode(in, out, last, trips)
			} else {
				cmlRingNodeCtx(ctx, in, out, last, trips)
			}
		}()
	}
	return cmlMeasured(func() {
		kick := make(chan struct{})
		go func() {
			chs[0] <- 0
			close(kick)
		}()
		<-kick
		wg.Wait()
	})
}

// The nested-scope row: the nodes are started inside the timed region, under
// a context derived from the parent, waited for, and the context cancelled at
// the end, which is what `with-cancel` does around its `spawn`s.
func cmlRingNestedScope(parent context.Context, workers, trips int) cmlRep {
	chs := cmlMakeRing(workers)
	return cmlMeasured(func() {
		ctx, cancel := context.WithCancel(parent)
		var wg sync.WaitGroup
		wg.Add(workers + 1)
		for i := 0; i < workers; i++ {
			in, out, last := chs[i], chs[(i+1)%workers], i == workers-1
			go func() {
				defer wg.Done()
				cmlRingNodeCtx(ctx, in, out, last, trips)
			}()
		}
		go func() {
			defer wg.Done()
			select {
			case chs[0] <- 0:
			case <-ctx.Done():
			}
		}()
		wg.Wait()
		cancel()
	})
}

// ---------------------------------------------------------------------------
// 2. Spawn burst — a million goroutines that do nothing.
//
// The Bjolang row's completion signal is the scope, whose per-child cost is a
// count in and a count out; a WaitGroup is the same thing.
// ---------------------------------------------------------------------------

func cmlSpawnBurst(n int) cmlRep {
	return cmlMeasured(func() {
		var wg sync.WaitGroup
		wg.Add(n)
		for i := 0; i < n; i++ {
			go wg.Done()
		}
		wg.Wait()
	})
}

// ---------------------------------------------------------------------------
// 3. Skewed choose — 8 branches, only the first ever fires.
//
// The sender starts outside the timed region; the receiver is a goroutine of
// its own, started and waited for inside it, as in the Bjolang row.
// ---------------------------------------------------------------------------

func cmlSkewedChoose(rounds int, ctx context.Context) cmlRep {
	var chs [8]chan int
	for i := range chs {
		chs[i] = make(chan int)
	}
	senderDone := make(chan struct{})
	go func() {
		for i := 0; i < rounds; i++ {
			chs[0] <- i
		}
		close(senderDone)
	}()

	// A nil channel never fires, so the plain row's ninth case is inert.
	var done <-chan struct{}
	if ctx != nil {
		done = ctx.Done()
	}

	return cmlMeasured(func() {
		receiverDone := make(chan struct{})
		go func() {
			for i := 0; i < rounds; i++ {
				select {
				case <-chs[0]:
				case <-chs[1]:
				case <-chs[2]:
				case <-chs[3]:
				case <-chs[4]:
				case <-chs[5]:
				case <-chs[6]:
				case <-chs[7]:
				case <-done:
					close(receiverDone)
					return
				}
			}
			close(receiverDone)
		}()
		<-receiverDone
		<-senderDone
	})
}

// ---------------------------------------------------------------------------

func runCmlbench() {
	const n = 5
	// The equivalent of `main`'s scope: a live context nothing cancels.
	root, stop := context.WithCancel(context.Background())
	defer stop()

	fmt.Printf("%-24s%8s%10s   (min of %d)\n", "benchmark", "ns/op", "B/op", n)

	// Warm-up, mirroring the Bjolang twin's. Go needs none, but it keeps the
	// two suites driven the same way.
	cmlRingJoined(100, 100, nil)
	cmlRingNestedScope(root, 100, 100)
	cmlSpawnBurst(50_000)
	cmlSkewedChoose(50_000, nil)

	cmlReport("Ring", 1_000_000, cmlReps(n, func() cmlRep { return cmlRingJoined(1000, 1000, nil) }))
	cmlReport("Ring (ctx.Done)", 1_000_000, cmlReps(n, func() cmlRep { return cmlRingJoined(1000, 1000, root) }))
	cmlReport("Ring (nested scope)", 1_000_000, cmlReps(n, func() cmlRep { return cmlRingNestedScope(root, 1000, 1000) }))
	cmlReport("Spawn burst", 1_000_000, cmlReps(n, func() cmlRep { return cmlSpawnBurst(1_000_000) }))
	cmlReport("Skewed choose(8)", 1_000_000, cmlReps(n, func() cmlRep { return cmlSkewedChoose(1_000_000, nil) }))
	cmlReport("Skewed choose(8)+ctx", 1_000_000, cmlReps(n, func() cmlRep { return cmlSkewedChoose(1_000_000, root) }))
}
