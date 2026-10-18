(* Linked in front of a solution by bench.py. OCaml runs the modules in link
   order, so this clock starts after the runtime has started and before the
   solution runs. The elapsed time goes to stderr when the program exits. *)
let () =
  let t0 = Unix.gettimeofday () in
  at_exit (fun () -> Printf.eprintf "BENCH %.4f\n" (Unix.gettimeofday () -. t0))
