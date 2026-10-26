#!/usr/bin/env python3
"""Bump the Windows product version, never just the CI run/build counter."""
from pathlib import Path
import argparse
import re

ROOT = Path(__file__).resolve().parents[1]
TARGETS = (
    ("src/RouteShield/RouteShield.csproj", r"(?<=<Version>)[0-9]+\.[0-9]+\.[0-9]+(?=</Version>)", ""),
    ("src/RouteShield/app.manifest", r'(?<=assemblyIdentity version=")[0-9]+\.[0-9]+\.[0-9]+\.0', ".0"),
    ("src/RouteShield.Core/Subscriptions/SubscriptionService.cs", r"(?<=RouteShield/)[0-9]+\.[0-9]+\.[0-9]+", ""),
    ("src/RouteShield/Services/TunnelTelemetry.cs", r"(?<=RouteShield/)[0-9]+\.[0-9]+\.[0-9]+", ""),
)

def plan(root, part="patch"):
    current = re.search(TARGETS[0][1], (root / TARGETS[0][0]).read_text()).group()
    major, minor, patch = map(int, current.split("."))
    next_version = {"patch": f"{major}.{minor}.{patch + 1}", "minor": f"{major}.{minor + 1}.0", "major": f"{major + 1}.0.0"}[part]
    changes = []
    for relative, pattern, suffix in TARGETS:
        path = root / relative
        old = path.read_text()
        found = re.search(pattern, old)
        if not found or found.group().removesuffix(suffix) != current:
            raise ValueError(f"Version mismatch in {relative}; reconcile before bumping")
        new, count = re.subn(pattern, next_version + suffix, old)
        if count != 1:
            raise ValueError(f"Expected one version marker in {relative}")
        changes.append((path, new))
    return current, next_version, changes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--part", choices=["patch", "minor", "major"], default="patch")
    parser.add_argument("--check", action="store_true", help="Check consistency without changing files")
    parser.add_argument("--dry-run", action="store_true", help="Print next product version without writing")
    args = parser.parse_args()
    old, new, changes = plan(ROOT, args.part)
    if args.check:
        print(f"Windows product version is consistent: {old}")
    elif args.dry_run:
        print(f"{old} -> {new}")
    else:
        for path, text in changes:
            path.write_text(text)
        print(f"Product version bumped: {old} -> {new}. Update CHANGELOG before publishing.")

if __name__ == "__main__":
    main()
