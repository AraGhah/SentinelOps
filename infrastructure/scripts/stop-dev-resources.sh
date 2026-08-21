#!/usr/bin/env bash
# Scales the dev environment's always-on compute to zero without destroying
# any stack — for nights/weekends when nobody's using it. Aurora Serverless
# v2 already scales its ACUs down on its own (see aurora.minCapacityAcu in
# environments.ts), so this only needs to touch ECS: the API and frontend
# services, whose desired count otherwise sits at 1 around the clock.
#
# Usage: ./stop-dev-resources.sh
set -euo pipefail

CLUSTER="sentinelops-cluster"
FRONTEND_CLUSTER="sentinelops-frontend-cluster"

echo "Scaling sentinelops-api to 0 tasks..."
aws ecs update-service --cluster "$CLUSTER" --service sentinelops-api --desired-count 0 >/dev/null

echo "Scaling sentinelops-frontend to 0 tasks..."
aws ecs update-service --cluster "$FRONTEND_CLUSTER" --service sentinelops-frontend --desired-count 0 >/dev/null

echo "Done. Run start-dev-resources.sh to bring dev back up."
