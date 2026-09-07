Hi Claude

Please also have a look at the file "CLAUDE-local.md"

Just some small infos to get you started:

To build and run all tests, ./run_tests.py . This runs all the files in TestFiles that start with 3 digits. Tests are reported as failed if compilation fails, or a test outputs "FAILURE: ..." or "FAIL_COMPILE"

If you change anything in bjolangruntime you need to rebuild it. It is a different c# project in BjolangRuntime

To rebuild the standard library, please use ./build_std.sh

Keep source comments as succinct as you can without being mystic. Do not reference conversations we have had. Only document what is in the code. Don't go "this does this that". Document only what needs to be explained in terms of "this does this, because later that". The comments should document what is in the code and at most document choices relevant to the code in question, not go into the reasoning behind compiler choices. This should instead be a part of a top comment if absolutely needed. 

Here is an example of a bad comment: 

"""What this forecloses: constant folding, dead-binding elimination and
eta-reduction are all deliberately absent. The first two would need to know
which calls are pure, and this pass runs before type checking, so it cannot
ask. A general inliner needs the same information plus a cost model, and
would break the guarantee above by having to decline."""

Write instead:

"""Because this normalization pass runs before the type checker, we don't
have enough type information to know if a function call is pure (side-effect
free). Without knowing if a function is pure, it is unsafe to perform
standard compiler optimizations like constant folding, dead-code elimination,
or eta-reduction at this stage, so they are not implemented here.
A general inliner needs the same information plus a cost model, and
would break the guarantee above by having to decline.""""


Do not write
"""Persistent, and deliberately: cancellation is a fact, so every
listener must see it and a listener that arrives late must still see
it. The cost is §9's limitation 10 — a cancelled scope is finished,
and resuming needs a fresh token rather than a reset.""""

Write instead:

"""Cancellation states are permanent. We do this so that if a task is
cancelled, any new listener that attaches to it immediately knows it
was cancelled, rather than waiting forever for an event that has
already ended. The cost is §9's limitation 10 — a cancelled scope
is finished, and resuming needs a fresh token rather than a reset.""""


Do not commit prompts.

