(* Day 7: Laboratories. A beam falls from S; a splitter (^) starts two beams,
   one column to each side. Part 1 counts the splitters a beam reaches, part 2
   the timelines. *)
let () =
  let rows =
    In_channel.with_open_text Sys.argv.(1) In_channel.input_lines
    |> List.filter (fun l -> String.trim l <> "")
  in
  let w = String.length (List.hd rows) in
  let beams = ref (Array.init w (fun x -> if (List.hd rows).[x] = 'S' then 1 else 0)) in
  let splits = ref 0 in
  List.iter
    (fun row ->
      let nxt = Array.make w 0 in
      !beams
      |> Array.iteri (fun x n ->
             if n > 0 then
               if row.[x] = '^' then begin
                 incr splits;
                 if x > 0 then nxt.(x - 1) <- nxt.(x - 1) + n;
                 if x + 1 < w then nxt.(x + 1) <- nxt.(x + 1) + n
               end
               else nxt.(x) <- nxt.(x) + n);
      beams := nxt)
    rows;
  Printf.printf "%d\n%d\n" !splits (Array.fold_left ( + ) 0 !beams)
