(* Day 5: Print Queue. Part 1 sums the middle page of each update that obeys
   the rules. Part 2 puts the others in order and sums their middle pages. *)

let () =
  let lines =
    In_channel.with_open_text Sys.argv.(1) In_channel.input_lines |> List.map String.trim
  in
  let rules = Hashtbl.create 2048 in
  List.iter (fun l -> if String.contains l '|' then Hashtbl.replace rules l ()) lines;
  let updates =
    List.filter (fun l -> l <> "" && not (String.contains l '|')) lines
    |> List.map (String.split_on_char ',')
  in
  let compare a b =
    if Hashtbl.mem rules (a ^ "|" ^ b) then -1
    else if Hashtbl.mem rules (b ^ "|" ^ a) then 1
    else 0
  in
  let middle u = int_of_string (List.nth u (List.length u / 2)) in
  let part1, part2 =
    List.fold_left
      (fun (p1, p2) u ->
        let ordered = List.stable_sort compare u in
        if ordered = u then (p1 + middle u, p2) else (p1, p2 + middle ordered))
      (0, 0) updates
  in
  Printf.printf "%d\n%d\n" part1 part2
