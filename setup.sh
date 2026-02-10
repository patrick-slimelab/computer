#!/usr/bin/env bash
set -euo pipefail

# setup.sh - Onboarding for 'computer' bot

echo "=== Computer Bot Setup ==="
echo "This bot connects to Matrix and MongoDB to serve !randcaps commands."
echo ""

if [[ -f .env ]]; then
  echo "Found existing .env file (content hidden)."
  echo ""
  read -r -p "Reuse these secrets? [Y/n] " REUSE
  if [[ ! "$REUSE" =~ ^[Nn] ]]; then
    echo "Using existing .env"
    docker compose up -d --build
    echo "Done! Logs:"
    docker compose logs -f
    exit 0
  fi
fi

read -r -p "Matrix Homeserver URL [https://cclub.cs.wmich.edu]: " HS
HS=${HS:-https://cclub.cs.wmich.edu}

read -r -p "Matrix User ID [@computer:cclub.cs.wmich.edu]: " USER_ID
USER_ID=${USER_ID:-@computer:cclub.cs.wmich.edu}

read -r -s -p "Matrix Password (or Access Token): " PASSWORD
echo ""

read -r -p "MongoDB URI [mongodb://scoob-doghouse-mongo:27017]: " MONGO_URI
MONGO_URI=${MONGO_URI:-mongodb://scoob-doghouse-mongo:27017}

read -r -p "Root User ID (for privileged commands) [@slimeq:cclub.cs.wmich.edu]: " ROOT_USER
ROOT_USER=${ROOT_USER:-@slimeq:cclub.cs.wmich.edu}

echo "Writing .env..."
cat > .env <<EOF
MATRIX_HOMESERVER=$HS
MATRIX_USER_ID=$USER_ID
MATRIX_PASSWORD=$PASSWORD
MONGODB_URI=$MONGO_URI
MONGODB_DB=matrix_index
ROOT_USER_ID=$ROOT_USER
EOF

echo "Building and starting..."
docker compose up -d --build

echo "Done! Logs:"
docker compose logs -f
