#!/usr/bin/env bash
# Deletes manual Aurora cluster snapshots older than RETENTION_DAYS for a
# given environment. Automated snapshots (Aurora's own backup retention,
# configured on the cluster itself) are out of scope here — this only cleans
# up one-off manual snapshots someone took before a risky migration/change
# and then forgot about, which otherwise accumulate storage cost forever.
#
# Usage: ./delete-old-snapshots.sh <dev|staging|production> [retention-days]
set -euo pipefail

ENV_NAME="${1:?Usage: delete-old-snapshots.sh <dev|staging|production> [retention-days]}"
RETENTION_DAYS="${2:-30}"
CLUSTER_ID="sentinelops-${ENV_NAME}"
CUTOFF_EPOCH=$(date -u -d "-${RETENTION_DAYS} days" +%s 2>/dev/null || date -u -v-"${RETENTION_DAYS}"d +%s)

echo "Looking for manual snapshots of $CLUSTER_ID older than $RETENTION_DAYS days..."

aws rds describe-db-cluster-snapshots \
  --db-cluster-identifier "$CLUSTER_ID" \
  --snapshot-type manual \
  --query "DBClusterSnapshots[].{Id:DBClusterSnapshotIdentifier,Created:SnapshotCreateTime}" \
  --output json |
  python3 -c "
import json, sys
from datetime import datetime, timezone
cutoff = datetime.fromtimestamp($CUTOFF_EPOCH, tz=timezone.utc)
for snap in json.load(sys.stdin):
    created = datetime.fromisoformat(snap['Created'].replace('Z', '+00:00'))
    if created < cutoff:
        print(snap['Id'])
" |
  while read -r snapshot_id; do
    [ -z "$snapshot_id" ] && continue
    echo "Deleting $snapshot_id..."
    aws rds delete-db-cluster-snapshot --db-cluster-snapshot-identifier "$snapshot_id" >/dev/null
  done

echo "Done."
