# List against Vec, Map against OrderedMap, and priority queues

Three tables that time the same operations on both collections of a pair, from
10 to 100,000 items, written in Bjolang:

- `list-vs-vec.bjo`: building, reading every item, reading some items,
  changing the ends, and whole-collection operations, on ints.
- `map-vs-orderedmap.bjo`: building, looking up, changing, reading every entry,
  and what an order makes cheap. Int keys, and string keys for one lookup row.
- `priority-queues.bjo`: `(std priority-queue)` against the pairing heap in
  `pairingheap.bjo` (a copy of `Playground/pairingheap.bjo`), and its
  transient against `(std mutable heap)`. Int priorities.

`results.txt` is the output of the last run. The numbers below are from it.

## Running

```
./run.sh                      # every table
./run.sh list-vs-vec          # one
```

The first two tables together take about ten minutes.

## Reading a table

A row is one operation on collections of `n` items, done the first way and the
second. A cell is the time of one call: the fastest of 5 rounds, where each
round makes as many calls as fit in 20 ms or more. The bytes are what one call
allocated, when it allocated anything. The last column is the second time
divided by the first: under 1 means the second way is faster.

The timings come from `(std stopwatch)`, in nanoseconds. A call that does
nothing takes less than 1 ns, so a cell of 1 or 2 ns is a cost at the floor.

`run.sh` sets `DOTNET_TC_CallCountingDelayMs=0`. Without it, the runtime can
wait to optimise a case while it compiles the cases after it, and the first
row of a table can be timed before its code is optimised.

## List or Vec

| | List | Vec |
|---|---|---|
| memory per int | 32 B | about 7 B |
| first item, drop the first, add at the front | 1 ns | 2 ns, 21 ns, 14–200 ns |
| last item, length, item at an index | 1 ns per item before it | 1–2 ns |
| add at the back | a copy of the list | 10 ns |
| read every item (`loop`, fold) | 1 ns per item | 0.6 ns per item |
| recursive match that binds the rest | 1 ns per item | 30 ns and 230 B per item |
| match the first items, ignore the rest | 2 ns | 2 ns |

**Use a List** when the work is at the front and goes item by item: a stack,
a result built with `cons`, or a recursive function that matches
`(Cons h t)` and goes on with `t`. Those are the operations a list does in
constant time, and in that work a list is 3 to 30 times faster than a vec.

**Use a Vec** for everything else. It reads every item 35–40% faster from 100
items up, it knows its length and its last item, it reads any index in 1–2 ns,
it adds at the back in 10 ns, and it takes a fifth of the memory. `vec-map`,
`vec-filter`, reverse, append and slicing are faster from 100 items up (filter
from 1000), and append and slicing are close to constant time.

What to know about each:

- **Build a vec with an accumulator** (`vecing`, or a `vecbuilder`), not with
  `vec-add` in a loop. The accumulator is 5–8 times faster than `listing` from
  1000 items up. `vec-add` in a loop is 3–4 times slower than `cons` in a loop,
  because each `vec-add` copies the vec's tail, up to 32 items.
- **Do not bind the rest of a vec in a recursive match.** `[a rest ...]`
  slices the vec on every step, which copies what is left of the vec's first
  leaf: about 30 ns and 230 bytes per item, against 1 ns for a list.
  `[a b _ ...]`, which ignores the rest, does not slice and costs 2 ns. To walk
  a vec, use `loop`, a fold, or an index.
- **A list's length and last item read the whole list,** and `list-ref` reads
  every item before the index: 64 reads in a list of 100,000 take 3 ms, against
  240 ns in a vec.
- **Small collections do not differ much.** At 10 items most rows are a few
  nanoseconds apart. Pick by the operations, not by the speed.

## Map or OrderedMap

| | Map | OrderedMap |
|---|---|---|
| lookup, int keys | 1 | 1.6–3 times slower |
| lookup, string keys | 1 | 2–6.5 times slower |
| update, insert, remove | 1 | 1.2–1.9 times slower (5–9 at 10 entries), twice the allocation |
| build | 1 | up to 2.9 times slower |
| read every entry, 10,000 or more | 1 | 2–6 times faster |
| smallest key | reads every entry | 2 ns |
| the entries in a key range | reads every entry | about 400 ns for 64 entries, at any size |
| every key, in order | collect and sort | about 4 times faster |

**Use a Map** to look up, update, insert and remove by key. It is the faster
of the two at all of those, at every size, and the difference is largest for
string keys, which a Map hashes once and an OrderedMap compares at each step
down its tree.

**Use an OrderedMap** when the order of the keys matters: the smallest or
largest key, the entries between two keys, the next key after one, or the
entries in key order. A Map can only get those by reading every entry. An
OrderedMap is also faster to read in full, which a program that iterates a
large map more than it looks up in it may prefer.

## Priority queues

`(std priority-queue)` is a pairing heap, as `Playground/pairingheap.bjo` is.
It keeps an element apart from its priority, a number that puts equal
priorities in the order they were added, and an owner for its transient, and
its children are a chain of fields rather than a list.

| 100,000 entries | Std | Playground |
|---|---|---|
| add one at a time | 12.9 ms | 7.5 ms |
| build from a list | 7.2 ms | 11.3 ms |
| drain | 92–109 ms | 69–88 ms |
| half added, then one added per least taken | 60 ms | 47 ms |
| remove the least from one queue built one at a time, again and again | 2.4 ms | 2.2 ms |
| the same after balancing | 201 ns | 135 ns |

The extra fields cost the persistent queue 10–40%, and building from a list
is faster. A queue built one add at a time has a root with a child for each
add, and taking its least walks all of them: a queue used again pays that
each time. `priorityqueue-balance` (`heap-balance` in the playground) removes
the cost.

| 100,000 entries | Transient | `(std mutable heap)` |
|---|---|---|
| add all, then take all | 29.3 ms, 80 B an entry | 7.3 ms, 21 B an entry |

The transient allocates one cell for an entry when it is added, and nothing
when it takes the least. Draining a queue through a transient takes 27.8 ms,
against 92 ms by persistent removal. For a queue used in one place and never
shared, the mutable heap, an array, is 4 times faster.

## What the numbers are

One machine and one runtime, which the first lines of each table name.
The ratios move less between machines than the times. Some rows at 10 and 100
items do not follow the sizes around them, because at that size the shape of
the collection and the code the JIT made matter more than the work. A
difference of a few nanoseconds there means little.
