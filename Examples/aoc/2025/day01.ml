(* Day 1: Secret Entrance. A dial of 0..99 starts at 50. Part 1 counts the
   turns that end at 0, part 2 every click that passes 0. *)
let () =
  let lines =
    In_channel.with_open_text Sys.argv.(1) In_channel.input_lines
    |> List.map String.trim
    |> List.filter (( <> ) "")
  in
  let pos = ref 50 and zeros = ref 0 and clicks = ref 0 in
  List.iter
    (fun line ->
      let d = int_of_string (String.sub line 1 (String.length line - 1)) in
      if line.[0] = 'R' then begin
        clicks := !clicks + ((!pos + d) / 100);
        pos := (!pos + d) mod 100
      end
      else begin
        if !pos = 0 then clicks := !clicks + (d / 100)
        else if d >= !pos then clicks := !clicks + 1 + ((d - !pos) / 100);
        pos := (((!pos - d) mod 100) + 100) mod 100
      end;
      if !pos = 0 then incr zeros)
    lines;
  Printf.printf "%d\n%d\n" !zeros !clicks
