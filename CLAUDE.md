Hi Claude

Just some small infos to get you started:

To build and run all tests, ./run_tests.sh . This runs all the files in TestFiles that start with 3 digits. Tests are reported as failed if compilation fails, or a test outputs "FAILURE: ..."

If you change anything in bjolangruntime you need to rebuild it. It is a different c# project in BjolangRuntime

To rebuild the standard library, please use ./build_std.sh

Keep source comments as succinct as you can without being mystic. Do not reference conversations we have had. Only document what is in the code. Don't go "this does this that". Document only what needs to be explained in terms of "this does this, because later that".

Do not commit prompts.
