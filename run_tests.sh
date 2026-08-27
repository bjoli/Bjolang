#!/bin/bash

# Color definitions
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

echo -e "${BLUE}=== Bjolang Optimized Parallel Test Runner ===${NC}"
echo ""

# One run at a time in a given working tree.
#
# Everything here is written *next to the source it came from*: `lib/std/*.dll`,
# a `.exe` beside each fixture, the log directory. Two runs at once therefore
# do not merely interleave — they rebuild the same standard library into the
# same files while the other is linking against it, and the second one's
# `rm -rf` on the log directory pulls it out from under the first. The symptom
# is a scatter of unrelated failures that do not reproduce.
#
# Waiting rather than refusing: a second run almost always means "run the tests
# again", and finishing the first is what that wants.
exec 9> .test-lock
if ! flock -n 9; then
    echo -e "${YELLOW}Another run_tests.sh is using this working tree. Waiting for it...${NC}"
    flock 9
fi

# The lock must not outlive this script, and left alone it does.
#
# `flock` is held per *open file description*, not per process, so every child
# that inherits fd 9 keeps the lock held for as long as it lives. Most children
# here exit with the run — but two do not. MSBuild's node-reuse daemons survive
# a build by design, and so does the Roslyn server that `csc -shared` talks to,
# which is the thing `Build.fs` reaches for to avoid paying JIT on every
# sub-compilation. Both then sit on the lock for their idle timeout, and the
# *next* run waits on a lock that nobody is left to release. The symptom is a
# run that hangs at "Waiting for it..." with no other run in sight.
#
# Closing the fd for the child is the whole fix, and shadowing `dotnet` is how
# every call site gets it without having to remember: a function is inherited
# by subshells, command substitutions and background jobs alike, which is where
# the calls below actually live.
dotnet() { command dotnet "$@" 9>&-; }

# 1. Build the compiler once in Release mode
echo -e "${BLUE}Building compiler in Release mode...${NC}"
dotnet build -c Release > /dev/null
if [ $? -ne 0 ]; then
    echo -e "${RED}Compiler build failed!${NC}"
    exit 1
fi
echo -e "${GREEN}Compiler build succeeded.${NC}"
echo ""

COMPILER_DLL="bin/Release/net10.0/Bjolang.dll"
if [ ! -f "$COMPILER_DLL" ]; then
    echo -e "${RED}Could not find compiler binary at $COMPILER_DLL${NC}"
    exit 1
fi

# Create a temporary directory for logs in the workspace
LOG_DIR="TestFiles/.test-logs"
rm -rf "$LOG_DIR"
mkdir -p "$LOG_DIR"

# Clean up temp files on exit
cleanup() {
    rm -rf "$LOG_DIR"
}
trap cleanup EXIT

MAX_JOBS=$(nproc 2>/dev/null || echo 8)

# --- The standard library ----------------------------------------------------
#
# Everything below imports `(std prelude)`, and an import of a `.bjo` resolves
# through `ensureLibrary` — which counts the compiler as part of a module's
# source closure, so a rebuilt compiler leaves every `lib/std/*.dll` out of
# date. Left to the compilations below, each would notice that and rebuild the
# standard library *itself*, concurrently, into the same files.
#
# It used to be hidden rather than absent: the fixtures were built one at a
# time, so the first of them rebuilt the prelude and the rest found it current.
# Building anything in parallel makes it a race, so the answer is to be
# deliberate about it — once, here, before anything else runs.
STD_DIR="lib/std"

# Asked of `build_std.sh` rather than decided here.
#
# There used to be a `std_is_stale` beside this that globbed `lib/std/*.bjo` and
# compared timestamps. It was wrong in both directions. A `.bjo` sitting in
# `lib/std` that the library does not build — a module still being written — has
# no `.dll`, which read as "stale", so the whole standard library was rebuilt on
# every run and the condition never cleared. And the glob did not cover
# `.protobjo`, so editing `Foldable.protobjo`, which `prelude` includes, read as
# "current" and the suite ran against a stale prelude.
#
# `build_std.sh` names the modules the standard library consists of, in
# dependency order, and `bjor` decides staleness per module against the whole
# source closure — includes and all. Both facts already lived somewhere. This
# asks them instead of keeping a third copy that drifts from both.
echo -e "${BLUE}Checking the standard library...${NC}"
if ! ./build_std.sh > "$LOG_DIR/std.log" 2>&1; then
    echo -e "${RED}Standard library build failed!${NC}"
    cat "$LOG_DIR/std.log"
    exit 1
fi

std_built=$(grep -c "^Built library:" "$LOG_DIR/std.log")
std_current=$(grep -c "^Up to date:" "$LOG_DIR/std.log")
echo -e "${GREEN}Standard library: $std_built built, $std_current already current.${NC}"

# --- Fixture libraries -------------------------------------------------------
#
# `TestFiles/inc` holds the modules the "across a dll" tests link against. The
# group runner never sees them — they carry no numeric prefix — so without this
# nothing builds them, and the tests that import one fail with a missing module
# class rather than anything that names the cause.
#
# Which files are modules is read off the source: one with an `(export ...)` or
# a `def/macro` is a module and gets compiled, and the rest are include
# fragments, spliced into whoever includes them and not modules at all. A macro
# counts because macros are published whether or not anything is exported, so a
# fixture that is nothing but transformers has no `(export ...)` to find.
#
# A fixture's edges are every `"....bjo"` it names, which covers `(import ...)`
# and `(include ...)` alike and does not care how many paths one form lists.
# They decide two things: what may be built at the same time, and what a
# rebuild forces — a module reads its dependencies' metadata at build time, so
# rebuilding one makes everything downstream of it out of date.
#
# A module is rebuilt when its `.dll` is older than a source it is built from,
# older than the compiler, or older than the standard library it links against.
# The last two are the same reason: a `.dll` carries metadata read at build time
# from the compiler's format and from the prelude's own metadata, so a rebuild of
# either invalidates it however fresh the source is. That is the rule
# `Pipeline.ensureLibrary` applies to an imported `.bjo` — and `bjor` to a
# program — spelled out here because these are compiled directly rather than
# reached through an import.
#
# One thing timestamps cannot see: a *moved* checkout. A compiled module records
# the absolute paths of its dependencies, so one built somewhere else links
# against files that are no longer there — which is what made this rebuild
# everything, every run. The directory the fixtures were built from is recorded
# instead, and a change to it rebuilds the lot.
INC_DIR="TestFiles/inc"
FIXTURE_STAMP="$INC_DIR/.built-from"
FIXTURES_BUILT=0
FIXTURES_CURRENT=0

build_fixture_libs() {
    local f d modules=()

    for f in "$INC_DIR"/*.bjo; do
        [ -f "$f" ] || continue
        if grep -qE '^[[:space:]]*\((export|def/macro)' "$f"; then
            modules+=("$f")
        fi
    done

    [ ${#modules[@]} -eq 0 ] && return 0

    local moved=0
    [ "$(cat "$FIXTURE_STAMP" 2>/dev/null)" = "$PWD" ] || moved=1

    # Sibling files each module is built from, and whether it has to be built.
    local -A edges stale built
    for f in "${modules[@]}"; do
        edges["$f"]=$(grep -ohE '"[^"]+\.bjo"' "$f" | tr -d '"' | xargs -r -n1 basename \
                      | sort -u | sed "s|^|$INC_DIR/|" | while read -r d; do
                          [ -f "$d" ] && [ "$d" != "$f" ] && echo "$d"
                      done)
    done

    for f in "${modules[@]}"; do
        local dll="${f%.bjo}.dll"
        stale["$f"]=0

        if [ $moved -eq 1 ] || [ ! -f "$dll" ] || [ "$f" -nt "$dll" ] || [ "$COMPILER_DLL" -nt "$dll" ]; then
            stale["$f"]=1
        else
            for d in ${edges["$f"]} "$STD_DIR"/*.dll; do
                [ -f "$d" ] && [ "$d" -nt "$dll" ] && stale["$f"]=1
            done
        fi
    done

    # A rebuilt dependency is a rebuilt dependent: what crossed between them is
    # metadata, read once, at the time the dependent was compiled.
    local changed=1
    while [ $changed -eq 1 ]; do
        changed=0
        for f in "${modules[@]}"; do
            [ "${stale[$f]}" = 1 ] && continue
            for d in ${edges["$f"]}; do
                if [ "${stale[$d]:-0}" = 1 ]; then
                    stale["$f"]=1
                    changed=1
                fi
            done
        done
    done

    # One wave at a time: everything whose dependencies are behind it goes at
    # once, because nothing in a wave can be waiting on anything else in it.
    local pending=("${modules[@]}")

    while [ ${#pending[@]} -gt 0 ]; do
        local wave=() remaining=() ready pids=() names=()

        for f in "${pending[@]}"; do
            ready=1
            for d in ${edges["$f"]}; do
                # Only a sibling *module* is something to wait for. A fragment
                # is spliced into whoever names it and builds nothing.
                if [ -n "${stale[$d]+set}" ] && [ -z "${built[$d]+set}" ]; then
                    ready=0
                fi
            done
            if [ $ready -eq 1 ]; then wave+=("$f"); else remaining+=("$f"); fi
        done

        if [ ${#wave[@]} -eq 0 ]; then
            echo -e "${RED}Could not resolve a build order for: ${pending[*]}${NC}"
            echo -e "${RED}A cycle, or an import naming a file that is not there.${NC}"
            exit 1
        fi

        for f in "${wave[@]}"; do
            built["$f"]=1

            if [ "${stale[$f]}" != 1 ]; then
                FIXTURES_CURRENT=$((FIXTURES_CURRENT + 1))
                continue
            fi

            rm -f "${f%.bjo}.dll"
            dotnet "$COMPILER_DLL" --lib "$f" > "$LOG_DIR/inc_$(basename "${f%.bjo}").log" 2>&1 &
            pids+=($!)
            names+=("$f")

            while [ $(jobs -r -p | wc -l) -ge $MAX_JOBS ]; do
                sleep 0.02
            done
        done

        local i
        for i in "${!pids[@]}"; do
            if ! wait "${pids[$i]}"; then
                echo -e "${RED}Failed to build fixture library ${names[$i]}${NC}"
                cat "$LOG_DIR/inc_$(basename "${names[$i]%.bjo}").log"
                exit 1
            fi
            FIXTURES_BUILT=$((FIXTURES_BUILT + 1))
        done

        pending=("${remaining[@]}")
    done

    echo "$PWD" > "$FIXTURE_STAMP"
}

echo -e "${BLUE}Building fixture libraries in $INC_DIR...${NC}"
build_fixture_libs
echo -e "${GREEN}Fixture libraries: $FIXTURES_BUILT built, $FIXTURES_CURRENT already current.${NC}"
echo ""

# Helper function to run a prefix group
run_prefix_group() {
    local prefix="$1"
    local log_file="$2"
    local files=$(ls TestFiles/${prefix}_*.bjo 2>/dev/null | sort)
    
    for bjo_file in $files; do
        local basename=$(basename "$bjo_file")
        
        local exe_file="${bjo_file%.bjo}.exe"
        local dll_file="${bjo_file%.bjo}.dll"
        
        # Cleanup previously generated files
        rm -f "$exe_file" "$dll_file" "${bjo_file%.bjo}.runtimeconfig.json" "${bjo_file%.bjo}.deps.json"
        
        # Compile
        echo "=== Compiling $basename ===" >> "$log_file"
        dotnet "$COMPILER_DLL" "$bjo_file" >> "$log_file" 2>&1
        local compile_status=$?
        
        if [ $compile_status -ne 0 ]; then
            echo "FAIL_COMPILE: $basename" >> "$log_file"
            return 1
        fi
        
        # Check if exe was actually generated
        if [ ! -f "$exe_file" ]; then
            echo "PASS_LIB: $basename" >> "$log_file"
            continue
        fi
        
        # Run
        echo "=== Running $basename ===" >> "$log_file"
        local input_file="${bjo_file%.bjo}.in"
        local run_output_file="${log_file}.run"
        rm -f "$run_output_file"
        
        if [ -f "$input_file" ]; then
            dotnet "$exe_file" < "$input_file" > "$run_output_file" 2>&1
        else
            dotnet "$exe_file" < /dev/null > "$run_output_file" 2>&1
        fi
        local run_status=$?
        
        # Append output to main log
        cat "$run_output_file" >> "$log_file"
        
        if [ $run_status -ne 0 ]; then
            echo "FAIL_RUN: $basename" >> "$log_file"
            rm -f "$run_output_file"
            return 2
        fi
        
        if grep -q "FAILURE:" "$run_output_file"; then
            echo "FAIL_LOGIC: $basename" >> "$log_file"
            rm -f "$run_output_file"
            return 3
        fi
        
        rm -f "$run_output_file"
        echo "PASS: $basename" >> "$log_file"
    done
    return 0
}

# Find all unique 3-digit prefixes in TestFiles/
prefixes=$(ls TestFiles/[0-9][0-9][0-9]_*.bjo 2>/dev/null | xargs -n1 basename | cut -c1-3 | sort -u)

if [ -z "$prefixes" ]; then
    echo -e "${RED}No test files matching TestFiles/[0-9][0-9][0-9]_*.bjo found.${NC}"
    exit 1
fi

echo -e "${BLUE}Running tests in parallel (max $MAX_JOBS concurrent jobs)...${NC}"
echo "--------------------------------------------------"

declare -A pids
start_time=$(date +%s.%N 2>/dev/null || date +%s)

for prefix in $prefixes; do
    log_file="$LOG_DIR/${prefix}.log"
    run_prefix_group "$prefix" "$log_file" &
    pids["$prefix"]=$!
    
    # Simple concurrency control
    while [ $(jobs -r -p | wc -l) -ge $MAX_JOBS ]; do
        sleep 0.02
    done
done

# Wait and process results in order
success_count=0
fail_compile_count=0
fail_run_count=0
skipped_count=0

declare -a compiled_failed
declare -a run_failed
declare -a skipped_list

for prefix in $prefixes; do
    log_file="$LOG_DIR/${prefix}.log"
    # Get group exit status by waiting on its specific PID
    wait ${pids["$prefix"]}
    status=$?
    
    # Determine what files are in this group for display
    files_in_group=$(ls TestFiles/${prefix}_*.bjo 2>/dev/null | xargs -n1 basename | tr '\n' ' ' | sed 's/ $//')
    
    if [ $status -eq 0 ]; then
        # Check if skipped or passed
        if grep -q "PASS" "$log_file" || grep -q "PASS_LIB" "$log_file"; then
            echo -e "  [${GREEN}PASS${NC}] Group $prefix: $files_in_group"
            ((success_count++))
        elif grep -q "SKIP" "$log_file"; then
            echo -e "  [${YELLOW}SKIP${NC}] Group $prefix: $files_in_group"
            ((skipped_count++))
            skipped_list+=("$files_in_group")
        else
            echo -e "  [${GREEN}PASS${NC}] Group $prefix: $files_in_group"
            ((success_count++))
        fi
    else
        echo -e "  [${RED}FAIL${NC}] Group $prefix: $files_in_group"
        if grep -q "FAIL_COMPILE" "$log_file"; then
            ((fail_compile_count++))
            failed_file=$(grep "FAIL_COMPILE" "$log_file" | cut -d' ' -f2)
            compiled_failed+=("$failed_file")
        elif grep -q "FAIL_RUN" "$log_file"; then
            ((fail_run_count++))
            failed_file=$(grep "FAIL_RUN" "$log_file" | cut -d' ' -f2)
            run_failed+=("$failed_file")
        else
            ((fail_run_count++))
            failed_file=$(grep "FAIL_LOGIC" "$log_file" | cut -d' ' -f2)
            run_failed+=("$failed_file (logic failure: contains 'FAILURE:')")
        fi
    fi
done

# --- Error tests: programs that must be REJECTED ---
#
# `TestFiles/errors/` holds programs that are supposed to fail to compile. A
# rejection is only worth anything if it is the *right* rejection — a program
# rejected by an unrelated bug still "passes" a test that only checks for
# failure — so a file may name the message it expects with one or more
#
#   ;; EXPECT-ERROR: <substring>
#
# lines. Every substring named must appear in the compiler's output. A file with
# no such line only has to fail, which at least pins that it is still rejected.
ERROR_DIR="TestFiles/errors"
error_total=0
error_failed=0
declare -a error_failures

if [ -d "$ERROR_DIR" ] && ls "$ERROR_DIR"/*.bjo >/dev/null 2>&1; then
    echo "--------------------------------------------------"
    echo -e "${BLUE}Running error tests (must be rejected)...${NC}"

    run_error_test() {
        local bjo_file="$1"
        local result_file="$2"
        local err_name
        err_name=$(basename "$bjo_file")

        local output
        output=$(dotnet "$COMPILER_DLL" "$bjo_file" 2>&1)
        local status=$?

        # A rejected program must not have left an artefact behind.
        rm -f "${bjo_file%.bjo}.exe" "${bjo_file%.bjo}.dll" \
              "${bjo_file%.bjo}.runtimeconfig.json" "${bjo_file%.bjo}.deps.json"

        if [ $status -eq 0 ]; then
            echo "FAIL|$err_name|compiled successfully, but was expected to be rejected" > "$result_file"
            return
        fi

        local missing=""
        while IFS= read -r expected; do
            [ -z "$expected" ] && continue
            if ! printf '%s' "$output" | grep -qF -- "$expected"; then
                missing="$expected"
                break
            fi
        done < <(sed -n 's/^;;[[:space:]]*EXPECT-ERROR:[[:space:]]*//p' "$bjo_file")

        if [ -n "$missing" ]; then
            echo "FAIL|$err_name|rejected, but not for the stated reason. Expected to find: $missing" > "$result_file"
        else
            echo "PASS|$err_name|" > "$result_file"
        fi
    }

    declare -A error_pids
    for bjo_file in "$ERROR_DIR"/*.bjo; do
        err_name=$(basename "$bjo_file" .bjo)
        run_error_test "$bjo_file" "$LOG_DIR/err_${err_name}.result" &
        error_pids["$err_name"]=$!

        while [ $(jobs -r -p | wc -l) -ge $MAX_JOBS ]; do
            sleep 0.02
        done
    done

    for bjo_file in "$ERROR_DIR"/*.bjo; do
        err_name=$(basename "$bjo_file" .bjo)
        wait ${error_pids["$err_name"]}
        error_total=$((error_total + 1))

        IFS='|' read -r verdict name reason < "$LOG_DIR/err_${err_name}.result"
        if [ "$verdict" = "PASS" ]; then
            echo -e "  [${GREEN}PASS${NC}] $name"
        else
            echo -e "  [${RED}FAIL${NC}] $name"
            error_failed=$((error_failed + 1))
            error_failures+=("$name: $reason")
        fi
    done
fi

# --- Warning tests: programs that must COMPILE, and say something -------------
#
# `TestFiles/warnings/` holds programs the compiler accepts while suspecting they
# were not meant. A warning has no other way of being tested: the behavioural
# phase reads the program's own output and the error phase requires a rejection,
# so a diagnostic on a program that builds fine falls between the two.
#
# Each file must compile *successfully*, and every
#
#   ;; EXPECT-WARNING: <substring>
#
# line in it must appear in the compiler's output. Compiling successfully is half
# the assertion: a warning is not an error, and one that became an error would
# pass a test that only looked for the text.
WARNING_DIR="TestFiles/warnings"
warning_total=0
warning_failed=0
declare -a warning_failures

if [ -d "$WARNING_DIR" ] && ls "$WARNING_DIR"/*.bjo >/dev/null 2>&1; then
    echo "--------------------------------------------------"
    echo -e "${BLUE}Running warning tests (must compile, and say so)...${NC}"

    run_warning_test() {
        local bjo_file="$1"
        local result_file="$2"
        local warn_name
        warn_name=$(basename "$bjo_file")

        local output
        output=$(dotnet "$COMPILER_DLL" "$bjo_file" 2>&1)
        local status=$?

        rm -f "${bjo_file%.bjo}.exe" "${bjo_file%.bjo}.dll" \
              "${bjo_file%.bjo}.runtimeconfig.json" "${bjo_file%.bjo}.deps.json"

        if [ $status -ne 0 ]; then
            echo "FAIL|$warn_name|was expected to compile, and did not" > "$result_file"
            return
        fi

        local missing=""
        while IFS= read -r expected; do
            [ -z "$expected" ] && continue
            if ! printf '%s' "$output" | grep -qF -- "$expected"; then
                missing="$expected"
                break
            fi
        done < <(sed -n 's/^;;[[:space:]]*EXPECT-WARNING:[[:space:]]*//p' "$bjo_file")

        if [ -n "$missing" ]; then
            echo "FAIL|$warn_name|compiled, but said nothing about it. Expected to find: $missing" > "$result_file"
        else
            echo "PASS|$warn_name|" > "$result_file"
        fi
    }

    declare -A warning_pids
    for bjo_file in "$WARNING_DIR"/*.bjo; do
        warn_name=$(basename "$bjo_file" .bjo)
        run_warning_test "$bjo_file" "$LOG_DIR/warn_${warn_name}.result" &
        warning_pids["$warn_name"]=$!

        while [ $(jobs -r -p | wc -l) -ge $MAX_JOBS ]; do
            sleep 0.02
        done
    done

    for bjo_file in "$WARNING_DIR"/*.bjo; do
        warn_name=$(basename "$bjo_file" .bjo)
        wait ${warning_pids["$warn_name"]}
        warning_total=$((warning_total + 1))

        IFS='|' read -r verdict name reason < "$LOG_DIR/warn_${warn_name}.result"
        if [ "$verdict" = "PASS" ]; then
            echo -e "  [${GREEN}PASS${NC}] $name"
        else
            echo -e "  [${RED}FAIL${NC}] $name"
            warning_failed=$((warning_failed + 1))
            warning_failures+=("$name: $reason")
        fi
    done
fi

# --- Generated-C# assertions -------------------------------------------------
#
# A few properties are about the C# that comes out rather than about what the
# program prints, and no behavioural test can see them. `(= 1 2)` compiling to
# `(1 == 2)` is the example: `=` is a trait method, so if a statically resolved
# call stopped being spliced back down to the operator every test here would
# still pass while the whole numeric surface got slower.
#
# Each fixture is compiled as a library with --debug, which dumps the generated
# C# to `out.cs` in the working directory, and every `;; EXPECT-CS:` line in it
# is an extended regex the dump has to match. Sequential, and in a directory of
# its own, because that dump is written to a fixed name.
CODEGEN_DIR="TestFiles/codegen"
codegen_total=0
codegen_failed=0
declare -a codegen_failures

# --- The four phases below run together --------------------------------------
#
# Codegen, the REPL sessions, the reproducibility check and the staleness check
# touch none of each other's files: codegen writes `TestFiles/codegen` and a
# dump directory of its own, a REPL session writes nothing but logs,
# reproducibility rebuilds `TestFiles/006_*`, and staleness works in a directory
# it writes from scratch. Run one after another they were 5.8s of a 15s run on a
# 24-core machine that averaged 11 busy cores, which is most of the idle time in
# the whole script.
#
# Each is a brace group writing to a file rather than to the terminal, and the
# three files are replayed in the old order once all three have finished. Three
# phases reporting at once would interleave into something nobody could read,
# and a test runner's log is read far more often than it is waited for.
#
# What may *not* join them is anything above: the group runner and the error
# tests already saturate the machine on their own, and reproducibility rebuilds
# the very artefacts group 006 owns.

# A phase's tally, handed back through the filesystem because a background
# subshell cannot assign to its parent's variables.
write_phase_result() {
    local name="$1" total="$2" failed="$3"
    shift 3
    printf '%s %s\n' "$total" "$failed" > "$LOG_DIR/phase_$name.counts"
    if [ "$#" -gt 0 ]; then
        printf '%s\n' "$@" > "$LOG_DIR/phase_$name.failures"
    else
        : > "$LOG_DIR/phase_$name.failures"
    fi
}

{
if [ -d "$CODEGEN_DIR" ] && ls "$CODEGEN_DIR"/*.bjo >/dev/null 2>&1; then
    echo "--------------------------------------------------"
    echo -e "${BLUE}Checking the generated C#...${NC}"

    ROOT="$PWD"
    CS_WORK="$LOG_DIR/codegen"
    mkdir -p "$CS_WORK"

    for bjo_file in "$CODEGEN_DIR"/*.bjo; do
        cs_name=$(basename "$bjo_file" .bjo)
        codegen_total=$((codegen_total + 1))

        ( cd "$CS_WORK" && dotnet "$ROOT/$COMPILER_DLL" --debug --lib "$ROOT/$bjo_file" ) \
            > "$LOG_DIR/cs_${cs_name}.log" 2>&1
        cs_status=$?

        rm -f "${bjo_file%.bjo}.dll" "${bjo_file%.bjo}.pdb"

        if [ $cs_status -ne 0 ]; then
            echo -e "  [${RED}FAIL${NC}] $cs_name.bjo"
            codegen_failed=$((codegen_failed + 1))
            codegen_failures+=("$cs_name.bjo: did not compile")
            continue
        fi

        cs_missing=""
        while IFS= read -r pattern; do
            [ -z "$pattern" ] && continue
            if ! grep -qE -- "$pattern" "$CS_WORK/out.cs"; then
                cs_missing="$pattern"
                break
            fi
        done < <(sed -n 's/^;;[[:space:]]*EXPECT-CS:[[:space:]]*//p' "$bjo_file")

        if [ -n "$cs_missing" ]; then
            echo -e "  [${RED}FAIL${NC}] $cs_name.bjo"
            codegen_failed=$((codegen_failed + 1))
            codegen_failures+=("$cs_name.bjo: the generated C# has no match for: $cs_missing")
        else
            echo -e "  [${GREEN}PASS${NC}] $cs_name.bjo"
        fi
    done
fi
write_phase_result codegen "$codegen_total" "$codegen_failed" "${codegen_failures[@]}"
} > "$LOG_DIR/phase_codegen.out" 2>&1 &
pid_codegen=$!

# --- The REPL ----------------------------------------------------------------
#
# A scripted session against a recorded transcript. Every `.in` under
# `TestFiles/repl` is fed to `--repl` and its output compared to the `.expected`
# beside it.
#
# Whole-transcript rather than a property per line, because what is being pinned
# is the *semantics* — which value each entry produces, that a redefinition
# shadows rather than replaces, that an impl written mid-session reaches the
# entries after it, and which diagnostics come out where. Those are decisions,
# and a decision quietly changing is exactly what this is for.
#
# Prompts are stripped and the dependency-build narration dropped: whether the
# standard library happened to need rebuilding is not part of the session.
REPL_DIR="TestFiles/repl"
repl_total=0
repl_failed=0
declare -a repl_failures

{
if [ -d "$REPL_DIR" ] && ls "$REPL_DIR"/*.in >/dev/null 2>&1; then
    echo "--------------------------------------------------"
    echo -e "${BLUE}Running REPL sessions...${NC}"

    for in_file in "$REPL_DIR"/*.in; do
        repl_name=$(basename "$in_file" .in)
        repl_total=$((repl_total + 1))
        expected="${in_file%.in}.expected"

        dotnet "$COMPILER_DLL" --repl < "$in_file" 2>&1 \
            | sed 's/^\(bjo> \|\.\.\.> \)*//' \
            | grep -vE "^(Building imported module|$)" > "$LOG_DIR/repl_$repl_name.out"

        if [ ! -f "$expected" ]; then
            echo -e "  [${RED}FAIL${NC}] $repl_name (no .expected beside it)"
            repl_failed=$((repl_failed + 1))
            repl_failures+=("$repl_name: no recorded transcript")
        elif diff -u "$expected" "$LOG_DIR/repl_$repl_name.out" > "$LOG_DIR/repl_$repl_name.diff"; then
            echo -e "  [${GREEN}PASS${NC}] $repl_name"
        else
            echo -e "  [${RED}FAIL${NC}] $repl_name"
            repl_failed=$((repl_failed + 1))
            repl_failures+=("$repl_name: the session no longer matches its transcript")
            cat "$LOG_DIR/repl_$repl_name.diff"
        fi
    done
fi
write_phase_result repl "$repl_total" "$repl_failed" "${repl_failures[@]}"
} > "$LOG_DIR/phase_repl.out" 2>&1 &
pid_repl=$!

# --- Reproducibility ---------------------------------------------------------
#
# A module's `.dll` must not depend on how it was reached. Staleness is decided
# by timestamp, so nothing *breaks* the moment this fails — the symptom is a
# rebuild that produces a different artefact for no reason the source explains,
# and then a diff that cannot be accounted for.
#
# Two things are checked, and they fail for different reasons. Building the same
# module twice catches a non-deterministic backend: `csc` stamps a fresh MVID
# into every assembly unless told not to, and records the path of the generated
# C#, which lives in a temp directory named after a GUID. Building it once with
# its dependency compiled in-process and once out-of-process catches the other
# thing — compiler state that belongs to a compilation and was not moved into
# `Session`, which is invisible until two modules share a process.
REPRO_MAIN="TestFiles/006_modules_and_input.bjo"
REPRO_DEP="TestFiles/006_lib"
repro_failed=0
declare -a repro_failures

repro_build() {
    rm -f "$REPRO_DEP.dll" "${REPRO_MAIN%.bjo}.exe"
    # `env` execs the real binary, so the shell function above does not apply
    # and the lock fd has to be closed here by hand.
    env $1 dotnet "$COMPILER_DLL" "$REPRO_MAIN" 9>&- > "$LOG_DIR/repro.log" 2>&1 \
        || { cat "$LOG_DIR/repro.log"; return 1; }
    md5sum "$REPRO_DEP.dll" "${REPRO_MAIN%.bjo}.exe" | cut -d' ' -f1 | tr '\n' ' '
}

{
echo "--------------------------------------------------"
echo -e "${BLUE}Checking that a build reproduces...${NC}"

# A build that will not compile is a failure of this phase rather than a reason
# to leave: an `exit` here would only end the brace group, taking the tally with
# it and reporting nothing at all. The summary fails the run on `repro_failed`,
# so saying so is enough — and it gets to print the rest of the results first.
if repro_a=$(repro_build "BJOLANG_X=1") \
   && repro_b=$(repro_build "BJOLANG_X=1") \
   && repro_c=$(repro_build "BJOLANG_OUT_OF_PROCESS_DEPS=1"); then

    if [ "$repro_a" = "$repro_b" ]; then
        echo -e "  [${GREEN}PASS${NC}] the same source builds to the same bytes"
    else
        echo -e "  [${RED}FAIL${NC}] the same source builds to the same bytes"
        repro_failed=1
        repro_failures+=("two identical builds differed: $repro_a vs $repro_b")
    fi

    if [ "$repro_a" = "$repro_c" ]; then
        echo -e "  [${GREEN}PASS${NC}] an in-process dependency build matches an out-of-process one"
    else
        echo -e "  [${RED}FAIL${NC}] an in-process dependency build matches an out-of-process one"
        repro_failed=1
        repro_failures+=("in-process $repro_a vs out-of-process $repro_c — compilation state has leaked between modules")
    fi
else
    echo -e "  [${RED}FAIL${NC}] a reproducibility build did not compile"
    repro_failed=1
    repro_failures+=("a reproducibility build did not compile; see $LOG_DIR/repro.log")
fi

rm -f "$REPRO_DEP.dll" "${REPRO_MAIN%.bjo}.exe" "${REPRO_MAIN%.bjo}.runtimeconfig.json" \
      "${REPRO_MAIN%.bjo}.deps.json" "$REPRO_DEP.pdb" "${REPRO_MAIN%.bjo}.pdb"
write_phase_result repro 0 "$repro_failed" "${repro_failures[@]}"
} > "$LOG_DIR/phase_repro.out" 2>&1 &
pid_repro=$!

# --- Staleness ---------------------------------------------------------------
#
# What crosses an import edge is metadata, read once, when the importer was
# compiled — so a module is out of date when something it imports has changed,
# and that may be two edges away. Nothing on disk says so: the importer's own
# source is untouched, and its `.dll` is newer than it. Left unchecked the
# symptom is not a failure but a silence, an edit that simply does not happen,
# which is the kind of thing a test suite exists to notice.
#
# A chain of three, written here rather than kept in `TestFiles`, because the
# fixture is a *sequence of edits* rather than a file: every other phase would
# otherwise have to know not to touch it, and a half-applied edit left behind by
# an interrupted run would be someone's confusing afternoon.
STALE_DIR="$LOG_DIR/staleness"
stale_total=0
stale_failed=0
declare -a stale_failures

stale_check() {
    local label="$1" wanted="$2" got="$3"
    stale_total=$((stale_total + 1))

    if [ "$got" = "$wanted" ]; then
        echo -e "  [${GREEN}PASS${NC}] $label"
    else
        echo -e "  [${RED}FAIL${NC}] $label"
        stale_failed=$((stale_failed + 1))
        stale_failures+=("$label: got '$got', wanted '$wanted'")
    fi
}

{
echo "--------------------------------------------------"
echo -e "${BLUE}Checking that a changed dependency is rebuilt...${NC}"

mkdir -p "$STALE_DIR"

cat > "$STALE_DIR/leaf.bjo" <<'EOF'
(export flavour)
(import (std prelude))
(: flavour (-> string))
(defun (flavour) "banana")
EOF

cat > "$STALE_DIR/middle.bjo" <<'EOF'
(export describe)
(import (std prelude))
(import "leaf.bjo")
(: describe (-> string))
(defun (describe) (string-append "a " (flavour)))
EOF

cat > "$STALE_DIR/app.bjo" <<'EOF'
(import (std prelude))
(import "middle.bjo")
(defun (main args) (println (describe)) 0)
EOF

if dotnet "$COMPILER_DLL" "$STALE_DIR/app.bjo" > "$LOG_DIR/stale.log" 2>&1; then
    stale_check "a chain of three modules builds" \
                "a banana" "$(dotnet "$STALE_DIR/app.exe" 2>&1)"

    # Nothing but the leaf is touched. `middle.dll` is newer than its own source
    # and older than nothing it names, so only its *imports* say it is stale.
    sed -i 's/"banana"/"cloudberry"/' "$STALE_DIR/leaf.bjo"

    if dotnet "$COMPILER_DLL" "$STALE_DIR/app.bjo" >> "$LOG_DIR/stale.log" 2>&1; then
        stale_check "an edit two modules down reaches the program" \
                    "a cloudberry" "$(dotnet "$STALE_DIR/app.exe" 2>&1)"
    else
        stale_check "an edit two modules down reaches the program" \
                    "a cloudberry" "the rebuild did not compile; see $LOG_DIR/stale.log"
    fi

    # And the other half of it: staleness that is not there costs nothing. A
    # walk that rebuilt on every build would pass everything above and make the
    # cache worthless.
    stale_check "a build with nothing changed rebuilds nothing" "0" \
                "$(dotnet "$COMPILER_DLL" "$STALE_DIR/app.bjo" 2>&1 \
                   | grep -c '^Building imported module')"
else
    stale_check "a chain of three modules builds" \
                "a banana" "it did not compile; see $LOG_DIR/stale.log"
fi

write_phase_result staleness "$stale_total" "$stale_failed" "${stale_failures[@]}"
} > "$LOG_DIR/phase_staleness.out" 2>&1 &
pid_staleness=$!

# --- Joining the four --------------------------------------------------------
#
# Replayed in the order they used to run in, so a log reads as it always did,
# and the tallies are read back out of the files each phase left behind.
wait $pid_codegen
wait $pid_repl
wait $pid_repro
wait $pid_staleness

cat "$LOG_DIR/phase_codegen.out" "$LOG_DIR/phase_repl.out" "$LOG_DIR/phase_repro.out" \
    "$LOG_DIR/phase_staleness.out"

read -r codegen_total codegen_failed < "$LOG_DIR/phase_codegen.counts"
read -r repl_total repl_failed < "$LOG_DIR/phase_repl.counts"
read -r repro_total repro_failed < "$LOG_DIR/phase_repro.counts"
read -r stale_total stale_failed < "$LOG_DIR/phase_staleness.counts"
mapfile -t codegen_failures < "$LOG_DIR/phase_codegen.failures"
mapfile -t repl_failures < "$LOG_DIR/phase_repl.failures"
mapfile -t stale_failures < "$LOG_DIR/phase_staleness.failures"
mapfile -t repro_failures < "$LOG_DIR/phase_repro.failures"

end_time=$(date +%s.%N 2>/dev/null || date +%s)
duration=$(echo "$end_time - $start_time" | bc -l 2>/dev/null)
if [ -z "$duration" ]; then
    start_sec=$(echo "$start_time" | cut -d'.' -f1)
    end_sec=$(echo "$end_time" | cut -d'.' -f1)
    duration=$((end_sec - start_sec))
else
    # Format/truncate duration to 2 decimal places manually to be locale-independent
    if [[ "$duration" == *.* ]]; then
        integer_part="${duration%.*}"
        decimal_part="${duration#*.}"
        duration="${integer_part}.${decimal_part:0:2}"
    fi
fi

echo "--------------------------------------------------"
echo ""
echo -e "${BLUE}=== Summary ===${NC}"
echo -e "Total groups:       $(echo "$prefixes" | wc -w)"
echo -e "Skipped:            $skipped_count"
echo -e "Compile failures:   $fail_compile_count"
echo -e "Execution failures: $fail_run_count"
echo -e "Successful runs:    $success_count"
if [ $error_total -gt 0 ]; then
    echo -e "Error tests:        $((error_total - error_failed))/$error_total rejected as expected"
fi
if [ $warning_total -gt 0 ]; then
    echo -e "Warning tests:      $((warning_total - warning_failed))/$warning_total warned as expected"
fi
if [ $codegen_total -gt 0 ]; then
    echo -e "Codegen tests:      $((codegen_total - codegen_failed))/$codegen_total emitted as expected"
fi
if [ $repl_total -gt 0 ]; then
    echo -e "REPL sessions:      $((repl_total - repl_failed))/$repl_total match their transcript"
fi
if [ $stale_total -gt 0 ]; then
    echo -e "Staleness:          $((stale_total - stale_failed))/$stale_total rebuilt as expected"
fi
echo -e "Total time:         ${duration}s"
echo ""

if [ ${#error_failures[@]} -ne 0 ]; then
    echo -e "${RED}=== Error Test Failures ===${NC}"
    for failure in "${error_failures[@]}"; do
        echo -e "  $failure"
    done
    echo ""
fi

if [ ${#warning_failures[@]} -ne 0 ]; then
    echo -e "${RED}=== Warning Test Failures ===${NC}"
    for failure in "${warning_failures[@]}"; do
        echo -e "  $failure"
    done
    echo ""
fi

if [ ${#repl_failures[@]} -ne 0 ]; then
    echo -e "${RED}=== REPL Session Failures ===${NC}"
    for failure in "${repl_failures[@]}"; do
        echo -e "  $failure"
    done
    echo ""
fi

if [ ${#repro_failures[@]} -ne 0 ]; then
    echo -e "${RED}=== Reproducibility Failures ===${NC}"
    for failure in "${repro_failures[@]}"; do
        echo -e "  $failure"
    done
    echo ""
fi

if [ ${#stale_failures[@]} -ne 0 ]; then
    echo -e "${RED}=== Staleness Failures ===${NC}"
    for failure in "${stale_failures[@]}"; do
        echo -e "  $failure"
    done
    echo ""
fi

if [ ${#codegen_failures[@]} -ne 0 ]; then
    echo -e "${RED}=== Codegen Test Failures ===${NC}"
    for failure in "${codegen_failures[@]}"; do
        echo -e "  $failure"
    done
    echo ""
fi

# Print failure details
if { [ $error_failed -ne 0 ] || [ $codegen_failed -ne 0 ] || [ $repl_failed -ne 0 ] \
     || [ $repro_failed -ne 0 ] || [ $stale_failed -ne 0 ] || [ $warning_failed -ne 0 ]; } \
   && [ ${#compiled_failed[@]} -eq 0 ] && [ ${#run_failed[@]} -eq 0 ]; then
    exit 1
fi

if [ ${#compiled_failed[@]} -ne 0 ] || [ ${#run_failed[@]} -ne 0 ]; then
    echo -e "${RED}=== Failure Details ===${NC}"
    for prefix in $prefixes; do
        log_file="$LOG_DIR/${prefix}.log"
        # Check if failure is logged
        if grep -q -E "FAIL_COMPILE|FAIL_RUN|FAIL_LOGIC" "$log_file"; then
            echo -e "${YELLOW}Logs for Group $prefix:${NC}"
            cat "$log_file"
            echo "--------------------------------------------------"
        fi
    done
    exit 1
fi

echo -e "${GREEN}All active tests compiled and ran successfully!${NC}"
exit 0
