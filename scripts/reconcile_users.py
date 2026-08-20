#!/usr/bin/env python3
"""User-reconciliation script - P1's go/no-go gate for the IAM program.

Spec (GorillaHR's specs/distributed-identity-architecture.md), section 9:

    "if the P1 reconciliation shows only a handful of people in both
    systems, a one-way nightly sync (~200 lines, 1-2 weeks) beats an
    8-12 week IAM program."

This reads both apps' user tables directly, matches accounts by email, and
reports how many people actually exist in both systems. That overlap count
is the number the go/no-go decision hinges on - not a percentage, a headcount
("a handful" is a raw number).

Usage:
    python reconcile_users.py            # counts only
    python reconcile_users.py --emails   # also lists the overlapping addresses

Connects with each app's own read-write app-user credentials by default,
which is fine for local dev - for a real go/no-go run against production
data, point this at a read replica or a dedicated read-only account instead;
this script only ever executes SELECT.

Configuration is environment variables, matching gorilla-platform/deploy's
existing naming (GORILLAHR_DB_PASSWORD / RECRUITMENT_DB_PASSWORD):

    GORILLAHR_DB_HOST       (default: localhost)
    GORILLAHR_DB_PORT       (default: 3306)
    GORILLAHR_DB_NAME       (default: gorillahr)
    GORILLAHR_DB_USER       (default: gorillahr_app)
    GORILLAHR_DB_PASSWORD   (required)

    RECRUITMENT_DB_HOST     (default: localhost)
    RECRUITMENT_DB_PORT     (default: 3306)
    RECRUITMENT_DB_NAME     (default: RecruitmentGorilla)
    RECRUITMENT_DB_USER     (default: root - RG has no dedicated app DB user
                              today; see server/appsettings.json)
    RECRUITMENT_DB_PASSWORD (required)

Requires: pymysql (`pip install pymysql`, or run via GorillaHR's backend venv,
which already has it: ./.venv/Scripts/python.exe reconcile_users.py).
"""

from __future__ import annotations

import argparse
import os
import sys
from dataclasses import dataclass, field


@dataclass(frozen=True)
class AppUser:
    email: str
    active: bool


@dataclass(frozen=True)
class ReconciliationReport:
    hr_total: int
    hr_active: int
    rg_total: int
    rg_active: int
    overlap_all: set[str] = field(repr=False)
    overlap_active: set[str] = field(repr=False)

    @property
    def hr_only(self) -> int:
        return self.hr_total - len(self.overlap_all)

    @property
    def rg_only(self) -> int:
        return self.rg_total - len(self.overlap_all)


def normalize_email(email: str) -> str:
    """Lowercase + trim so casing/whitespace differences between the two
    apps' signup forms don't produce false negatives."""
    return email.strip().lower()


def reconcile(hr_users: list[AppUser], rg_users: list[AppUser]) -> ReconciliationReport:
    """Pure matching logic, deliberately separated from DB I/O - this is the
    part correctness actually depends on, and it's cheap to hand-verify or
    unit test without a database."""
    hr_emails = {normalize_email(u.email) for u in hr_users}
    rg_emails = {normalize_email(u.email) for u in rg_users}
    hr_active_emails = {normalize_email(u.email) for u in hr_users if u.active}
    rg_active_emails = {normalize_email(u.email) for u in rg_users if u.active}

    return ReconciliationReport(
        hr_total=len(hr_users),
        hr_active=len(hr_active_emails),
        rg_total=len(rg_users),
        rg_active=len(rg_active_emails),
        overlap_all=hr_emails & rg_emails,
        overlap_active=hr_active_emails & rg_active_emails,
    )


def fetch_hr_users() -> list[AppUser]:
    import pymysql

    conn = pymysql.connect(
        host=os.environ.get("GORILLAHR_DB_HOST", "localhost"),
        port=int(os.environ.get("GORILLAHR_DB_PORT", "3306")),
        database=os.environ.get("GORILLAHR_DB_NAME", "gorillahr"),
        user=os.environ.get("GORILLAHR_DB_USER", "gorillahr_app"),
        password=_require_env("GORILLAHR_DB_PASSWORD"),
    )
    try:
        with conn.cursor() as cur:
            # A user's "active" is is_active AND (no employee row, or that
            # employee is ACTIVE) - an employee record only exists once HR
            # onboarding creates one, and a bare user with no employee row
            # is still a real account (e.g. before Onboarding completes it).
            cur.execute(
                """
                SELECT u.email, u.is_active, e.status
                FROM users u
                LEFT JOIN employees e ON e.user_id = u.id
                """
            )
            return [
                AppUser(email=email, active=bool(is_active) and status in (None, "ACTIVE"))
                for email, is_active, status in cur.fetchall()
            ]
    finally:
        conn.close()


def fetch_rg_users() -> list[AppUser]:
    import pymysql

    conn = pymysql.connect(
        host=os.environ.get("RECRUITMENT_DB_HOST", "localhost"),
        port=int(os.environ.get("RECRUITMENT_DB_PORT", "3306")),
        database=os.environ.get("RECRUITMENT_DB_NAME", "RecruitmentGorilla"),
        user=os.environ.get("RECRUITMENT_DB_USER", "root"),
        password=_require_env("RECRUITMENT_DB_PASSWORD"),
    )
    try:
        with conn.cursor() as cur:
            cur.execute("SELECT Email, IsActive FROM Users")
            return [AppUser(email=email, active=bool(active)) for email, active in cur.fetchall()]
    finally:
        conn.close()


def _require_env(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        sys.exit(f"Missing required environment variable: {name}")
    return value


def print_report(report: ReconciliationReport, show_emails: bool) -> None:
    print("=== User reconciliation ===\n")
    print(f"GorillaHR:            {report.hr_total} users ({report.hr_active} active)")
    print(f"Recruitment.Gorilla:  {report.rg_total} users ({report.rg_active} active)")
    print()
    print(f"In both systems (any status):     {len(report.overlap_all)}")
    print(f"In both systems (active/active):  {len(report.overlap_active)}")
    print(f"HR only:                          {report.hr_only}")
    print(f"RG only:                          {report.rg_only}")

    if show_emails:
        print("\nOverlapping addresses (active in both):")
        for email in sorted(report.overlap_active):
            print(f"  {email}")

    print(
        "\nGo/no-go framing (spec section 9): if the active/active overlap "
        "above is only a handful of people, a one-way nightly sync beats "
        "the rest of the IAM program. This number is only meaningful if "
        "both databases hold real (or realistically representative) data - "
        "seeded dev/test data will trivially show near-zero overlap."
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument(
        "--emails",
        action="store_true",
        help="also print the overlapping (active-in-both) email addresses - PII, omit by default",
    )
    args = parser.parse_args()

    hr_users = fetch_hr_users()
    rg_users = fetch_rg_users()
    report = reconcile(hr_users, rg_users)
    print_report(report, show_emails=args.emails)


if __name__ == "__main__":
    main()
