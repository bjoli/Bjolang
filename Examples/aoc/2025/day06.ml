(* Day 6: Trash Compactor. Problems side by side, separated by blank columns,
   with the operator in the last row. Part 1 reads each row of a problem as a
   number; part 2 reads each column, its digits top to bottom. *)
let () =
  let lines =
    In_channel.with_open_text Sys.argv.(1) In_channel.input_lines
    |> List.filter (fun l -> String.trim l <> "")
  in
  let width = List.fold_left (fun m l -> max m (String.length l)) 0 lines in
  let rows = List.map (fun l -> l ^ String.make (width - String.length l) ' ') lines in
  let ops, digits =
    match List.rev rows with ops :: rest -> (ops, List.rev rest) | [] -> failwith "empty"
  in
  let blank c = List.for_all (fun r -> r.[c] = ' ') rows in
  let problems = ref [] and start = ref 0 in
  for c = 0 to width do
    if c = width || blank c then begin
      if !start < c then problems := (!start, c) :: !problems;
      start := c + 1
    end
  done;
  let combine op xs = if op = "*" then List.fold_left ( * ) 1 xs else List.fold_left ( + ) 0 xs in
  let part1 = ref 0 and part2 = ref 0 in
  List.iter
    (fun (lo, hi) ->
      let op = String.trim (String.sub ops lo (hi - lo)) in
      let nums =
        List.filter_map
          (fun r -> int_of_string_opt (String.trim (String.sub r lo (hi - lo))))
          digits
      in
      part1 := !part1 + combine op nums;
      (* The digits of the column, top to bottom; a space between them is skipped. *)
      let col c =
        String.of_seq (List.to_seq digits |> Seq.map (fun r -> r.[c]) |> Seq.filter (( <> ) ' '))
        |> int_of_string
      in
      part2 := !part2 + combine op (List.init (hi - lo) (fun i -> col (lo + i))))
    !problems;
  Printf.printf "%d\n%d\n" !part1 !part2
