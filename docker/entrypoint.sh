#!/bin/sh
# Ohne Parameter: Synchronisation im Abstand von SYNC_INTERVAL.
# Mit Parametern: einmalig ausführen, z.B. für den OAuth2-Login:
#   docker run -it --rm -v ./config:/config ghcr.io/... --oauth-login
APP="dotnet /app/StudIPDownloader.dll"

if [ "$#" -gt 0 ]; then
    exec $APP --config "$CONFIG" --path "$DATA" "$@"
fi

while true; do
    $APP --config "$CONFIG" --path "$DATA" --non-interactive
    code=$?
    if [ "$SYNC_INTERVAL" = "0" ]; then
        exit $code
    fi
    echo "Exit-Code $code, nächster Lauf in $SYNC_INTERVAL"
    sleep "$SYNC_INTERVAL"
done
