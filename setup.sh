#!/usr/bin/env bash
set -euo pipefail

# setup.sh - Onboarding for 'computer' bot

echo "=== Computer Bot Setup ==="
echo "This bot connects to Matrix and MongoDB to serve !randcaps commands."
echo ""

read -r -p "Matrix Homeserver URL [https://cclub.cs.wmich.edu]: " HS
HS=${HS:-https://cclub.cs.wmich.edu}

read -r -p "Matrix User ID [@computer:cclub.cs.wmich.edu]: " USER_ID
USER_ID=${USER_ID:-@computer:cclub.cs.wmich.edu}

read -r -s -p "Matrix Password (or Access Token): " PASSWORD
echo ""

read -r -p "MongoDB URI [mongodb://mongo:27017]: " MONGO_URI
MONGO_URI=${MONGO_URI:-mongodb://mongo:27017}

echo "Writing .env..."
cat > .env <<EOF
MATRIX_HOMESERVER=$HS
MATRIX_USER_ID=$USER_ID
MATRIX_PASSWORD=$PASSWORD
MONGODB_URI=$MONGO_URI
MONGODB_DB=matrix_index
EOF

echo "Building and starting..."
docker compose up -d --build

echo "Done! Logs:"
docker compose logs -f
