#!/bin/bash

# Color definitions
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

echo -e "${BLUE}=== Bjolang Optimized Parallel Test Runner ===${NC}"
echo ""

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

# --- Fixture libraries -------------------------------------------------------
#
# `TestFiles/inc` holds the modules the "across a dll" tests link against. The
# group runner never sees them — they carry no numeric prefix — so without this
# nothing builds them, and the tests that import one fail with a missing module
# class rather than anything that names the cause.
#
# They are rebuilt every run rather than reused, because a stale `.dll` is worse
# than a missing one: a compiled module records the *absolute paths* of its own
# dependencies, so one built in another directory links against files that are
# no longer there. That is exactly what a move of the repository leaves behind.
#
# Which files are modules is read off the source: one with an `(export ...)` is
# a module and gets compiled, and the rest are include fragments, spliced into
# whoever includes them and not modules at all. Build order follows the
# `(import "...")` edges between them, so a new fixture needs no edit here.
INC_DIR="TestFiles/inc"

build_fixture_libs() {
    local pending=()
    local f

    for f in "$INC_DIR"/*.bjo; do
        [ -f "$f" ] || continue
        if grep -qE '^[[:space:]]*\(export' "$f"; then
            pending+=("$f")
            rm -f "${f%.bjo}.dll"
        fi
    done

    [ ${#pending[@]} -eq 0 ] && return 0

    local progress=1
    while [ ${#pending[@]} -gt 0 ] && [ $progress -eq 1 ]; do
        progress=0
        local remaining=()

        for f in "${pending[@]}"; do
            # Ready once every sibling module it imports has been built.
            local ready=1
            local dep
            while read -r dep; do
                [ -z "$dep" ] && continue
                if [ -f "$INC_DIR/$dep" ] && [ ! -f "$INC_DIR/${dep%.bjo}.dll" ]; then
                    ready=0
                fi
            done < <(grep -oE '\(import[[:space:]]+"[^"]+\.bjo"' "$f" \
                     | grep -oE '"[^"]+"' | tr -d '"' | xargs -r -n1 basename)

            if [ $ready -eq 1 ]; then
                if ! dotnet "$COMPILER_DLL" --lib "$f" > "$LOG_DIR/inc.log" 2>&1; then
                    echo -e "${RED}Failed to build fixture library $f${NC}"
                    cat "$LOG_DIR/inc.log"
                    exit 1
                fi
                progress=1
            else
                remaining+=("$f")
            fi
        done

        pending=("${remaining[@]}")
    done

    if [ ${#pending[@]} -gt 0 ]; then
        echo -e "${RED}Could not resolve a build order for: ${pending[*]}${NC}"
        echo -e "${RED}A cycle, or an import naming a file that is not there.${NC}"
        exit 1
    fi
}

echo -e "${BLUE}Building fixture libraries in $INC_DIR...${NC}"
build_fixture_libs
echo -e "${GREEN}Fixture libraries built.${NC}"
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

# Find all unique 2-digit prefixes in TestFiles/
prefixes=$(ls TestFiles/[0-9][0-9]_*.bjo 2>/dev/null | xargs -n1 basename | cut -c1-2 | sort -u)

if [ -z "$prefixes" ]; then
    echo -e "${RED}No test files matching TestFiles/[0-9][0-9]_*.bjo found.${NC}"
    exit 1
fi

MAX_JOBS=$(nproc 2>/dev/null || echo 8)
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
echo -e "Total time:         ${duration}s"
echo ""

if [ ${#error_failures[@]} -ne 0 ]; then
    echo -e "${RED}=== Error Test Failures ===${NC}"
    for failure in "${error_failures[@]}"; do
        echo -e "  $failure"
    done
    echo ""
fi

# Print failure details
if [ $error_failed -ne 0 ] && [ ${#compiled_failed[@]} -eq 0 ] && [ ${#run_failed[@]} -eq 0 ]; then
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
