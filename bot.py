import asyncio
import os
import re
import random
import logging
from nio import AsyncClient, MatrixRoom, RoomMessageText
from pymongo import MongoClient

# Config
HOMESERVER = os.getenv("MATRIX_HOMESERVER", "https://matrix.org")
USER_ID = os.getenv("MATRIX_USER_ID", "")
PASSWORD = os.getenv("MATRIX_PASSWORD", "")
ACCESS_TOKEN = os.getenv("MATRIX_ACCESS_TOKEN", "")
MONGO_URI = os.getenv("MONGODB_URI", "mongodb://mongo:27017")
MONGO_DB = os.getenv("MONGODB_DB", "matrix_index")

# Blacklist
BLACKLIST_USERS = [
    "@fish:cclub.cs.wmich.edu",
    "@rustix:cclub.cs.wmich.edu",
    "@gooey:cclub.cs.wmich.edu"
]

# Logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("computer")

async def main():
    if not USER_ID:
        logger.error("MATRIX_USER_ID is required")
        return

    client = AsyncClient(HOMESERVER, USER_ID)

    if ACCESS_TOKEN:
        client.access_token = ACCESS_TOKEN
    elif PASSWORD:
        await client.login(PASSWORD)
    else:
        logger.error("MATRIX_PASSWORD or MATRIX_ACCESS_TOKEN required")
        return

    logger.info(f"Logged in as {client.user_id}")

    # Mongo connection
    mongo = MongoClient(MONGO_URI)
    db = mongo[MONGO_DB]
    events_col = db["events"]

    async def message_callback(room: MatrixRoom, event: RoomMessageText):
        if not event.body.startswith("!randcaps"):
            return

        logger.info(f"Command received in {room.room_id}: {event.body}")

        # Aggregation pipeline for random sampling
        pipeline = [
            {
                "$match": {
                    "type": "m.room.message",
                    "content.body": {"$regex": r"^[^a-z]+$"},  # No lowercase
                    "sender": {"$nin": BLACKLIST_USERS}
                }
            },
            {"$sample": {"size": 50}}  # Fetch a batch to filter in app
        ]

        try:
            cursor = events_col.aggregate(pipeline)
            candidates = []
            
            for doc in cursor:
                body = doc.get("content", {}).get("body", "")
                if not body:
                    continue
                
                # 1. Length > 10
                if len(body) <= 10:
                    continue
                
                # 2. All caps (already checked by regex, but double check)
                if any(c.islower() for c in body):
                    continue

                # 3. Not full of non-alphabetical chars (mostly alpha)
                # Count alpha chars
                alpha_count = sum(c.isalpha() for c in body)
                if alpha_count / len(body) < 0.6: # At least 60% letters
                    continue

                candidates.append(body)

            if candidates:
                choice = random.choice(candidates)
                response = f"```\n{choice}\n```"
                await client.room_send(
                    room_id=room.room_id,
                    message_type="m.room.message",
                    content={"msgtype": "m.text", "body": response}
                )
                logger.info(f"Sent: {choice}")
            else:
                logger.info("No candidates found after filtering")
                await client.room_send(
                    room_id=room.room_id,
                    message_type="m.room.message",
                    content={"msgtype": "m.text", "body": "```\nNO SCREAMING FOUND\n```"}
                )

        except Exception as e:
            logger.error(f"Error executing command: {e}")

    client.add_event_callback(message_callback, RoomMessageText)

    await client.sync_forever(timeout=30000)

if __name__ == "__main__":
    asyncio.run(main())
