#!/usr/bin/env bash
set -euo pipefail
# Space Game — deploy to production VPS
# Usage: ./deploy.sh <server-ip>

SERVER_IP="${1:?Usage: ./deploy.sh <server-ip>}"
SSH_USER="root"
REMOTE_DIR="/opt/space"

echo "==> Deploying to $SERVER_IP..."

# Sync project files to VPS (excludes dev/build artifacts)
rsync -avz --delete \
    --exclude '.git/' \
    --exclude '.godot/' \
    --exclude '/Client/android/' \
    --exclude 'obj/' \
    --exclude 'bin/' \
    --exclude '.idea/' \
    --exclude '.env' \
    --exclude 'docker-compose.override.yml' \
    --exclude '.DS_Store' \
    ./ "$SSH_USER@$SERVER_IP:$REMOTE_DIR/"

echo "==> Building and starting services..."
ssh "$SSH_USER@$SERVER_IP" "cd $REMOTE_DIR && docker compose up -d --build"

echo "==> Deploy complete!"
