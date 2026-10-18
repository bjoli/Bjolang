(* Day 1: Historian Hysteria. Part 1 pairs the two sorted columns and sums the
   distances. Part 2 sums each left ID times its count in the right column. *)

let words s = String.split_on_char ' ' s |> List.filter (( <> ) "")

let () =
  let pairs =
    In_channel.with_open_text Sys.argv.(1) In_channel.input_lines
    |> List.filter_map (fun l ->
           match words l with
           | [ a; b ] -> Some (int_of_string a, int_of_string b)
           | _ -> None)
  in
  let left = List.map fst pairs |> List.sort compare in
  let right = List.map snd pairs |> List.sort compare in
  let sum = List.fold_left ( + ) 0 in
  print_int (List.map2 (fun a b -> abs (a - b)) left right |> sum);
  print_newline ();
  let counts = Hashtbl.create 1024 in
  let count x = Option.value ~default:0 (Hashtbl.find_opt counts x) in
  List.iter (fun b -> Hashtbl.replace counts b (count b + 1)) right;
  print_int (List.map (fun a -> a * count a) left |> sum);
  print_newline ()
