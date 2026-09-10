// The "varied" suite: shapes the main suite never exercises, hunting dark spots.
// Twin of bench/Bench/Varied.cs and bench/hopac/Varied.fs — same counts, same
// channel topology, same work per unit. All channels unbuffered.
//
// Run with: go run . -suite varied -reps 5

package main

import (
	"fmt"
	"sync"
	"sync/atomic"
	"time"
)

func runVaried(reps int) {
	repeat(reps, fanIn)
	repeat(reps, manyToMany)
	repeat(reps, wideChoose)
	repeat(reps, skewedChoose)
	repeat(reps, chooseSend)
	repeat(reps, chooseTimeout)
	repeat(reps, pipeline)
	repeat(reps, parallelPingPong)
}

// 1. Fan-in: 100 long-lived producers, one channel, one consumer.
func fanIn() {
	const producers = 100
	const perProducer = 10_000
	const n = producers * perProducer

	ch := make(chan int)

	start := time.Now()

	for p := 0; p < producers; p++ {
		go func() {
			for i := 0; i < perProducer; i++ {
				ch <- 1
			}
		}()
	}

	total := 0
	for i := 0; i < n; i++ {
		total += <-ch
	}

	elapsed := time.Since(start)
	fmt.Printf("Fan-in:           %d in %v (%.0f ns/op), total=%d\n",
		n, elapsed.Round(time.Millisecond), float64(elapsed.Nanoseconds())/float64(n), total)
}

// 2. Many-to-many: 100 producers, 100 consumers, one channel.
func manyToMany() {
	const side = 100
	const per = 10_000
	const n = side * per

	ch := make(chan int)
	var wg sync.WaitGroup
	wg.Add(2 * side)

	start := time.Now()

	for p := 0; p < side; p++ {
		go func() {
			defer wg.Done()
			for i := 0; i < per; i++ {
				ch <- 1
			}
		}()
		go func() {
			defer wg.Done()
			for i := 0; i < per; i++ {
				<-ch
			}
		}()
	}
	wg.Wait()

	elapsed := time.Since(start)
	fmt.Printf("Many-to-many:     %d in %v (%.0f ns/op), msgs\n",
		n, elapsed.Round(time.Millisecond), float64(elapsed.Nanoseconds())/float64(n))
}

// 3./4. Wide and skewed choose over 8 channels.
func wideChooseCore(name string, skewed bool) {
	const k = 8
	const rounds = 1_000_000

	var chs [k]chan int
	for i := range chs {
		chs[i] = make(chan int)
	}
	done := make(chan struct{})

	go func() {
		for i := 0; i < rounds; i++ {
			if skewed {
				chs[0] <- i
			} else {
				chs[i&(k-1)] <- i
			}
		}
		close(done)
	}()

	start := time.Now()

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
		}
	}

	<-done
	elapsed := time.Since(start)
	fmt.Printf("%-17s %d in %v (%.0f ns/op), ops\n",
		name+":", rounds, elapsed.Round(time.Millisecond),
		float64(elapsed.Nanoseconds())/float64(rounds))
}

func wideChoose()   { wideChooseCore("Wide choose (8)", false) }
func skewedChoose() { wideChooseCore("Skewed choose(8)", true) }

// 5. Choose send: select over two sends, two receivers draining.
func chooseSend() {
	const rounds = 1_000_000

	a := make(chan int)
	b := make(chan int)
	var count int64
	var wg sync.WaitGroup
	wg.Add(2)

	drain := func(ch chan int) {
		defer wg.Done()
		for {
			if v := <-ch; v == -1 {
				return
			}
			atomic.AddInt64(&count, 1)
		}
	}
	go drain(a)
	go drain(b)

	start := time.Now()

	for i := 0; i < rounds; i++ {
		select {
		case a <- 1:
		case b <- 1:
		}
	}
	a <- -1
	b <- -1
	wg.Wait()

	elapsed := time.Since(start)
	fmt.Printf("Choose send (2):  %d in %v (%.0f ns/op), received=%d\n",
		rounds, elapsed.Round(time.Millisecond),
		float64(elapsed.Nanoseconds())/float64(rounds), count)
}

// 6. Choose + timeout: the idiomatic select-with-deadline loop. time.After
// allocates a timer per iteration, exactly as the BjoML twin arms one per sync.
func chooseTimeout() {
	const rounds = 100_000

	ch := make(chan int)
	done := make(chan struct{})

	go func() {
		for i := 0; i < rounds; i++ {
			ch <- i
		}
		close(done)
	}()

	start := time.Now()

	for i := 0; i < rounds; i++ {
		select {
		case <-ch:
		case <-time.After(time.Second):
		}
	}

	<-done
	elapsed := time.Since(start)
	fmt.Printf("Choose+timeout:   %d in %v (%.0f ns/op), ops\n",
		rounds, elapsed.Round(time.Millisecond),
		float64(elapsed.Nanoseconds())/float64(rounds))
}

// 7. Pipeline: source -> 4 stages -> sink, 500k items.
func pipeline() {
	const stages = 4
	const items = 500_000

	chs := make([]chan int, stages+1)
	for i := range chs {
		chs[i] = make(chan int)
	}
	result := make(chan int64)

	go func() {
		for i := 0; i < items; i++ {
			chs[0] <- i
		}
		chs[0] <- -1
	}()

	for s := 0; s < stages; s++ {
		in, out := chs[s], chs[s+1]
		go func() {
			for {
				v := <-in
				if v == -1 {
					out <- -1
					return
				}
				out <- v + 1
			}
		}()
	}

	go func() {
		var total int64
		for {
			v := <-chs[stages]
			if v == -1 {
				result <- total
				return
			}
			total += int64(v)
		}
	}()

	start := time.Now()
	total := <-result
	elapsed := time.Since(start)

	fmt.Printf("Pipeline (4):     %d in %v (%.0f ns/op), total=%d\n",
		items, elapsed.Round(time.Millisecond),
		float64(elapsed.Nanoseconds())/float64(items), total)
}

// 8. Parallel ping-pong: 12 independent pairs.
func parallelPingPong() {
	const pairs = 12
	const rounds = 100_000
	const n = pairs * rounds

	var wg sync.WaitGroup
	wg.Add(2 * pairs)

	start := time.Now()

	for p := 0; p < pairs; p++ {
		a := make(chan int)
		b := make(chan int)
		go func() {
			defer wg.Done()
			for i := 0; i < rounds; i++ {
				v := <-a
				b <- v * 2
			}
		}()
		go func() {
			defer wg.Done()
			for i := 0; i < rounds; i++ {
				a <- i
				<-b
			}
		}()
	}
	wg.Wait()

	elapsed := time.Since(start)
	fmt.Printf("Par ping-pong(12) %d in %v (%.0f ns/op), round trips\n",
		n, elapsed.Round(time.Millisecond), float64(elapsed.Nanoseconds())/float64(n))
}
