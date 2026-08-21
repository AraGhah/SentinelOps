#!/usr/bin/env bash
# Reverses stop-dev-resources.sh — scales the API and frontend ECS services
# back to dev's configured desired count (environments.ts: ecs.apiDesiredCount
# / ecs.frontendDesiredCount, both 1 today). If those values change, update
# the numbers below to match.
#
# Usage: ./start-dev-resources.sh
set -euo pipefail

CLUSTER="sentinelops-cluster"
FRONTEND_CLUSTER="sentinelops-frontend-cluster"
API_DESIRED_COUNT=1
FRONTEND_DESIRED_COUNT=1

echo "Scaling sentinelops-api to $API_DESIRED_COUNT task(s)..."
aws ecs update-service --cluster "$CLUSTER" --service sentinelops-api --desired-count "$API_DESIRED_COUNT" >/dev/null

echo "Scaling sentinelops-frontend to $FRONTEND_DESIRED_COUNT task(s)..."
aws ecs update-service --cluster "$FRONTEND_CLUSTER" --service sentinelops-frontend --desired-count "$FRONTEND_DESIRED_COUNT" >/dev/null

echo "Done. Services will take a few minutes to reach steady state."
