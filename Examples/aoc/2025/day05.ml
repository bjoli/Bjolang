(* Day 5: Cafeteria. Part 1 counts the IDs on hand that some range covers;
   part 2 counts every ID the ranges cover. *)
let () =
  let input = In_channel.with_open_text Sys.argv.(1) In_channel.input_all in
  let words s = Str.split (Str.regexp "[ \t\r\n]+") s in
  match Str.split (Str.regexp_string "\n\n") (String.trim input) with
  | [ head; tail ] ->
      let ranges =
        words head
        |> List.map (fun l -> Scanf.sscanf l "%d-%d" (fun lo hi -> (lo, hi)))
        |> List.sort compare
      in
      let ids = List.map int_of_string (words tail) in
      ids
      |> List.filter (fun i -> List.exists (fun (lo, hi) -> lo <= i && i <= hi) ranges)
      |> List.length |> Printf.printf "%d\n";
      (* nxt: the first ID not yet counted *)
      let covered, _ =
        List.fold_left
          (fun (covered, nxt) (lo, hi) ->
            (covered + max 0 (hi + 1 - max lo nxt), max nxt (hi + 1)))
          (0, 0) ranges
      in
      Printf.printf "%d\n" covered
  | _ -> failwith "bad input"
