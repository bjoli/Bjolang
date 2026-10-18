(* Day 12: Christmas Tree Farm. Count the regions that all their presents fit in, turned
   or flipped, without overlap. Most regions are settled by area; the rest are searched
   square by square. *)
let () =
  let lines = In_channel.with_open_text Sys.argv.(1) In_channel.input_lines in
  let is_region l = String.contains l 'x' in
  (* Each shape is a header "N:" followed by its rows. *)
  let rec parse_shapes acc = function
    | h :: rest when String.ends_with ~suffix:":" h ->
        let rec take y cells = function
          | r :: rest when r <> "" && not (is_region r) ->
              let row = List.init (String.length r) (fun x -> (x, r.[x])) in
              let cs = List.filter_map (fun (x, c) -> if c = '#' then Some (x, y) else None) row in
              take (y + 1) (cells @ cs) rest
          | rest -> (cells, rest)
        in
        let cells, rest = take 0 [] rest in
        parse_shapes (cells :: acc) rest
    | _ :: rest -> parse_shapes acc rest
    | [] -> List.rev acc
  in
  let shapes = Array.of_list (parse_shapes [] lines) in
  let normalise cells =
    let x0 = List.fold_left (fun m (x, _) -> min m x) max_int cells
    and y0 = List.fold_left (fun m (_, y) -> min m y) max_int cells in
    List.map (fun (x, y) -> (x - x0, y - y0)) cells
    |> List.sort (fun (ax, ay) (bx, by) -> compare (ay, ax) (by, bx))
  in
  let orientations cells =
    let rec go i cells acc =
      if i = 4 then acc
      else
        let cells = List.map (fun (x, y) -> (-y, x)) cells in
        let flipped = List.map (fun (x, y) -> (-x, y)) cells in
        go (i + 1) cells (normalise cells :: normalise flipped :: acc)
    in
    List.sort_uniq compare (go 0 cells [])
  in
  let orients = Array.map orientations shapes in
  let search w h counts slack =
    let grid = Array.make (w * h) false in
    let fits o x0 y0 =
      List.for_all
        (fun (dx, dy) ->
          let x = x0 + dx and y = y0 + dy in
          0 <= x && x < w && 0 <= y && y < h && not grid.(x + (y * w)))
        o
    in
    let mark o x0 y0 v = List.iter (fun (dx, dy) -> grid.(x0 + dx + ((y0 + dy) * w)) <- v) o in
    let rec go at slack =
      if Array.for_all (( = ) 0) counts then true
      else if at >= w * h then false
      else if grid.(at) then go (at + 1) slack
      else
        let x = at mod w and y = at / w in
        let place i o =
          let ax = x - fst (List.hd o) in
          (* the shape's first cell goes on (x, y) *)
          fits o ax y
          && begin
               mark o ax y true;
               counts.(i) <- counts.(i) - 1;
               let ok = go (at + 1) slack in
               counts.(i) <- counts.(i) + 1;
               mark o ax y false;
               ok
             end
        in
        Array.to_seqi orients
        |> Seq.exists (fun (i, os) -> counts.(i) > 0 && List.exists (place i) os)
        || slack > 0
           && begin
                grid.(at) <- true;
                let ok = go (at + 1) (slack - 1) in
                grid.(at) <- false;
                ok
              end
    in
    go 0 slack
  in
  let fits_region line =
    Scanf.sscanf line "%dx%d: %[^\n]" (fun w h rest ->
        let counts =
          String.split_on_char ' ' rest |> List.filter (( <> ) "") |> List.map int_of_string
          |> Array.of_list
        in
        let needed = ref 0 in
        Array.iteri (fun i c -> needed := !needed + (c * List.length shapes.(i))) counts;
        if !needed > w * h then false
        else if Array.fold_left ( + ) 0 counts <= w / 3 * (h / 3) then true
        else search w h counts ((w * h) - !needed))
  in
  List.filter is_region lines |> List.filter fits_region |> List.length |> Printf.printf "%d\n"
