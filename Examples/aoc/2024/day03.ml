(* Day 3: Mull It Over. Part 1 sums the products of the mul(X,Y)
   instructions. Part 2 obeys do() and don't(). *)

let () =
  let memory = In_channel.with_open_text Sys.argv.(1) In_channel.input_all in
  let re = Str.regexp {|mul(\([0-9][0-9]?[0-9]?\),\([0-9][0-9]?[0-9]?\))\|do()\|don't()|} in
  let rec go pos on total enabled =
    match Str.search_forward re memory pos with
    | exception Not_found -> (total, enabled)
    | _ -> (
        let next = Str.match_end () in
        match Str.matched_string memory with
        | "do()" -> go next true total enabled
        | "don't()" -> go next false total enabled
        | _ ->
            let g n = int_of_string (Str.matched_group n memory) in
            let product = g 1 * g 2 in
            go next on (total + product) (if on then enabled + product else enabled))
  in
  let total, enabled = go 0 true 0 0 in
  Printf.printf "%d\n%d\n" total enabled
