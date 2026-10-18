(* Day 10: Factory. Part 1: the fewest presses that light the pattern, a press toggling
   its lights. Part 2: the fewest presses that bring each counter to its target, a press
   adding one to its counters. Summed over the machines. *)
let solve line =
  let toks = String.split_on_char ' ' line |> List.filter (( <> ) "") |> Array.of_list in
  let inner s = String.sub s 1 (String.length s - 2) in
  let nums s = String.split_on_char ',' (inner s) |> List.map int_of_string in
  let lights = String.to_seq (inner toks.(0)) |> Seq.map (( = ) '#') |> Array.of_seq in
  let nb = Array.length toks - 2 in
  let buttons = Array.init nb (fun i -> nums toks.(i + 1)) in
  let targets = Array.of_list (nums toks.(nb + 1)) in
  let k = Array.length targets in
  (* Every subset of the buttons, pressed once each: what it adds, and how many presses. *)
  let presses =
    List.init (1 lsl nb) (fun mask ->
        let adds = Array.make k 0 and n = ref 0 in
        buttons
        |> Array.iteri (fun i b ->
               if mask land (1 lsl i) <> 0 then begin
                 incr n;
                 List.iter (fun c -> adds.(c) <- adds.(c) + 1) b
               end);
        (adds, !n))
  in
  let toggles =
    presses
    |> List.filter (fun (adds, _) ->
           Array.for_all Fun.id (Array.mapi (fun i l -> (adds.(i) mod 2 = 1) = l) lights))
    |> List.map snd |> List.fold_left min max_int
  in
  (* The subsets grouped by the parity of what they add. *)
  let by_parity = Hashtbl.create 1024 in
  List.iter
    (fun ((adds, _) as p) ->
      let key = Array.map (fun a -> a mod 2) adds in
      Hashtbl.replace by_parity key
        (p :: Option.value ~default:[] (Hashtbl.find_opt by_parity key)))
    (List.rev presses);
  (* The buttons pressed an odd number of times give the targets' parity; what is left
     is even, and is half the problem, pressed twice. *)
  let memo = Hashtbl.create 1024 in
  let rec fewest t =
    if Array.for_all (( = ) 0) t then Some 0
    else
      match Hashtbl.find_opt memo t with
      | Some r -> r
      | None ->
          let best =
            Option.value ~default:[] (Hashtbl.find_opt by_parity (Array.map (fun x -> x mod 2) t))
            |> List.fold_left
                 (fun best (adds, n) ->
                   if not (Array.for_all2 ( <= ) adds t) then best
                   else
                     match (fewest (Array.map2 (fun a x -> (x - a) / 2) adds t), best) with
                     | None, _ -> best
                     | Some r, Some b when b <= n + (2 * r) -> best
                     | Some r, _ -> Some (n + (2 * r)))
                 None
          in
          Hashtbl.add memo t best;
          best
  in
  (toggles, Option.get (fewest targets))

let () =
  let answers =
    In_channel.with_open_text Sys.argv.(1) In_channel.input_lines
    |> List.filter (fun l -> String.trim l <> "")
    |> List.map solve
  in
  Printf.printf "%d\n%d\n"
    (List.fold_left (fun s (a, _) -> s + a) 0 answers)
    (List.fold_left (fun s (_, b) -> s + b) 0 answers)
