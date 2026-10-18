(* Day 4: Ceres Search. Part 1 counts XMAS in all eight directions. Part 2
   counts the crosses of two MAS on the diagonals of a 3x3 square. *)

let () =
  let rows =
    In_channel.with_open_text Sys.argv.(1) In_channel.input_lines
    |> List.map String.trim |> List.filter (( <> ) "") |> Array.of_list
  in
  let h, w = (Array.length rows, String.length rows.(0)) in
  let at x y = if 0 <= x && x < w && 0 <= y && y < h then rows.(y).[x] else '.' in
  let dirs = [ (-1, -1); (-1, 0); (-1, 1); (0, -1); (0, 1); (1, -1); (1, 0); (1, 1) ] in
  let count f =
    let n = ref 0 in
    for y = 0 to h - 1 do
      for x = 0 to w - 1 do
        n := !n + f x y
      done
    done;
    !n
  in
  let xmas x y (dx, dy) =
    String.to_seqi "XMAS" |> Seq.for_all (fun (i, c) -> at (x + i * dx) (y + i * dy) = c)
  in
  print_int (count (fun x y -> List.length (List.filter (xmas x y) dirs)));
  print_newline ();
  let mas x y dx dy =
    match (at (x - dx) (y - dy), at (x + dx) (y + dy)) with
    | 'M', 'S' | 'S', 'M' -> true
    | _ -> false
  in
  let cross x y = at x y = 'A' && mas x y 1 1 && mas x y 1 (-1) in
  print_int (count (fun x y -> Bool.to_int (cross x y)));
  print_newline ()
