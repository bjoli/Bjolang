(* Day 2: Gift Shop. An ID is invalid when its digits are one block repeated:
   exactly twice in part 1, at least twice in part 2. Sums the invalid IDs. *)
let repeats s k =
  let rec go i = i >= String.length s || (s.[i] = s.[i - k] && go (i + 1)) in
  go k

let twice s = String.length s mod 2 = 0 && repeats s (String.length s / 2)

let some_repeat s =
  let n = String.length s in
  Seq.init (n / 2) succ |> Seq.exists (fun k -> n mod k = 0 && repeats s k)

let () =
  let input = In_channel.with_open_text Sys.argv.(1) In_channel.input_all in
  let part1 = ref 0 and part2 = ref 0 in
  String.trim input
  |> String.split_on_char ','
  |> List.iter (fun r ->
         Scanf.sscanf r "%d-%d" (fun lo hi ->
             for n = lo to hi do
               let s = string_of_int n in
               if twice s then part1 := !part1 + n;
               if some_repeat s then part2 := !part2 + n
             done));
  Printf.printf "%d\n%d\n" !part1 !part2
