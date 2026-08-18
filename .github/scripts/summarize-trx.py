#!/usr/bin/env python3
"""summarize-trx.py — turn dotnet test .trx files into CI-visible output.

Job logs need repository admin rights to download, so a bare "exit code 1" tells
nobody which test broke. This prints a per-suite table, writes it to the job
summary, and emits one ::error:: annotation per failed test (annotations are
readable from the run page and the public checks API).

Usage: summarize-trx.py <results-dir>
Always exits 0 — the dotnet test step owns the job's pass/fail verdict.
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET


def local(tag):
    return tag.rsplit('}', 1)[-1]


def main():
    results_dir = sys.argv[1] if len(sys.argv) > 1 else "Logs/test-results"
    paths = sorted(glob.glob(os.path.join(results_dir, "dotnet-*.trx")))
    if not paths:
        print(f"No .trx files under {results_dir}")
        return 0

    rows = []       # (suite, total, passed, failed, skipped)
    failures = []   # (suite, test name, message)

    for path in paths:
        suite = os.path.basename(path)[len("dotnet-"):-len(".trx")]
        try:
            root = ET.parse(path).getroot()
        except Exception as e:
            rows.append((suite, 0, 0, 0, 0))
            failures.append((suite, "<result file unreadable>", str(e)))
            continue

        total = passed = failed = 0
        for el in root.iter():
            if local(el.tag) == "Counters":
                total = int(el.get("total", 0))
                passed = int(el.get("passed", 0))
                failed = int(el.get("failed", 0)) + int(el.get("error", 0))
                break
        rows.append((suite, total, passed, failed, max(total - passed - failed, 0)))

        for el in root.iter():
            if local(el.tag) != "UnitTestResult" or el.get("outcome") != "Failed":
                continue
            msg = ""
            for sub in el.iter():
                if local(sub.tag) == "Message" and sub.text:
                    msg = " ".join(sub.text.split())
                    break
            failures.append((suite, el.get("testName", "?"), msg))

    name_w = max([len(r[0]) for r in rows] + [len("SUITE")])
    print(f"  {'SUITE':<{name_w}}  {'TOTAL':>6}  {'PASS':>6}  {'FAIL':>6}  {'SKIP':>7}")
    for suite, total, passed, failed, skipped in rows:
        print(f"  {suite:<{name_w}}  {total:>6}  {passed:>6}  {failed:>6}  {skipped:>7}")

    # One annotation per failure, so the run page shows the names without log access.
    for suite, name, msg in failures:
        print(f"::error title=Test failed ({suite})::{name} — {msg[:800]}")

    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as fh:
            fh.write("## .NET test results\n\n")
            fh.write("| Suite | Total | Pass | Fail | Skip |\n|---|---:|---:|---:|---:|\n")
            for suite, total, passed, failed, skipped in rows:
                fh.write(f"| {suite} | {total} | {passed} | {failed} | {skipped} |\n")
            if failures:
                fh.write(f"\n### Failed tests ({len(failures)})\n\n")
                for suite, name, msg in failures:
                    fh.write(f"- **[{suite}]** `{name}`\n")
                    if msg:
                        fh.write(f"  - {msg[:500]}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
