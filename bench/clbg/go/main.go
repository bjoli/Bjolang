// Single-threaded Go versions of the Benchmarks Game programs that
// bench/clbg/bjolang ports. Each follows the algorithm of the Bjolang
// program. The first argument names the benchmark.
package main

import (
	"bufio"
	"fmt"
	"math"
	"math/big"
	"os"
	"sort"
	"strconv"
	"strings"
)

func main() {
	out := bufio.NewWriterSize(os.Stdout, 1<<16)
	defer out.Flush()
	arg := func() int {
		n, err := strconv.Atoi(os.Args[2])
		if err != nil {
			panic(err)
		}
		return n
	}
	switch os.Args[1] {
	case "nbody":
		nbody(out, arg())
	case "spectralnorm":
		spectralnorm(out, arg())
	case "fannkuchredux":
		fannkuch(out, arg())
	case "binarytrees":
		binarytrees(out, arg())
	case "mandelbrot":
		mandelbrot(out, arg())
	case "fasta":
		fasta(out, arg())
	case "knucleotide":
		knucleotide(out)
	case "revcomp":
		revcomp(out)
	case "pidigits":
		pidigits(out, arg())
	default:
		fmt.Fprintln(os.Stderr, "unknown benchmark", os.Args[1])
		os.Exit(2)
	}
}

// ---------------------------------------------------------------- n-body

type body struct {
	x, y, z, vx, vy, vz, mass float64
}

const (
	solarMass   = 4 * math.Pi * math.Pi
	daysPerYear = 365.24
)

func planet(x, y, z, vx, vy, vz, mass float64) *body {
	return &body{x, y, z, vx * daysPerYear, vy * daysPerYear, vz * daysPerYear, mass * solarMass}
}

func nbody(out *bufio.Writer, n int) {
	bodies := []*body{
		planet(0, 0, 0, 0, 0, 0, 1),
		planet(4.84143144246472090e+00, -1.16032004402742839e+00, -1.03622044471123109e-01,
			1.66007664274403694e-03, 7.69901118419740425e-03, -6.90460016972063023e-05,
			9.54791938424326609e-04),
		planet(8.34336671824457987e+00, 4.12479856412430479e+00, -4.03523417114321381e-01,
			-2.76742510726862411e-03, 4.99852801234917238e-03, 2.30417297573763929e-05,
			2.85885980666130812e-04),
		planet(1.28943695621391310e+01, -1.51111514016986312e+01, -2.23307578892655734e-01,
			2.96460137564761618e-03, 2.37847173959480950e-03, -2.96589568540237556e-05,
			4.36624404335156298e-05),
		planet(1.53796971148509165e+01, -2.59193146099879641e+01, 1.79258772950371181e-01,
			2.68067772490389322e-03, 1.62824170038242295e-03, -9.51592254519715870e-05,
			5.15138902046611451e-05),
	}
	var px, py, pz float64
	for _, b := range bodies {
		px += b.vx * b.mass
		py += b.vy * b.mass
		pz += b.vz * b.mass
	}
	bodies[0].vx = -px / solarMass
	bodies[0].vy = -py / solarMass
	bodies[0].vz = -pz / solarMass
	fmt.Fprintf(out, "%.9f\n", energy(bodies))
	for i := 0; i < n; i++ {
		advance(bodies, 0.01)
	}
	fmt.Fprintf(out, "%.9f\n", energy(bodies))
}

func energy(bodies []*body) float64 {
	e := 0.0
	for i, b := range bodies {
		e += 0.5 * (b.mass * (b.vx*b.vx + (b.vy*b.vy + b.vz*b.vz)))
		for j := i + 1; j < len(bodies); j++ {
			b2 := bodies[j]
			dx, dy, dz := b.x-b2.x, b.y-b2.y, b.z-b2.z
			e -= (b.mass * b2.mass) / math.Sqrt(dx*dx+(dy*dy+dz*dz))
		}
	}
	return e
}

func advance(bodies []*body, dt float64) {
	for i, b := range bodies {
		for j := i + 1; j < len(bodies); j++ {
			b2 := bodies[j]
			dx, dy, dz := b.x-b2.x, b.y-b2.y, b.z-b2.z
			d2 := dx*dx + (dy*dy + dz*dz)
			mag := dt / (d2 * math.Sqrt(d2))
			bm, b2m := b.mass*mag, b2.mass*mag
			b.vx -= dx * b2m
			b.vy -= dy * b2m
			b.vz -= dz * b2m
			b2.vx += dx * bm
			b2.vy += dy * bm
			b2.vz += dz * bm
		}
	}
	for _, b := range bodies {
		b.x += dt * b.vx
		b.y += dt * b.vy
		b.z += dt * b.vz
	}
}

// ---------------------------------------------------------- spectral-norm

func a(i, j int) float64 {
	ij := i + j
	return 1.0 / float64(ij*(ij+1)/2+(i+1))
}

func mulAv(v, out []float64) {
	for i := range v {
		s := 0.0
		for j := range v {
			s += a(i, j) * v[j]
		}
		out[i] = s
	}
}

func mulAtv(v, out []float64) {
	for i := range v {
		s := 0.0
		for j := range v {
			s += a(j, i) * v[j]
		}
		out[i] = s
	}
}

func mulAtAv(v, out, tmp []float64) {
	mulAv(v, tmp)
	mulAtv(tmp, out)
}

func spectralnorm(out *bufio.Writer, n int) {
	u, v, tmp := make([]float64, n), make([]float64, n), make([]float64, n)
	for i := range u {
		u[i] = 1
	}
	for i := 0; i < 10; i++ {
		mulAtAv(u, v, tmp)
		mulAtAv(v, u, tmp)
	}
	var vBv, vv float64
	for i := range u {
		vBv += u[i] * v[i]
		vv += v[i] * v[i]
	}
	fmt.Fprintf(out, "%.9f\n", math.Sqrt(vBv/vv))
}

// --------------------------------------------------------- fannkuch-redux

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

func fannkuch(out *bufio.Writer, n int) {
	perm1, perm, count := make([]int, n), make([]int, n), make([]int, n)
	for i := range perm1 {
		perm1[i] = i
	}
	r, maxFlips, checksum, permCount := n, 0, 0, 0
	for {
		for ; r > 1; r-- {
			count[r-1] = r
		}
		flips := countFlips(perm1, perm)
		if flips > maxFlips {
			maxFlips = flips
		}
		if permCount%2 == 0 {
			checksum += flips
		} else {
			checksum -= flips
		}
		for r = 1; ; r++ {
			if r == n {
				fmt.Fprintf(out, "%d\nPfannkuchen(%d) = %d\n", checksum, n, maxFlips)
				return
			}
			perm0 := perm1[0]
			for i := 0; i < r; i++ {
				perm1[i] = perm1[i+1]
			}
			perm1[r] = perm0
			count[r]--
			if count[r] > 0 {
				break
			}
		}
		permCount++
	}
}

// ----------------------------------------------------------- binary-trees

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
	for depth := minDepth; depth <= maxDepth; depth += 2 {
		iterations := 1 << (maxDepth - depth + minDepth)
		sum := 0
		for i := 0; i < iterations; i++ {
			sum += check(bottomUp(depth))
		}
		fmt.Fprintf(out, "%d\t trees of depth %d\t check: %d\n", iterations, depth, sum)
	}
	fmt.Fprintf(out, "long lived tree of depth %d\t check: %d\n", maxDepth, check(longLived))
}

// ------------------------------------------------------------- mandelbrot

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

func mandelbrot(out *bufio.Writer, n int) {
	rowBytes := (n + 7) / 8
	bitmap := make([]byte, rowBytes*n)
	size := float64(n)
	for y := 0; y < n; y++ {
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
	fmt.Fprintf(out, "P4\n%d %d\n", n, n)
	out.Write(bitmap)
}

// ------------------------------------------------------------------ fasta

const lineLength = 60

const alu = "GGCCGGGCGCGGTGGCTCACGCCTGTAATCCCAGCACTTTGGGAGGCCGAGGCGGGCGGATCACCTGAGGTCAGGAGTTCGAGA" +
	"CCAGCCTGGCCAACATGGTGAAACCCCGTCTCTACTAAAAATACAAAAATTAGCCGGGCGTGGTGGCGCGCGCCTGTAATCCCA" +
	"GCTACTCGGGAGGCTGAGGCAGGAGAATCGCTTGAACCCGGGAGGCGGAGGTTGCAGTGAGCCGAGATCGCGCCACTGCACTCC" +
	"AGCCTGGGCGACAGAGCGAGACTCCGTCTCAAAAA"

const (
	im = 139968
	ia = 3877
	ic = 29573
)

var seed = 42

func random(max float64) float64 {
	seed = (seed*ia + ic) % im
	return max * float64(seed) / im
}

func repeatFasta(out *bufio.Writer, source []byte, n int) {
	line := make([]byte, lineLength+1)
	k := 0
	for left := n; left > 0; left -= lineLength {
		count := min(left, lineLength)
		for i := 0; i < count; i++ {
			line[i] = source[k]
			if k+1 == len(source) {
				k = 0
			} else {
				k++
			}
		}
		line[count] = '\n'
		out.Write(line[:count+1])
	}
}

func randomFasta(out *bufio.Writer, codes []byte, probs []float64, n int) {
	line := make([]byte, lineLength+1)
	last := len(probs) - 1
	for left := n; left > 0; left -= lineLength {
		count := min(left, lineLength)
		for i := 0; i < count; i++ {
			r := random(1.0)
			j := 0
			for j < last && r >= probs[j] {
				j++
			}
			line[i] = codes[j]
		}
		line[count] = '\n'
		out.Write(line[:count+1])
	}
}

func cumulative(ps []float64) []float64 {
	acc := make([]float64, len(ps))
	sum := 0.0
	for i, p := range ps {
		sum += p
		acc[i] = sum
	}
	return acc
}

func fasta(out *bufio.Writer, n int) {
	out.WriteString(">ONE Homo sapiens alu\n")
	repeatFasta(out, []byte(alu), 2*n)
	out.WriteString(">TWO IUB ambiguity codes\n")
	randomFasta(out, []byte("acgtBDHKMNRSVWY"),
		cumulative([]float64{0.27, 0.12, 0.12, 0.27, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02}),
		3*n)
	out.WriteString(">THREE Homo sapiens frequency\n")
	randomFasta(out, []byte("acgt"),
		cumulative([]float64{0.3029549426680, 0.1979883004921, 0.1975473066391, 0.3015094502008}),
		5*n)
}

// ------------------------------------------------------------ k-nucleotide

func code(c byte) byte {
	switch c {
	case 'a', 'A':
		return 0
	case 'c', 'C':
		return 1
	case 'g', 'G':
		return 2
	}
	return 3
}

func newScanner() *bufio.Scanner {
	in := bufio.NewScanner(os.Stdin)
	in.Buffer(make([]byte, 1<<16), 1<<20)
	return in
}

func readSequence() []byte {
	in := newScanner()
	for in.Scan() && !strings.HasPrefix(in.Text(), ">THREE") {
	}
	var sb strings.Builder
	for in.Scan() {
		line := in.Bytes()
		if len(line) > 0 && line[0] == '>' {
			break
		}
		sb.Write(line)
	}
	s := sb.String()
	sq := make([]byte, len(s))
	for i := 0; i < len(s); i++ {
		sq[i] = code(s[i])
	}
	return sq
}

func countKmers(sq []byte, k int) map[int64]int {
	counts := make(map[int64]int)
	mask := int64(1)<<(2*k) - 1
	key := int64(0)
	for i, c := range sq {
		key = (key<<2 | int64(c)) & mask
		if i >= k-1 {
			counts[key]++
		}
	}
	return counts
}

func keyToString(key int64, k int) string {
	var sb strings.Builder
	for i := k - 1; i >= 0; i-- {
		sb.WriteByte("ACGT"[(key>>(2*i))&3])
	}
	return sb.String()
}

func stringToKey(s string) int64 {
	key := int64(0)
	for i := 0; i < len(s); i++ {
		key = key<<2 | int64(code(s[i]))
	}
	return key
}

func writeFrequencies(out *bufio.Writer, sq []byte, k int) {
	counts := countKmers(sq, k)
	total := float64(len(sq) + 1 - k)
	type freq struct {
		count int
		name  string
	}
	var fs []freq
	for key, c := range counts {
		fs = append(fs, freq{c, keyToString(key, k)})
	}
	sort.Slice(fs, func(i, j int) bool {
		if fs[i].count != fs[j].count {
			return fs[i].count > fs[j].count
		}
		return fs[i].name < fs[j].name
	})
	for _, f := range fs {
		fmt.Fprintf(out, "%s %.3f\n", f.name, 100.0*float64(f.count)/total)
	}
	out.WriteString("\n")
}

func knucleotide(out *bufio.Writer) {
	sq := readSequence()
	writeFrequencies(out, sq, 1)
	writeFrequencies(out, sq, 2)
	for _, name := range []string{"GGT", "GGTA", "GGTATT", "GGTATTTTAATT", "GGTATTTTAATTTATAGT"} {
		counts := countKmers(sq, len(name))
		fmt.Fprintf(out, "%d\t%s\n", counts[stringToKey(name)], name)
	}
}

// ------------------------------------------------------ reverse-complement

func complements() []byte {
	table := make([]byte, 256)
	for i := range table {
		table[i] = byte(i)
	}
	from, to := "ACGTUMRWSYKVHDBN", "TGCAAKYWSRMBDHVN"
	for i := 0; i < len(from); i++ {
		table[from[i]] = to[i]
		table[from[i]+32] = to[i]
	}
	return table
}

func writeReversed(out *bufio.Writer, table []byte, sq []byte) {
	n := len(sq)
	if n == 0 {
		return
	}
	lines := (n + lineLength - 1) / lineLength
	buf := make([]byte, n+lines)
	i, col := 0, 0
	for from := n - 1; from >= 0; from-- {
		if col == lineLength {
			buf[i] = '\n'
			i++
			col = 0
		}
		buf[i] = table[sq[from]]
		i++
		col++
	}
	buf[i] = '\n'
	out.Write(buf)
}

func revcomp(out *bufio.Writer) {
	in := newScanner()
	table := complements()
	var sq []byte
	for in.Scan() {
		line := in.Bytes()
		if len(line) > 0 && line[0] == '>' {
			writeReversed(out, table, sq)
			out.Write(line)
			out.WriteByte('\n')
			sq = sq[:0]
		} else {
			sq = append(sq, line...)
		}
	}
	writeReversed(out, table, sq)
}

// --------------------------------------------------------------- pidigits

// Values are made anew for each result, as the other programs make them, and
// not updated in place, which math/big would allow.
func pidigits(out *bufio.Writer, n int) {
	acc, den, num := big.NewInt(0), big.NewInt(1), big.NewInt(1)
	two, three, four, ten := big.NewInt(2), big.NewInt(3), big.NewInt(4), big.NewInt(10)
	i, k := 0, 0
	for i < n {
		k++
		k2 := big.NewInt(int64(2*k + 1))
		acc = new(big.Int).Mul(new(big.Int).Add(acc, new(big.Int).Mul(num, two)), k2)
		den = new(big.Int).Mul(den, k2)
		num = new(big.Int).Mul(num, big.NewInt(int64(k)))
		if num.Cmp(acc) > 0 {
			continue
		}
		d := new(big.Int).Quo(new(big.Int).Add(new(big.Int).Mul(num, three), acc), den)
		d4 := new(big.Int).Quo(new(big.Int).Add(new(big.Int).Mul(num, four), acc), den)
		if d.Cmp(d4) != 0 {
			continue
		}
		out.WriteByte(byte('0' + d.Int64()))
		i++
		if i%10 == 0 {
			fmt.Fprintf(out, "\t:%d\n", i)
		}
		acc = new(big.Int).Mul(new(big.Int).Sub(acc, new(big.Int).Mul(den, d)), ten)
		num = new(big.Int).Mul(num, ten)
	}
	if n%10 != 0 {
		for j := n % 10; j < 10; j++ {
			out.WriteByte(' ')
		}
		fmt.Fprintf(out, "\t:%d\n", n)
	}
}
