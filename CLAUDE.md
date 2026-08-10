Hi Claude

Just some small infos to get you started:

To build and run all tests, ./run_tests.sh . This runs all the files in TestFiles that start with 2 digits. Tests are reported as failed if compilation fails, or a test outputs "FAILURE: ..."

If you change anything in bjolangruntime you need to rebuild it. It is a different c# project in BjolangRuntime

To rebuild the standard library, please use ./build_std.sh

Keep source comments as succinct as you can without being mystic.
