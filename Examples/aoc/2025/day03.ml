(* Day 3: Lobby. Turn on k batteries of each bank, in order, to make the
   largest number: k = 2 in part 1, 12 in part 2. Sums over the banks. *)
let joltage ds k =
  let rec go j start acc =
    if j = k then acc
    else begin
      (* Leave room for the k - j - 1 digits still to pick. *)
      let i = ref start in
      for x = start + 1 to Array.length ds - (k - j) do
        if ds.(x) > ds.(!i) then i := x
      done;
      go (j + 1) (!i + 1) ((acc * 10) + ds.(!i))
    end
  in
  go 0 0 0

let () =
  let banks =
    In_channel.with_open_text Sys.argv.(1) In_channel.input_lines
    |> List.map String.trim
    |> List.filter (( <> ) "")
    |> List.map (fun l -> Array.init (String.length l) (fun i -> Char.code l.[i] - 48))
  in
  let total k = List.fold_left (fun acc b -> acc + joltage b k) 0 banks in
  Printf.printf "%d\n%d\n" (total 2) (total 12)
