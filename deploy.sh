#!/usr/bin/env bash
set -euo pipefail
# Space Game — deploy to production VPS
# Usage: ./deploy.sh <server-ip>

SERVER_IP="${1:?Usage: ./deploy.sh <server-ip>}"
SSH_USER="root"
REMOTE_DIR="/opt/space"

echo "==> Deploying to $SERVER_IP..."

# Sync only what the server image builds from (mirrors the .dockerignore allowlist). Server-only
# files such as .env or docker-compose.override.yml are excluded, so --delete leaves them alone.
rsync -avz --delete \
    --exclude 'bin/' \
    --exclude 'obj/' \
    --exclude '*.user' \
    --exclude '.DS_Store' \
    --include '/Dockerfile' \
    --include '/docker-compose.yml' \
    --include '/.dockerignore' \
    --include '/Server/***' \
    --include '/GameCore/***' \
    --include '/FixedPoint/***' \
    --include '/static-ecs/***' \
    --include '/static-rollback/***' \
    --include '/static-rollback-litenetlib/***' \
    --include '/Client/' \
    --include '/Client/maps/' \
    --include '/Client/maps/*.level.bytes' \
    --exclude '*' \
    ./ "$SSH_USER@$SERVER_IP:$REMOTE_DIR/"

echo "==> Building and starting services..."
ssh "$SSH_USER@$SERVER_IP" "cd $REMOTE_DIR && docker compose up -d --build"

echo "==> Deploy complete!"
