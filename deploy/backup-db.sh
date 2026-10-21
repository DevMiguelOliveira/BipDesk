#!/bin/bash
set -euo pipefail

# Rotina de Backup Seguro do SQLite WAL (ADR-009 / Fatia I)
# Executa cópia snapshot consistente utilizando a API online do SQLite

BACKUP_DIR="${BACKUP_DIR:-/data/backups}"
DB_PATH="${DB_PATH:-/data/bipdesk.db}"
TIMESTAMP=$(date +"%Y%m%d_%H%M%S")
BACKUP_FILE="${BACKUP_DIR}/bipdesk_backup_${TIMESTAMP}.db"

mkdir -p "${BACKUP_DIR}"

echo ">> [BipDesk Backup] Iniciando backup consistente de ${DB_PATH}..."

if command -v sqlite3 &> /dev/null; then
    sqlite3 "${DB_PATH}" ".backup '${BACKUP_FILE}'"
else
    # Fallback caso sqlite3 CLI não esteja instalado: usa script python via SQLAlchemy/sqlite3 nativo
    python3 -c "
import sqlite3
src = sqlite3.connect('${DB_PATH}')
dst = sqlite3.connect('${BACKUP_FILE}')
src.backup(dst)
dst.close()
src.close()
"
fi

gzip -f "${BACKUP_FILE}"
echo ">> [BipDesk Backup] Concluído com sucesso: ${BACKUP_FILE}.gz"

# Retenção: remove backups com mais de 14 dias
find "${BACKUP_DIR}" -name "bipdesk_backup_*.db.gz" -mtime +14 -exec rm -f {} \;
echo ">> [BipDesk Backup] Limpeza de retenção (14 dias) executada."
