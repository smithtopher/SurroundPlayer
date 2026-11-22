# Windows only. Mid-file read errors: locks the second half of the source (a mandatory Windows
# byte-range lock, so the engine's reads fail like a USB drive / network share dropping out),
# runs pcdump, unlocks after N s. Expect: 3 s -> recovered, output = reference minus one parser
# frame; 10 s -> 'error: read error', no early EOF and no gapless join into the next file.
# MSYS2 UCRT64: python3 -I scripts/test_read_errors.py <pcdump.exe as a Windows path> <scratch dir>
import msvcrt, os, subprocess, sys, threading, time, filecmp

pcdump, work = sys.argv[1], sys.argv[2]
os.makedirs(work, exist_ok=True)
a = os.path.join(work, "a.flac"); b = os.path.join(work, "b.flac")
for path, f in ((a, 440), (b, 660)):
    if not os.path.exists(path):
        subprocess.run(["ffmpeg", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i",
                        f"sine=f={f}:r=48000:d=60", "-ac", "2", "-t", "60", path], check=True)

def run(name, lock_s, extra=()):
    out = os.path.join(work, name + ".spdif")
    fh = None
    if lock_s:
        fh = open(a, "rb")
        size = os.path.getsize(a)
        fh.seek(size // 2)
        msvcrt.locking(fh.fileno(), msvcrt.LK_NBLCK, size - size // 2)
        def unlock():
            time.sleep(lock_s)
            fh.seek(size // 2)
            msvcrt.locking(fh.fileno(), msvcrt.LK_UNLCK, size - size // 2)
            fh.close()
        threading.Thread(target=unlock, daemon=True).start()
    t0 = time.time()
    p = subprocess.run([pcdump, a, out, *extra], capture_output=True, text=True, timeout=120)
    el = time.time() - t0
    lines = [l for l in (p.stdout + p.stderr).splitlines() if "read" in l or "error" in l or "next" in l.lower()]
    print(f"--- {name}: lock {lock_s}s, rc={p.returncode}, {el:.1f}s, out {os.path.getsize(out) if os.path.exists(out) else 0} bytes")
    for l in lines: print("   ", l)
    time.sleep(lock_s + 0.5 if lock_s and el < lock_s else 0)
    return out

ref = run("ref", 0)
short = run("lock3", 3)
print("lock3 identical to ref:", filecmp.cmp(ref, short, shallow=False))
long = run("lock10", 10)
print("lock10 shorter than ref:", os.path.getsize(long) < os.path.getsize(ref))
gref = run("gap_ref", 0, ("--then", b))
gshort = run("gap_lock3", 3, ("--then", b))
print("gapless lock3 identical to gapless ref:", filecmp.cmp(gref, gshort, shallow=False))
glong = run("gap_lock10", 10, ("--then", b))
print("gapless lock10 did NOT join b (shorter than a alone):", os.path.getsize(glong) < os.path.getsize(ref))
