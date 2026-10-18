(* Day 9: Movie Theater. Red tiles in order around a loop of horizontal and vertical
   sides. Part 1: the largest rectangle with red tiles at two opposite corners.
   Part 2: the largest such rectangle inside the loop. *)
let () =
  let ps =
    In_channel.with_open_text Sys.argv.(1) In_channel.input_lines
    |> List.filter (fun l -> String.trim l <> "")
    |> List.map (fun l -> Scanf.sscanf l "%d,%d" (fun x y -> (x, y)))
    |> Array.of_list
  in
  let n = Array.length ps in
  let sides = List.init n (fun i -> (ps.(i), ps.((i + 1) mod n))) in
  (* Whether a side passes through the open inside of the rectangle. *)
  let crossed x0 x1 y0 y1 =
    List.exists
      (fun ((ax, ay), (bx, by)) ->
        min ax bx < x1 && max ax bx > x0 && min ay by < y1 && max ay by > y0)
      sides
  in
  (* Whether (px + 1/2, py + 1/2) is inside, counting sides to its right. *)
  let inside px py =
    List.length
      (List.filter
         (fun ((ax, ay), (bx, by)) -> ax = bx && ax > px && min ay by <= py && py < max ay by)
         sides)
    mod 2 = 1
  in
  let part1 = ref 0 and part2 = ref 0 in
  for i = 0 to n - 1 do
    for j = i + 1 to n - 1 do
      let (ax, ay), (bx, by) = (ps.(i), ps.(j)) in
      let x0, x1, y0, y1 = (min ax bx, max ax bx, min ay by, max ay by) in
      let area = (x1 - x0 + 1) * (y1 - y0 + 1) in
      part1 := max !part1 area;
      if area > !part2 && x0 < x1 && y0 < y1 && (not (crossed x0 x1 y0 y1)) && inside x0 y0
      then part2 := area
    done
  done;
  Printf.printf "%d\n%d\n" !part1 !part2
