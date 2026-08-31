#!/bin/bash
# Runs once against an empty MySQL data volume.
# Creates the per-app databases and app-level users.
#
# The image SOURCES .sh files in docker-entrypoint-initdb.d rather than
# executing them, so `set` here mutates the entrypoint's own shell and outlives
# this file. `set -u` in particular is fatal: a few lines after sourcing us the
# entrypoint reads $MYSQL_ONETIME_PASSWORD, which it never sets, so nounset
# aborts it mid-initialisation -- before it stops the temporary bootstrap
# server. That server keeps /var/run/mysqld/mysqld.sock, and every restart then
# dies on "Another process with pid N is using unix socket file ... Aborting".
#
# Observed exactly that in CI once the smoke test first got far enough to start
# the stack. So: keep the strict options for our own commands, then put the
# entrypoint's shell back the way we found it.
_gorilla_prev_opts="$(set +o)"
set -euo pipefail

mysql -u root -p"${MYSQL_ROOT_PASSWORD}" <<-EOSQL
    CREATE DATABASE IF NOT EXISTS gorillahr CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    CREATE DATABASE IF NOT EXISTS RecruitmentGorilla CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

    CREATE USER IF NOT EXISTS 'gorillahr_app'@'%' IDENTIFIED BY '${GORILLAHR_DB_PASSWORD}';
    GRANT ALL PRIVILEGES ON gorillahr.* TO 'gorillahr_app'@'%';

    CREATE USER IF NOT EXISTS 'recruitment_app'@'%' IDENTIFIED BY '${RECRUITMENT_DB_PASSWORD}';
    GRANT ALL PRIVILEGES ON RecruitmentGorilla.* TO 'recruitment_app'@'%';

    FLUSH PRIVILEGES;
EOSQL

# Restore whatever the entrypoint had set before it sourced this file. Not
# reached if the block above fails under `set -e` -- which is intended: a
# half-provisioned database should stop the container loudly, not quietly serve
# an estate with no schemas.
eval "$_gorilla_prev_opts"
unset _gorilla_prev_opts
