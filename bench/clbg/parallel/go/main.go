// Parallel Go versions of four Benchmarks Game programs. Each splits the work
// over goroutines as the Bjolang program in ../bjolang splits it over fibers,
// and computes each part as the single-threaded programs in ../../go do. The
// first argument names the benchmark.
package main

import (
	"bufio"
	"fmt"
	"math"
	"os"
	"runtime"
	"strconv"
	"sync"
)

// One worker for each logical CPU.
var workers = runtime.NumCPU()

func main() {
	out := bufio.NewWriterSize(os.Stdout, 1<<16)
	defer out.Flush()
	n, err := strconv.Atoi(os.Args[2])
	if err != nil {
		panic(err)
	}
	switch os.Args[1] {
	case "mandelbrot":
		mandelbrot(out, n)
	case "spectralnorm":
		spectralnorm(out, n)
	case "fannkuchredux":
		fannkuch(out, n)
	case "binarytrees":
		binarytrees(out, n)
	default:
		fmt.Fprintln(os.Stderr, "unknown benchmark", os.Args[1])
		os.Exit(2)
	}
}

// ------------------------------------------------------------- mandelbrot

// The rows go to the workers through an unbuffered channel, so that a worker
// that finishes early takes the next row.

func inside(cr, ci float64) bool {
	var zr, zi, tr, ti float64
	for i := 0; i < 50 && tr+ti <= 4.0; i++ {
		zi = 2.0*(zr*zi) + ci
		zr = tr - ti + cr
		tr = zr * zr
		ti = zi * zi
	}
	return tr+ti <= 4.0
}

func mandelRow(bitmap []byte, n, y int) {
	rowBytes := (n + 7) / 8
	size := float64(n)
	ci := 2.0*float64(y)/size - 1.0
	bits, at := 0, y*rowBytes
	for x := 0; x < n; x++ {
		cr := 2.0*float64(x)/size - 1.5
		bits <<= 1
		if inside(cr, ci) {
			bits++
		}
		if x%8 == 7 {
			bitmap[at] = byte(bits)
			at++
			bits = 0
		}
	}
	if n%8 != 0 {
		bitmap[at] = byte(bits << (8 - n%8))
	}
}

func mandelbrot(out *bufio.Writer, n int) {
	bitmap := make([]byte, (n+7)/8*n)
	rows := make(chan int)
	var wg sync.WaitGroup
	for w := 0; w < workers; w++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for y := range rows {
				mandelRow(bitmap, n, y)
			}
		}()
	}
	for y := 0; y < n; y++ {
		rows <- y
	}
	close(rows)
	wg.Wait()
	fmt.Fprintf(out, "P4\n%d %d\n", n, n)
	out.Write(bitmap)
}

// ---------------------------------------------------------- spectral-norm

// Each of the 40 products splits its rows into one block for each worker and
// waits for all of them before the next product starts.

func a(i, j int) float64 {
	ij := i + j
	return 1.0 / float64(ij*(ij+1)/2+(i+1))
}

func inParallel(n int, f func(lo, hi int)) {
	var wg sync.WaitGroup
	for w := 0; w < workers; w++ {
		lo, hi := w*n/workers, (w+1)*n/workers
		wg.Add(1)
		go func() {
			defer wg.Done()
			f(lo, hi)
		}()
	}
	wg.Wait()
}

func mulAv(v, out []float64) {
	inParallel(len(v), func(lo, hi int) {
		for i := lo; i < hi; i++ {
			s := 0.0
			for j := range v {
				s += a(i, j) * v[j]
			}
			out[i] = s
		}
	})
}

func mulAtv(v, out []float64) {
	inParallel(len(v), func(lo, hi int) {
		for i := lo; i < hi; i++ {
			s := 0.0
			for j := range v {
				s += a(j, i) * v[j]
			}
			out[i] = s
		}
	})
}

func spectralnorm(out *bufio.Writer, n int) {
	u, v, tmp := make([]float64, n), make([]float64, n), make([]float64, n)
	for i := range u {
		u[i] = 1
	}
	for i := 0; i < 10; i++ {
		mulAv(u, tmp)
		mulAtv(tmp, v)
		mulAv(v, tmp)
		mulAtv(tmp, u)
	}
	var vBv, vv float64
	for i := range u {
		vBv += u[i] * v[i]
		vv += v[i] * v[i]
	}
	fmt.Fprintf(out, "%.9f\n", math.Sqrt(vBv/vv))
}

// --------------------------------------------------------- fannkuch-redux

// The n! permutations are numbered in the order the single-threaded program
// makes them. They are cut into blocks of consecutive numbers, and the blocks
// go to the workers through a channel. A worker makes the first permutation
// of a block from its number, then the next ones in order. The sign of a
// permutation's flips in the checksum is the parity of its number, so the
// checksum does not depend on which worker did which block.

const fannkuchBlocks = 240

func countFlips(perm1, perm []int) int {
	copy(perm, perm1)
	flips := 0
	for k := perm[0]; k != 0; k = perm[0] {
		for i, j := 0, k; i < j; i, j = i+1, j-1 {
			perm[i], perm[j] = perm[j], perm[i]
		}
		flips++
	}
	return flips
}

// Makes permutation number idx in perm1, and the counts that continue from it.
func firstPermutation(idx int, fact, perm1, count, scratch []int) {
	for i := range perm1 {
		perm1[i] = i
	}
	for i := len(perm1) - 1; i > 0; i-- {
		d := idx / fact[i]
		count[i] = d
		idx %= fact[i]
		copy(scratch, perm1[:i+1])
		for j := 0; j <= i; j++ {
			if j+d <= i {
				perm1[j] = scratch[j+d]
			} else {
				perm1[j] = scratch[j+d-i-1]
			}
		}
	}
}

func nextPermutation(perm1, count []int) {
	first := perm1[1]
	perm1[1] = perm1[0]
	perm1[0] = first
	i := 1
	for count[i]++; count[i] > i; count[i]++ {
		count[i] = 0
		i++
		next := perm1[1]
		perm1[0] = next
		for j := 1; j < i; j++ {
			perm1[j] = perm1[j+1]
		}
		perm1[i] = first
		first = next
	}
}

func fannkuch(out *bufio.Writer, n int) {
	fact := make([]int, n+1)
	fact[0] = 1
	for i := 1; i <= n; i++ {
		fact[i] = fact[i-1] * i
	}
	block := (fact[n] + fannkuchBlocks - 1) / fannkuchBlocks
	blocks := make(chan int)
	checksums, maxes := make([]int, workers), make([]int, workers)
	var wg sync.WaitGroup
	for w := 0; w < workers; w++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			perm1, perm, count, scratch := make([]int, n), make([]int, n), make([]int, n), make([]int, n)
			checksum, maxFlips := 0, 0
			for b := range blocks {
				lo, hi := b*block, min((b+1)*block, fact[n])
				firstPermutation(lo, fact, perm1, count, scratch)
				for idx := lo; ; {
					flips := countFlips(perm1, perm)
					maxFlips = max(maxFlips, flips)
					if idx%2 == 0 {
						checksum += flips
					} else {
						checksum -= flips
					}
					idx++
					if idx == hi {
						break
					}
					nextPermutation(perm1, count)
				}
			}
			checksums[w], maxes[w] = checksum, maxFlips
		}()
	}
	for b := 0; b*block < fact[n]; b++ {
		blocks <- b
	}
	close(blocks)
	wg.Wait()
	checksum, maxFlips := 0, 0
	for w := 0; w < workers; w++ {
		checksum += checksums[w]
		maxFlips = max(maxFlips, maxes[w])
	}
	fmt.Fprintf(out, "%d\nPfannkuchen(%d) = %d\n", checksum, n, maxFlips)
}

// ----------------------------------------------------------- binary-trees

// The stretch tree and the long-lived tree are made first, as in the
// single-threaded program. Then the iterations of each depth are cut into one
// block for each worker, and every block of every depth starts at once.

type node struct {
	left, right *node
}

func bottomUp(depth int) *node {
	if depth > 0 {
		return &node{bottomUp(depth - 1), bottomUp(depth - 1)}
	}
	return &node{}
}

func check(t *node) int {
	if t == nil {
		return 0
	}
	return 1 + check(t.left) + check(t.right)
}

func binarytrees(out *bufio.Writer, n int) {
	const minDepth = 4
	maxDepth := max(minDepth+2, n)
	stretchDepth := maxDepth + 1
	fmt.Fprintf(out, "stretch tree of depth %d\t check: %d\n", stretchDepth, check(bottomUp(stretchDepth)))
	longLived := bottomUp(maxDepth)
	var depths [][]int
	var wg sync.WaitGroup
	for depth := minDepth; depth <= maxDepth; depth += 2 {
		iterations := 1 << (maxDepth - depth + minDepth)
		parts := make([]int, workers)
		depths = append(depths, parts)
		for w := 0; w < workers; w++ {
			lo, hi := w*iterations/workers, (w+1)*iterations/workers
			wg.Add(1)
			go func() {
				defer wg.Done()
				sum := 0
				for i := lo; i < hi; i++ {
					sum += check(bottomUp(depth))
				}
				parts[w] = sum
			}()
		}
	}
	wg.Wait()
	for k, parts := range depths {
		depth := minDepth + 2*k
		sum := 0
		for _, s := range parts {
			sum += s
		}
		fmt.Fprintf(out, "%d\t trees of depth %d\t check: %d\n", 1<<(maxDepth-depth+minDepth), depth, sum)
	}
	fmt.Fprintf(out, "long lived tree of depth %d\t check: %d\n", maxDepth, check(longLived))
}
