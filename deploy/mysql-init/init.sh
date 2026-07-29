#!/bin/bash
# Runs once against an empty MySQL data volume (docker-entrypoint-initdb.d
# executes .sh scripts with the same env vars as the server container).
# Creates the per-app databases and app-level users.
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
