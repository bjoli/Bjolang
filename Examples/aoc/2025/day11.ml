(* Day 11: Reactor. Part 1 counts the paths from "you" to "out". Part 2 counts
   the paths from "svr" to "out" through both "dac" and "fft". *)
let () =
  let graph = Hashtbl.create 1000 in
  In_channel.with_open_text Sys.argv.(1) In_channel.input_lines
  |> List.iter (fun line ->
         if String.trim line <> "" then
           match String.split_on_char ':' line with
           | [ name; outs ] ->
               Hashtbl.replace graph name
                 (String.split_on_char ' ' outs |> List.filter (( <> ) ""))
           | _ -> failwith line);
  let count source target =
    let memo = Hashtbl.create 1000 in
    let rec paths node =
      if node = target then 1
      else
        match Hashtbl.find_opt memo node with
        | Some n -> n
        | None ->
            let n =
              List.fold_left (fun s m -> s + paths m) 0
                (Option.value ~default:[] (Hashtbl.find_opt graph node))
            in
            Hashtbl.add memo node n;
            n
    in
    paths source
  in
  Printf.printf "%d\n" (if Hashtbl.mem graph "you" then count "you" "out" else 0);
  Printf.printf "%d\n"
    (if Hashtbl.mem graph "svr" then
       (count "svr" "dac" * count "dac" "fft" * count "fft" "out")
       + (count "svr" "fft" * count "fft" "dac" * count "dac" "out")
     else 0)
