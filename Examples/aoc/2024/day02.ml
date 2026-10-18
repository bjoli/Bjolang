(* Day 2: Red-Nosed Reports. A report is safe when it rises or falls by 1 to 3
   at each step. Part 2 also counts a report that is safe with one level removed. *)

let safe levels =
  let rec steps = function a :: (b :: _ as rest) -> (b - a) :: steps rest | _ -> [] in
  let ds = steps levels in
  List.for_all (fun d -> 1 <= d && d <= 3) ds || List.for_all (fun d -> -3 <= d && d <= -1) ds

let dampened levels =
  safe levels
  || List.exists safe (List.mapi (fun i _ -> List.filteri (fun j _ -> j <> i) levels) levels)

let () =
  let reports =
    In_channel.with_open_text Sys.argv.(1) In_channel.input_lines
    |> List.map (fun l ->
           String.split_on_char ' ' l |> List.filter (( <> ) "") |> List.map int_of_string)
    |> List.filter (( <> ) [])
  in
  let count p = List.length (List.filter p reports) in
  Printf.printf "%d\n%d\n" (count safe) (count dampened)
