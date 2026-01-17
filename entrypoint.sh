#!/bin/sh

wait_either() {
    local pid1=$1
    local pid2=$2

    while true; do
        if ! kill -0 "$pid1" 2>/dev/null; then
            wait "$pid1"
            EXITED_PID=$pid1
            REMAINING_PID=$pid2
            return $?
        fi

        if ! kill -0 "$pid2" 2>/dev/null; then
            wait "$pid2"
            EXITED_PID=$pid2
            REMAINING_PID=$pid1
            return $?
        fi

        sleep 0.5
    done
}

# Signal handling for graceful shutdown
terminate() {
    echo "Caught termination signal. Shutting down..."
    if [ -n "$BACKEND_PID" ] && kill -0 "$BACKEND_PID" 2>/dev/null; then
        kill "$BACKEND_PID"
    fi
    if [ -n "$FRONTEND_PID" ] && kill -0 "$FRONTEND_PID" 2>/dev/null; then
        kill "$FRONTEND_PID"
    fi
    # Wait for children to exit
    wait
    exit 0
}
trap terminate TERM INT

# Use env vars or default to 1000
PUID=${PUID:-1000}
PGID=${PGID:-1000}

# Determine group name - use existing group if GID is taken, otherwise create appgroup
EXISTING_GROUP=$(getent group "$PGID" 2>/dev/null | cut -d: -f1)
if [ -n "$EXISTING_GROUP" ]; then
    APP_GROUP="$EXISTING_GROUP"
    echo "Using existing group '$APP_GROUP' for GID $PGID"
elif ! getent group appgroup >/dev/null; then
    addgroup -g "$PGID" appgroup
    APP_GROUP="appgroup"
else
    APP_GROUP="appgroup"
fi

# Create user if it doesn't exist
if ! id appuser >/dev/null 2>&1; then
    adduser -D -H -u "$PUID" -G "$APP_GROUP" appuser
fi

# Set environment variables
if [ -z "${BACKEND_URL}" ]; then
    export BACKEND_URL="http://localhost:8080"
fi

if [ -z "${FRONTEND_BACKEND_API_KEY}" ]; then
    export FRONTEND_BACKEND_API_KEY=$(head -c 32 /dev/urandom | hexdump -ve '1/1 "%.2x"')
fi

# Change permissions on /config directory to the given PUID and PGID
chown $PUID:$PGID /config

cd /app/backend

# Start frontend FIRST so startup page is available immediately
echo "Starting frontend (startup page will be available while backend initializes)..."
cd /app/frontend
su-exec appuser npm run start &
FRONTEND_PID=$!

# Give frontend a moment to bind to port
sleep 2

# Run database migration with PRAGMA optimizations (5-10x faster)
cd /app/backend
echo "Starting database migration..."
su-exec appuser ./NzbWebDAV --db-migration
echo "Database migration completed."

# Run backend as appuser in background
su-exec appuser ./NzbWebDAV &
BACKEND_PID=$!

# Wait for either to exit
wait_either $BACKEND_PID $FRONTEND_PID
EXIT_CODE=$?

# Determine which process exited
if [ "$EXITED_PID" -eq "$FRONTEND_PID" ]; then
    echo "The web-frontend has exited. Shutting down the web-backend..."
else
    echo "The web-backend has exited. Shutting down the web-frontend..."
fi

# Kill the remaining process
kill $REMAINING_PID

# Exit with the code of the process that died first
exit $EXIT_CODE