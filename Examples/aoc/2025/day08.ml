(* Day 8: Playground. Connect junction boxes, closest pairs first. Part 1: after K pairs
   (10 for the example, 1000 for a real input), the product of the three largest
   circuits. Part 2: the product of the X coordinates of the last pair that makes one. *)
let () =
  let boxes =
    In_channel.with_open_text Sys.argv.(1) In_channel.input_lines
    |> List.filter (fun l -> String.trim l <> "")
    |> List.map (fun l -> String.split_on_char ',' l |> List.map int_of_string |> Array.of_list)
    |> Array.of_list
  in
  let n = Array.length boxes in
  let k = if n < 100 then 10 else 1000 in
  let dist a b = Array.fold_left ( + ) 0 (Array.map2 (fun p q -> (p - q) * (p - q)) a b) in
  let pairs =
    List.init n (fun i -> List.init (n - i - 1) (fun d -> (dist boxes.(i) boxes.(i + d + 1), i, i + d + 1)))
    |> List.concat |> List.sort compare
  in
  let parent = Array.init n Fun.id and size = Array.make n 1 in
  let rec root i =
    if parent.(i) = i then i
    else begin
      parent.(i) <- parent.(parent.(i));
      root parent.(i)
    end
  in
  let join i j =
    let a = root i and b = root j in
    a <> b
    && begin
         parent.(a) <- b;
         size.(b) <- size.(b) + size.(a);
         true
       end
  in
  List.iteri (fun m (_, i, j) -> if m < k then ignore (join i j)) pairs;
  let roots = List.filter (fun i -> root i = i) (List.init n Fun.id) in
  List.map (fun i -> size.(i)) roots
  |> List.sort (Fun.flip compare)
  |> List.filteri (fun m _ -> m < 3)
  |> List.fold_left ( * ) 1 |> Printf.printf "%d\n";
  let rec go circuits = function
    | [] -> ()
    | (_, i, j) :: rest when join i j ->
        if circuits = 2 then Printf.printf "%d\n" (boxes.(i).(0) * boxes.(j).(0))
        else go (circuits - 1) rest
    | _ :: rest -> go circuits rest
  in
  go (List.length roots) (List.filteri (fun m _ -> m >= k) pairs)
