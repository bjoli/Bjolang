(* Day 4: Printing Department. A roll (@) is reachable with fewer than four
   rolls among its eight neighbours. Part 1 counts them; part 2 removes
   reachable rolls until none are left to reach. *)
let () =
  let rolls = Hashtbl.create 4096 in
  In_channel.with_open_text Sys.argv.(1) In_channel.input_lines
  |> List.iteri (fun y l ->
         String.iteri (fun x c -> if c = '@' then Hashtbl.replace rolls (x, y) ()) l);
  let reachable () =
    Hashtbl.fold
      (fun (x, y) () acc ->
        let n = ref 0 in
        for dx = -1 to 1 do
          for dy = -1 to 1 do
            if (dx <> 0 || dy <> 0) && Hashtbl.mem rolls (x + dx, y + dy) then incr n
          done
        done;
        if !n < 4 then (x, y) :: acc else acc)
      rolls []
  in
  print_int (List.length (reachable ()));
  print_newline ();
  let rec remove total =
    match reachable () with
    | [] -> total
    | cells ->
        List.iter (Hashtbl.remove rolls) cells;
        remove (total + List.length cells)
  in
  Printf.printf "%d\n" (remove 0)
