#!/usr/bin/env python3

import os
import sys
import json
import time
import fcntl
import shutil
import subprocess
import glob
import re
import hashlib
import itertools
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

# Fix dotnet first-time use experience in read-only sandbox
os.environ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
os.environ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
os.environ["DOTNET_NOLOGO"] = "1"
os.environ["DOTNET_CLI_HOME"] = "/tmp"

# Color definitions
RED = '\033[0;31m'
GREEN = '\033[0;32m'
YELLOW = '\033[0;33m'
BLUE = '\033[0;34m'
NC = '\033[0m' # No Color

def print_color(color, text):
    print(f"{color}{text}{NC}")

print_color(BLUE, "=== Bjolang Optimized Parallel Test Runner (Python) ===")
print("")

MAX_JOBS = min(32, (os.cpu_count() or 8))

# Manage Lock
lock_fd = None
try:
    lock_fd = os.open(".test-lock", os.O_CREAT | os.O_RDWR)
    try:
        fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        # On stderr, and flushed: stdout is block-buffered when it is redirected
        # to a file, so this message would sit in the buffer for as long as the
        # wait lasts — and a run blocked on the lock is then indistinguishable
        # from a run that has hung. Anything still holding the lock counts,
        # including a REPL left open in this working tree.
        print("Another test runner is using this working tree. Waiting for it...",
              file=sys.stderr, flush=True)
        fcntl.flock(lock_fd, fcntl.LOCK_EX)
except Exception as e:
    print_color(RED, f"Failed to acquire lock: {e}")
    sys.exit(1)

# Ensure dotnet subprocesses don't inherit the lock fd
os.set_inheritable(lock_fd, False)

COMPILER_DLL = Path("bin/Release/net10.0/Bjolang.dll")

def get_compiler_source_mtime():
    max_mtime = 0
    patterns = [
        Path('.').glob('*.fs'),
        Path('.').glob('*.fsi'),
        Path('.').glob('*.fsproj'),
        Path('BjolangRuntime').rglob('*.cs'),
        Path('BjolangRuntime').rglob('*.csproj')
    ]
    for pattern in patterns:
        for p in pattern:
            max_mtime = max(max_mtime, p.stat().st_mtime)
    return max_mtime

# 1. Build the compiler once in Release mode
print_color(BLUE, "Checking if compiler needs rebuilding...")
needs_build = True
if COMPILER_DLL.exists():
    dll_mtime = COMPILER_DLL.stat().st_mtime
    src_mtime = get_compiler_source_mtime()
    if dll_mtime > src_mtime:
        needs_build = False

if needs_build:
    print_color(BLUE, "Building compiler in Release mode...")
    result = subprocess.run(["dotnet", "build", "-c", "Release"], capture_output=True, text=True)
    if result.returncode != 0:
        print_color(RED, "Compiler build failed!")
        print(result.stdout)
        print(result.stderr)
        sys.exit(1)
    print_color(GREEN, "Compiler build succeeded.\n")
else:
    print_color(GREEN, "Compiler is up to date, skipping build.\n")

if not COMPILER_DLL.exists():
    print_color(RED, f"Could not find compiler binary at {COMPILER_DLL}")
    sys.exit(1)

# Create a temporary directory for logs in the workspace
LOG_DIR = Path("TestFiles/.test-logs")
if LOG_DIR.exists():
    shutil.rmtree(LOG_DIR)
LOG_DIR.mkdir(parents=True)


# --- Batchkompilering ---------------------------------------------------
#
# En kall kompilatorprocess kostar omkring en halv sekund innan den har gjort
# någonting alls — värdstart, JIT av frontenden, reflektion över körtidens
# assemblies — och omkring 70 ms per modul därefter. Sviten har närmare 400
# rotfiler att kompilera, så en process per fil betalar det första talet 400
# gånger.
#
# `--batch` lämnar över många filer till en process, som isolerar dem från
# varandra med `Session.isolated` och svarar med en JSON-rad per fil. Det som
# behövs här är alltså inte längre en kompilator per fil utan en per klunga.

_batch_seq = itertools.count()

def chunk(items, n):
    """Delar upp `items` i högst `n` listor, varvat så att de blir jämnstora."""
    buckets = [[] for _ in range(n)]
    for i, item in enumerate(items):
        buckets[i % n].append(item)
    return [b for b in buckets if b]

def compile_one(bjo_file, lib=False, debug=False):
    """Kompilerar en ensam fil, i formen ett batchsvar har.

    Reserv för filer som en batch inte hann svara om — en kompilator som dör
    mitt i tar med sig resten av sin klunga, och ett testfall som aldrig
    kördes ska inte kunna se ut som ett som gick igenom.
    """
    path = Path(bjo_file)
    cmd = ["dotnet", str(COMPILER_DLL)]
    if lib:
        cmd.append("--lib")
    if debug:
        # Samma placering som batchen väljer åt sig själv, så att den som
        # läser den genererade C#-koden hittar den på ett ställe.
        cmd += ["--debug", "--emit-cs", str(path.with_suffix(".out.cs"))]
    cmd.append(str(path))

    res = subprocess.run(cmd, capture_output=True, text=True)

    artifact = ""
    for ext in (".exe", ".dll"):
        candidate = path.with_suffix(ext)
        if candidate.exists():
            artifact = str(candidate.resolve())
            break

    return {"file": str(path.resolve()), "status": res.returncode,
            "output": res.stdout + res.stderr, "artifact": artifact}

def batch_compile(files, lib=False, debug=False, if_stale=False, tag="batch"):
    """Kompilerar `files` i en kompilatorprocess.

    Ger en tabell från absolut sökväg till batchsvaret, så att den som frågade
    kan slå upp vilken fil som helst av dem den bad om oavsett hur den stavade
    sökvägen.

    Filerna går via `--files-from` snarare än som argument: listorna blir
    hundratals poster långa, och en fil på disk håller sig utanför skalets
    citeringsregler och argumentlängdsgräns.
    """
    files = [str(f) for f in files]
    if not files:
        return {}

    n = next(_batch_seq)
    list_file = LOG_DIR / f"{tag}_{n}.files"
    report_file = LOG_DIR / f"{tag}_{n}.report"
    list_file.write_text("\n".join(files) + "\n")

    cmd = ["dotnet", str(COMPILER_DLL), "--batch",
           "--files-from", str(list_file), "--report", str(report_file)]
    if lib:
        cmd.append("--lib")
    if debug:
        cmd.append("--debug")
    if if_stale:
        cmd.append("--if-stale")

    proc = subprocess.run(cmd, capture_output=True, text=True)

    results = {}
    if report_file.exists():
        for line in report_file.read_text().splitlines():
            if line.strip():
                entry = json.loads(line)
                results[entry["file"]] = entry

    # En batch som dog innan sista filen lämnar ett kortare svar än frågan.
    # Resten kompileras var för sig hellre än att rapporteras som något —
    # långsamt, men bara i det läget, och det är ett läge som annars göms.
    missing = [f for f in files if str(Path(f).resolve()) not in results]
    if missing:
        print_color(YELLOW,
                    f"  Batch '{tag}' answered for {len(results)}/{len(files)} files "
                    f"(exit {proc.returncode}); recompiling {len(missing)} individually.")
        for f in missing:
            results[str(Path(f).resolve())] = compile_one(f, lib=lib, debug=debug)

    return results

def batch_compile_parallel(chunks, lib=False, debug=False, if_stale=False, tag="batch"):
    """Kör en batch per klunga, klungorna samtidigt."""
    results = {}
    with ThreadPoolExecutor(max_workers=MAX_JOBS) as executor:
        futures = [executor.submit(batch_compile, c, lib=lib, debug=debug,
                                   if_stale=if_stale, tag=tag)
                   for c in chunks]
        for future in as_completed(futures):
            results.update(future.result())
    return results

def result_for(results, bjo_file):
    return results[str(Path(bjo_file).resolve())]

def remove_artifacts(bjo_file, extra=()):
    """Raderar det en kompilering av `bjo_file` lämnar efter sig."""
    path = Path(bjo_file)
    for suffix in (".exe", ".dll", ".runtimeconfig.json", ".deps.json", ".pdb") + tuple(extra):
        path.with_suffix(suffix).unlink(missing_ok=True)


# Standard library
STD_DIR = Path("lib/std")
print_color(BLUE, "Checking the standard library...")
with open(LOG_DIR / "std.log", "w") as std_log:
    result = subprocess.run(["./build_std.sh"], stdout=std_log, stderr=subprocess.STDOUT)

if result.returncode != 0:
    print_color(RED, "Standard library build failed!")
    with open(LOG_DIR / "std.log") as f:
        print(f.read())
    sys.exit(1)

with open(LOG_DIR / "std.log") as f:
    std_out = f.read()
std_built = std_out.count("Built library:")
std_current = std_out.count("Up to date:")
print_color(GREEN, f"Standard library: {std_built} built, {std_current} already current.")

# --- Fixture libraries ---
#
# `--if-stale` ställer samma fråga som en import gör: finns det en aktuell
# `.dll`? Kompilatorn svarar på den redan, och svarar också på följdfrågan om
# vilken ordning de ska byggas i — den som är ett beroende till en annan byggs
# när den nås. Det som stod här förut var en egen kopia av båda: en
# regexavläsning av importerna och en vågschemaläggare ovanpå den.
print("")
INC_DIR = Path("TestFiles/inc")
FIXTURE_STAMP = INC_DIR / ".built-from"
fixtures_built = 0
fixtures_current = 0

def fixture_modules():
    """De filer i `inc` som är moduler snarare än inkluderade fragment."""
    modules = []
    for f in sorted(INC_DIR.glob("*.bjo")):
        content = f.read_text()
        if re.search(r'^[ \t]*\((export|def/macro)', content, re.MULTILINE):
            modules.append(f)
    return modules

def mtime(path):
    return path.stat().st_mtime if path.exists() else 0

def fixtures_possibly_stale(modules, moved):
    """Är det värt att starta en kompilator för att fråga ordentligt?

    Grovt med flit. Varje `.bjo` i katalogen räknas som indata till varenda
    fixtur, så en ändrad inkluderad fil väcker hela uppsättningen — det är
    kompilatorn som vet vilken av dem det faktiskt gällde, och den frågan är
    inte värd en process om ingenting alls har rört sig.
    """
    if moved:
        return True

    newest_input = max(
        [mtime(COMPILER_DLL)]
        + [mtime(f) for f in INC_DIR.glob("*.bjo")]
        + [mtime(d) for d in STD_DIR.glob("*.dll")]
    )

    for f in modules:
        dll = f.with_suffix(".dll")
        if not dll.exists() or newest_input > mtime(dll):
            return True

    return False

def build_fixture_libs():
    global fixtures_built, fixtures_current

    modules = fixture_modules()
    if not modules:
        return

    # Sökvägarna en modul byggdes mot står i dess `.dll`. Ett flyttat träd gör
    # dem alla fel på en gång, och ingen tidsstämpel säger det.
    try:
        moved = FIXTURE_STAMP.read_text().strip() != os.getcwd()
    except FileNotFoundError:
        moved = True

    if moved:
        for f in modules:
            remove_artifacts(f)

    if not fixtures_possibly_stale(modules, moved):
        fixtures_current = len(modules)
        return

    before = {f: mtime(f.with_suffix(".dll")) for f in modules}
    results = batch_compile(modules, if_stale=True, tag="inc")

    for f in modules:
        entry = result_for(results, f)
        if entry["status"] != 0:
            print_color(RED, f"Failed to build fixture library {f}")
            print(entry["output"])
            sys.exit(1)

    for f in modules:
        if mtime(f.with_suffix(".dll")) != before[f]:
            fixtures_built += 1
        else:
            fixtures_current += 1

    FIXTURE_STAMP.write_text(os.getcwd())

print_color(BLUE, f"Building fixture libraries in {INC_DIR}...")
build_fixture_libs()
print_color(GREEN, f"Fixture libraries: {fixtures_built} built, {fixtures_current} already current.")
print("")


# --- Testfilerna ---
#
# Grupperade på trebokstavsprefix som förut, eftersom filer med samma prefix
# hör ihop och ska köras i ordning. Kompileringen sker däremot inte längre per
# grupp utan per klunga av grupper, i en batch var.

bjo_files = sorted(glob.glob("TestFiles/[0-9][0-9][0-9]_*.bjo"))
groups = {}
for f in bjo_files:
    groups.setdefault(os.path.basename(f)[:3], []).append(f)
prefixes = sorted(groups)

if not prefixes:
    print_color(RED, "No test files matching TestFiles/[0-9][0-9][0-9]_*.bjo found.")
    sys.exit(1)

def run_prefix_group(prefix, log_file, compiled):
    """Kör en grupps redan kompilerade program, i filordning."""
    for bjo_file in groups[prefix]:
        basename = os.path.basename(bjo_file)
        bjo_path = Path(bjo_file)
        exe_file = bjo_path.with_suffix(".exe")
        entry = result_for(compiled, bjo_file)

        with open(log_file, "a") as out:
            out.write(f"=== Compiling {basename} ===\n")
            out.write(entry["output"])

        if entry["status"] != 0:
            with open(log_file, "a") as out:
                out.write(f"FAIL_COMPILE: {basename}\n")
            return 1

        # Ingen `.exe` betyder att filen saknar `main` och byggdes som
        # bibliotek. Den har inget att köra, och det är ett godkänt utfall.
        if not exe_file.exists():
            with open(log_file, "a") as out:
                out.write(f"PASS_LIB: {basename}\n")
            continue

        with open(log_file, "a") as out:
            out.write(f"=== Running {basename} ===\n")

        input_file = bjo_path.with_suffix(".in")
        run_output_file = Path(f"{log_file}.run")
        if run_output_file.exists():
            run_output_file.unlink()

        with open(run_output_file, "w") as out:
            stdin = None
            if input_file.exists():
                stdin = open(input_file, "r")
            try:
                res = subprocess.run(["dotnet", str(exe_file)], stdin=stdin, stdout=out, stderr=subprocess.STDOUT)
            finally:
                if stdin:
                    stdin.close()

        with open(run_output_file, "r") as run_out:
            run_content = run_out.read()

        with open(log_file, "a") as out:
            out.write(run_content)

        if res.returncode != 0:
            with open(log_file, "a") as out:
                out.write(f"FAIL_RUN: {basename}\n")
            run_output_file.unlink()
            return 2

        if "FAILURE:" in run_content:
            with open(log_file, "a") as out:
                out.write(f"FAIL_LOGIC: {basename}\n")
            run_output_file.unlink()
            return 3

        run_output_file.unlink()
        with open(log_file, "a") as out:
            out.write(f"PASS: {basename}\n")

    return 0

print_color(BLUE, f"Running tests in parallel (max {MAX_JOBS} concurrent jobs)...")
print("-" * 50)

start_time = time.time()

# Allt raderas före batchen, inte i den: en fil vars `.exe` ligger kvar från
# förra körningen skulle annars kunna se ut som byggd av den här.
for f in bjo_files:
    remove_artifacts(f)

# En klunga är hela grupper, så att en grupps filer kompileras i ordning och i
# samma process — det är där en `_lib.bjo` byggs som den efterföljande filen
# importerar.
prefix_chunks = chunk(prefixes, MAX_JOBS)
file_chunks = [[f for prefix in pc for f in groups[prefix]] for pc in prefix_chunks]
compiled = batch_compile_parallel(file_chunks, tag="tests")

success_count = 0
fail_compile_count = 0
fail_run_count = 0
skipped_count = 0

compiled_failed = []
run_failed = []
skipped_list = []

group_futures = {}
with ThreadPoolExecutor(max_workers=MAX_JOBS) as executor:
    for prefix in prefixes:
        log_file = LOG_DIR / f"{prefix}.log"
        if log_file.exists():
            log_file.unlink()
        group_futures[executor.submit(run_prefix_group, prefix, log_file, compiled)] = prefix

    for future in as_completed(group_futures):
        prefix = group_futures[future]
        status = future.result()
        log_file = LOG_DIR / f"{prefix}.log"

        files_in_group = " ".join([os.path.basename(f) for f in groups[prefix]])

        if log_file.exists():
            with open(log_file, "r") as log:
                content = log.read()
        else:
            content = ""

        if status == 0:
            if "PASS" in content or "PASS_LIB" in content:
                print(f"  [{GREEN}PASS{NC}] Group {prefix}: {files_in_group}")
                success_count += 1
            elif "SKIP" in content:
                print(f"  [{YELLOW}SKIP{NC}] Group {prefix}: {files_in_group}")
                skipped_count += 1
                skipped_list.append(files_in_group)
            else:
                print(f"  [{GREEN}PASS{NC}] Group {prefix}: {files_in_group}")
                success_count += 1
        else:
            print(f"  [{RED}FAIL{NC}] Group {prefix}: {files_in_group}")
            if "FAIL_COMPILE" in content:
                fail_compile_count += 1
                match = re.search(r"FAIL_COMPILE:\s+(\S+)", content)
                if match:
                    compiled_failed.append(match.group(1))
            elif "FAIL_RUN" in content:
                fail_run_count += 1
                match = re.search(r"FAIL_RUN:\s+(\S+)", content)
                if match:
                    run_failed.append(match.group(1))
            else:
                fail_run_count += 1
                match = re.search(r"FAIL_LOGIC:\s+(\S+)", content)
                if match:
                    run_failed.append(f"{match.group(1)} (logic failure: contains 'FAILURE:')")

# --- Error tests ---
ERROR_DIR = Path("TestFiles/errors")
error_total = 0
error_failed = 0
error_failures = []

error_files = sorted(ERROR_DIR.glob("*.bjo")) if ERROR_DIR.exists() else []
if error_files:
    print("-" * 50)
    print_color(BLUE, "Running error tests (must be rejected)...")

    rejected = batch_compile_parallel(chunk(error_files, MAX_JOBS), tag="errors")

    def judge_error_test(bjo_file, entry):
        if entry["status"] == 0:
            return "FAIL", "compiled successfully, but was expected to be rejected"

        # `EXPECT-ERROR` säger inte bara att filen ska avvisas utan varför.
        # En fil som avvisas av något annat skäl än det angivna är ett
        # felmeddelande som slutat gälla, inte ett test som går igenom.
        for line in bjo_file.read_text().splitlines():
            match = re.match(r'^\s*;;\s*EXPECT-ERROR:\s*(.*)', line)
            if match:
                expected = match.group(1).strip()
                if expected and expected not in entry["output"]:
                    return "FAIL", f"rejected, but not for the stated reason. Expected to find: {expected}"

        return "PASS", ""

    for bjo_file in error_files:
        entry = result_for(rejected, bjo_file)
        remove_artifacts(bjo_file)

        verdict, reason = judge_error_test(bjo_file, entry)
        error_total += 1
        if verdict == "PASS":
            print(f"  [{GREEN}PASS{NC}] {bjo_file.name}")
        else:
            print(f"  [{RED}FAIL{NC}] {bjo_file.name}")
            error_failed += 1
            error_failures.append(f"{bjo_file.name}: {reason}")

# --- Warning tests ---
WARNING_DIR = Path("TestFiles/warnings")
warning_total = 0
warning_failed = 0
warning_failures = []

warning_files = sorted(WARNING_DIR.glob("*.bjo")) if WARNING_DIR.exists() else []
if warning_files:
    print("-" * 50)
    print_color(BLUE, "Running warning tests (must compile, and say so)...")

    warned = batch_compile(warning_files, tag="warnings")

    def judge_warning_test(bjo_file, entry):
        if entry["status"] != 0:
            return "FAIL", "was expected to compile, and did not"

        for line in bjo_file.read_text().splitlines():
            match = re.match(r'^\s*;;\s*EXPECT-WARNING:\s*(.*)', line)
            if match:
                expected = match.group(1).strip()
                if expected and expected not in entry["output"]:
                    return "FAIL", f"compiled, but said nothing about it. Expected to find: {expected}"

            match = re.match(r'^\s*;;\s*EXPECT-NO-WARNING:\s*(.*)', line)
            if match:
                unwanted = match.group(1).strip()
                if unwanted and unwanted in entry["output"]:
                    return "FAIL", f"warned where it should have kept quiet: {unwanted}"

        return "PASS", ""

    for bjo_file in warning_files:
        entry = result_for(warned, bjo_file)
        remove_artifacts(bjo_file)

        verdict, reason = judge_warning_test(bjo_file, entry)
        warning_total += 1
        if verdict == "PASS":
            print(f"  [{GREEN}PASS{NC}] {bjo_file.name}")
        else:
            print(f"  [{RED}FAIL{NC}] {bjo_file.name}")
            warning_failed += 1
            warning_failures.append(f"{bjo_file.name}: {reason}")

# --- Phase Runners ---
def run_codegen_tests():
    """Läser den genererade C#-koden.

    `--debug` lägger den bredvid indatafilen som `<namn>.out.cs`, en per fil,
    vilket är vad som gör att de kan byggas i en batch: förut skrev varje
    kompilering `out.cs` i arbetskatalogen, så de fick köras en i taget i var
    sin katalog.
    """
    CODEGEN_DIR = Path("TestFiles/codegen")
    c_total, c_failed = 0, 0
    c_failures = []

    files = sorted(CODEGEN_DIR.glob("*.bjo")) if CODEGEN_DIR.exists() else []
    if not files:
        return "codegen", c_total, c_failed, c_failures

    emitted = batch_compile(files, lib=True, debug=True, tag="codegen")

    for bjo_file in files:
        cs_name = bjo_file.stem
        c_total += 1
        entry = result_for(emitted, bjo_file)

        out_cs = bjo_file.with_suffix(".out.cs")
        out_cs_content = out_cs.read_text() if out_cs.exists() else ""
        remove_artifacts(bjo_file, extra=(".out.cs", ".out.ast.txt"))

        if entry["status"] != 0:
            c_failed += 1
            c_failures.append(f"{cs_name}.bjo: did not compile")
            continue

        cs_missing = None
        cs_present = None

        for line in bjo_file.read_text().splitlines():
            match1 = re.match(r'^\s*;;\s*EXPECT-CS:\s*(.*)', line)
            if match1:
                pattern = match1.group(1).strip()
                if pattern and not re.search(pattern, out_cs_content, re.MULTILINE):
                    cs_missing = pattern
                    break
            match2 = re.match(r'^\s*;;\s*EXPECT-NO-CS:\s*(.*)', line)
            if match2:
                pattern = match2.group(1).strip()
                if pattern and re.search(pattern, out_cs_content, re.MULTILINE):
                    cs_present = pattern
                    break

        if cs_missing:
            c_failed += 1
            c_failures.append(f"{cs_name}.bjo: the generated C# has no match for: {cs_missing}")
        elif cs_present:
            c_failed += 1
            c_failures.append(f"{cs_name}.bjo: the generated C# matches what it must not: {cs_present}")

    return "codegen", c_total, c_failed, c_failures

def run_repl_tests():
    REPL_DIR = Path("TestFiles/repl")
    r_total, r_failed = 0, 0
    r_failures = []
    
    files = list(REPL_DIR.glob("*.in")) if REPL_DIR.exists() else []
    if files:
        for in_file in files:
            repl_name = in_file.stem
            r_total += 1
            expected = in_file.with_suffix(".expected")
            
            res = subprocess.run(["dotnet", COMPILER_DLL, "--repl"], stdin=open(in_file), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
            
            # Clean output
            cleaned_lines = []
            for line in res.stdout.splitlines():
                line = re.sub(r'^(bjo> |\.\.\.> )*', '', line)
                if not re.match(r'^(Building imported module|$)', line):
                    cleaned_lines.append(line)
            out_text = "\n".join(cleaned_lines) + ("\n" if cleaned_lines else "")
            
            if not expected.exists():
                r_failed += 1
                r_failures.append(f"{repl_name}: no recorded transcript")
            else:
                expected_text = expected.read_text()
                if out_text != expected_text:
                    r_failed += 1
                    r_failures.append(f"{repl_name}: the session no longer matches its transcript")
                    diff_file = LOG_DIR / f"repl_{repl_name}.diff"
                    with open(diff_file, "w") as df:
                        df.write("EXPECTED:\n" + expected_text + "\nGOT:\n" + out_text)
                    
    return "repl", r_total, r_failed, r_failures

def run_reproducibility():
    # Enstaka kompileringar med flit. Det som mäts är att samma källa ger samma
    # byte, och batchläget emitterar med en annan C#-backend än en ensam
    # kompilering gör — att jämföra över den gränsen vore att mäta något annat.
    REPRO_MAIN = Path("TestFiles/006_modules_and_input.bjo")
    REPRO_DEP = Path("TestFiles/006_lib")
    rp_failed = 0
    rp_failures = []
    
    if not REPRO_MAIN.exists():
        return "repro", 0, 0, []
        
    def repro_build(env_var, val):
        for ext in (".dll", ".pdb"):
            REPRO_DEP.with_suffix(ext).unlink(missing_ok=True)
        for ext in (".exe", ".dll", ".runtimeconfig.json", ".deps.json", ".pdb"):
            REPRO_MAIN.with_suffix(ext).unlink(missing_ok=True)
            
        env = os.environ.copy()
        env[env_var] = val
        res = subprocess.run(["dotnet", COMPILER_DLL, str(REPRO_MAIN)], env=env, capture_output=True, text=True)
        if res.returncode != 0:
            return None
            
        md5s = []
        for f in (REPRO_DEP.with_suffix(".dll"), REPRO_MAIN.with_suffix(".exe")):
            if f.exists():
                with open(f, "rb") as file:
                    md5s.append(hashlib.md5(file.read()).hexdigest())
            else:
                md5s.append("MISSING")
        return " ".join(md5s)

    repro_a = repro_build("BJOLANG_X", "1")
    repro_b = repro_build("BJOLANG_X", "1")
    repro_c = repro_build("BJOLANG_OUT_OF_PROCESS_DEPS", "1")
    
    if repro_a and repro_b and repro_c:
        if repro_a != repro_b:
            rp_failed = 1
            rp_failures.append(f"two identical builds differed: {repro_a} vs {repro_b}")
        if repro_a != repro_c:
            rp_failed = 1
            rp_failures.append(f"in-process {repro_a} vs out-of-process {repro_c} — compilation state has leaked")
    else:
        rp_failed = 1
        rp_failures.append("a reproducibility build did not compile")
        
    for ext in (".dll", ".pdb"):
        REPRO_DEP.with_suffix(ext).unlink(missing_ok=True)
    for ext in (".exe", ".dll", ".runtimeconfig.json", ".deps.json", ".pdb"):
        REPRO_MAIN.with_suffix(ext).unlink(missing_ok=True)
        
    return "repro", 0, rp_failed, rp_failures

def run_staleness():
    STALE_DIR = LOG_DIR / "staleness"
    s_total, s_failed = 0, 0
    s_failures = []
    
    STALE_DIR.mkdir(exist_ok=True)
    
    (STALE_DIR / "leaf.bjo").write_text("(export flavour)\n(import (std prelude))\n(: flavour (-> string))\n(defun (flavour) \"banana\")\n")
    (STALE_DIR / "middle.bjo").write_text("(export describe)\n(import (std prelude))\n(import \"leaf.bjo\")\n(: describe (-> string))\n(defun (describe) (string-append \"a \" (flavour)))\n")
    (STALE_DIR / "app.bjo").write_text("(import (std prelude))\n(import \"middle.bjo\")\n(defun (main args) (println (describe)) 0)\n")
    
    def stale_check(label, wanted, got):
        nonlocal s_total, s_failed, s_failures
        s_total += 1
        if got == wanted:
            pass # OK
        else:
            s_failed += 1
            s_failures.append(f"{label}: got '{got}', wanted '{wanted}'")

    res = subprocess.run(["dotnet", COMPILER_DLL, str(STALE_DIR / "app.bjo")], capture_output=True, text=True)
    if res.returncode == 0:
        app_res = subprocess.run(["dotnet", str(STALE_DIR / "app.exe")], capture_output=True, text=True)
        stale_check("a chain of three modules builds", "a banana\n", app_res.stdout)
        
        leaf = (STALE_DIR / "leaf.bjo")
        leaf.write_text(leaf.read_text().replace('"banana"', '"cloudberry"'))
        
        res2 = subprocess.run(["dotnet", COMPILER_DLL, str(STALE_DIR / "app.bjo")], capture_output=True, text=True)
        if res2.returncode == 0:
            app_res2 = subprocess.run(["dotnet", str(STALE_DIR / "app.exe")], capture_output=True, text=True)
            stale_check("an edit two modules down reaches the program", "a cloudberry\n", app_res2.stdout)
        else:
            stale_check("an edit two modules down reaches the program", "a cloudberry\n", "it did not compile")
            
        res3 = subprocess.run(["dotnet", COMPILER_DLL, str(STALE_DIR / "app.bjo")], capture_output=True, text=True)
        stale_check("a build with nothing changed rebuilds nothing", 0, res3.stdout.count("Building imported module"))
    else:
        stale_check("a chain of three modules builds", "a banana\n", "it did not compile")

    return "staleness", s_total, s_failed, s_failures

# Run phases concurrently
phases = []
with ThreadPoolExecutor(max_workers=4) as executor:
    if list(Path("TestFiles/codegen").glob("*.bjo")) if Path("TestFiles/codegen").exists() else []:
        print("-" * 50)
        print_color(BLUE, "Checking the generated C#...")
        phases.append(executor.submit(run_codegen_tests))
        
    if list(Path("TestFiles/repl").glob("*.in")) if Path("TestFiles/repl").exists() else []:
        print("-" * 50)
        print_color(BLUE, "Running REPL sessions...")
        phases.append(executor.submit(run_repl_tests))
        
    print("-" * 50)
    print_color(BLUE, "Checking that a build reproduces...")
    phases.append(executor.submit(run_reproducibility))
    
    print("-" * 50)
    print_color(BLUE, "Checking that a changed dependency is rebuilt...")
    phases.append(executor.submit(run_staleness))
    
    codegen_total = codegen_failed = 0
    repl_total = repl_failed = 0
    repro_total = repro_failed = 0
    stale_total = stale_failed = 0
    codegen_failures, repl_failures, repro_failures, stale_failures = [], [], [], []
    
    for future in as_completed(phases):
        name, total, failed, failures = future.result()
        if name == "codegen":
            codegen_total, codegen_failed, codegen_failures = total, failed, failures
            for bjo_file in list(Path("TestFiles/codegen").glob("*.bjo")) if Path("TestFiles/codegen").exists() else []:
                cs_name = bjo_file.stem
                if any(cs_name in f for f in codegen_failures):
                    print(f"  [{RED}FAIL{NC}] {cs_name}.bjo")
                else:
                    print(f"  [{GREEN}PASS{NC}] {cs_name}.bjo")
        elif name == "repl":
            repl_total, repl_failed, repl_failures = total, failed, failures
            for in_file in list(Path("TestFiles/repl").glob("*.in")) if Path("TestFiles/repl").exists() else []:
                repl_name = in_file.stem
                if any(repl_name in f for f in repl_failures):
                    print(f"  [{RED}FAIL{NC}] {repl_name}")
                else:
                    print(f"  [{GREEN}PASS{NC}] {repl_name}")
        elif name == "repro":
            repro_total, repro_failed, repro_failures = total, failed, failures
            if failed == 0:
                print(f"  [{GREEN}PASS{NC}] reproducibility checks")
            else:
                for f in repro_failures:
                    print(f"  [{RED}FAIL{NC}] {f}")
        elif name == "staleness":
            stale_total, stale_failed, stale_failures = total, failed, failures
            if failed == 0:
                print(f"  [{GREEN}PASS{NC}] staleness checks")
            else:
                for f in stale_failures:
                    print(f"  [{RED}FAIL{NC}] {f}")

end_time = time.time()
duration = end_time - start_time

print("-" * 50)
print("")
print_color(BLUE, "=== Summary ===")
print(f"Total groups:       {len(prefixes)}")
print(f"Skipped:            {skipped_count}")
print(f"Compile failures:   {fail_compile_count}")
print(f"Execution failures: {fail_run_count}")
print(f"Successful runs:    {success_count}")
if error_total > 0:
    print(f"Error tests:        {error_total - error_failed}/{error_total} rejected as expected")
if warning_total > 0:
    print(f"Warning tests:      {warning_total - warning_failed}/{warning_total} warned as expected")
if codegen_total > 0:
    print(f"Codegen tests:      {codegen_total - codegen_failed}/{codegen_total} emitted as expected")
if repl_total > 0:
    print(f"REPL sessions:      {repl_total - repl_failed}/{repl_total} match their transcript")
if stale_total > 0:
    print(f"Staleness:          {stale_total - stale_failed}/{stale_total} rebuilt as expected")
print(f"Total time:         {duration:.2f}s")
print("")

def print_failures(title, failures):
    if failures:
        print_color(RED, f"=== {title} ===")
        for failure in failures:
            print(f"  {failure}")
        print("")

print_failures("Error Test Failures", error_failures)
print_failures("Warning Test Failures", warning_failures)
print_failures("REPL Session Failures", repl_failures)
print_failures("Reproducibility Failures", repro_failures)
print_failures("Staleness Failures", stale_failures)
print_failures("Codegen Test Failures", codegen_failures)

if (error_failed or codegen_failed or repl_failed or repro_failed or stale_failed or warning_failed) and not compiled_failed and not run_failed:
    sys.exit(1)
    
if compiled_failed or run_failed:
    print_color(RED, "=== Failure Details ===")
    for prefix in prefixes:
        log_file = LOG_DIR / f"{prefix}.log"
        if log_file.exists():
            content = log_file.read_text()
            if "FAIL_COMPILE" in content or "FAIL_RUN" in content or "FAIL_LOGIC" in content:
                print_color(YELLOW, f"Logs for Group {prefix}:")
                print(content)
                print("-" * 50)
    sys.exit(1)

print_color(GREEN, "All active tests compiled and ran successfully!")
sys.exit(0)
