#!/usr/bin/env bash
set -euo pipefail

# setup.sh - Onboarding for 'computer' bot

echo "=== Computer Bot Setup ==="
echo "This bot connects to Matrix and MongoDB to serve !randcaps commands."
echo ""

# Load existing values as defaults
EXISTING_HS="https://cclub.cs.wmich.edu"
EXISTING_USER="@computer:cclub.cs.wmich.edu"
EXISTING_PASS=""
EXISTING_MONGO="mongodb://scoob-doghouse-mongo:27017"
EXISTING_ROOT="@slimeq:cclub.cs.wmich.edu"
EXISTING_SD_AUTH=""
EXISTING_MEDIA=""

if [[ -f .env ]]; then
  echo "Found existing .env file. Loading defaults..."
  # Source it to get values
  set -a
  source .env
  set +a
  
  EXISTING_HS="${MATRIX_HOMESERVER:-$EXISTING_HS}"
  EXISTING_USER="${MATRIX_USER_ID:-$EXISTING_USER}"
  EXISTING_PASS="${MATRIX_PASSWORD:-}"
  EXISTING_MONGO="${MONGODB_URI:-$EXISTING_MONGO}"
  EXISTING_ROOT="${ROOT_USER_ID:-$EXISTING_ROOT}"
  EXISTING_SD_AUTH="${SD_AUTH:-}"
  EXISTING_MEDIA="${MATRIX_MEDIA_URL:-}"
fi

read -r -p "Matrix Homeserver URL [$EXISTING_HS]: " HS
HS=${HS:-$EXISTING_HS}

read -r -p "Matrix User ID [$EXISTING_USER]: " USER_ID
USER_ID=${USER_ID:-$EXISTING_USER}

# Password special case: don't show it, but indicate if set
PASS_PROMPT="Matrix Password"
if [[ -n "$EXISTING_PASS" ]]; then PASS_PROMPT="$PASS_PROMPT [keep existing]"; fi
read -r -s -p "$PASS_PROMPT: " PASSWORD
echo ""
if [[ -z "$PASSWORD" && -n "$EXISTING_PASS" ]]; then
  PASSWORD="$EXISTING_PASS"
fi

read -r -p "MongoDB URI [$EXISTING_MONGO]: " MONGO_URI
MONGO_URI=${MONGO_URI:-$EXISTING_MONGO}

read -r -p "Root User ID [$EXISTING_ROOT]: " ROOT_USER
ROOT_USER=${ROOT_USER:-$EXISTING_ROOT}

read -r -p "Stable Diffusion Auth (user:pass) [${EXISTING_SD_AUTH:-empty}]: " SD_AUTH
SD_AUTH=${SD_AUTH:-$EXISTING_SD_AUTH}

read -r -p "Matrix Media URL Override [${EXISTING_MEDIA:-empty}]: " MEDIA_URL
MEDIA_URL=${MEDIA_URL:-$EXISTING_MEDIA}

if [[ "$MEDIA_URL" == "none" || "$MEDIA_URL" == "empty" || "$MEDIA_URL" == "clear" ]]; then
  MEDIA_URL=""
fi

echo "Writing .env..."
cat > .env <<EOF
MATRIX_HOMESERVER=$HS
MATRIX_USER_ID=$USER_ID
MATRIX_PASSWORD=$PASSWORD
MONGODB_URI=$MONGO_URI
MONGODB_DB=matrix_index
ROOT_USER_ID=$ROOT_USER
SD_AUTH=$SD_AUTH
MATRIX_MEDIA_URL=$MEDIA_URL
EOF

echo "Building and starting..."
docker compose up -d --build

echo "Done! Logs:"
docker compose logs -f
