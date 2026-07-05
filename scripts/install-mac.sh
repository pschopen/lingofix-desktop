#!/bin/bash
set -e

APP_NAME="Lingofix Desktop"
BACKUP_FOLDER="Manuelle Installation (Backup)"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
APP_SOURCE="$SCRIPT_DIR/$BACKUP_FOLDER/$APP_NAME.app"
APP_DEST="/Applications/$APP_NAME.app"

echo ""
echo "=== Installation: $APP_NAME ==="
echo ""

if [ ! -d "$APP_SOURCE" ]; then
    echo "FEHLER: $APP_NAME.app nicht im DMG gefunden."
    echo "Bitte stelle sicher, dass die DMG korrekt gemountet ist."
    echo ""
    read -p "Enter zum Beenden..."
    exit 1
fi

echo "Installiere $APP_NAME nach /Applications ..."
echo "macOS fragt gleich nach deinem Passwort, um die Installation zu bestätigen."
echo ""

# Paths are fixed constants, quoted with single quotes for the shell command
# that runs with administrator privileges (needed on non-admin accounts and
# to reliably clear the quarantine flag regardless of file ownership).
INSTALL_CMD="rm -rf '$APP_DEST' && ditto '$APP_SOURCE' '$APP_DEST' && xattr -cr '$APP_DEST'"

if ! osascript -e "do shell script \"$INSTALL_CMD\" with administrator privileges with prompt \"Lingofix Desktop möchte installiert werden.\""; then
    echo ""
    echo "FEHLER: Die Installation wurde abgebrochen oder ist fehlgeschlagen."
    echo ""
    read -p "Enter zum Beenden..."
    exit 1
fi

echo "Installation abgeschlossen!"
echo "Starte $APP_NAME ..."
open "$APP_DEST"

echo ""
echo "Dieses Fenster schließt sich gleich automatisch."
sleep 3
osascript -e 'tell application "Terminal" to close (first window whose name contains "Install")' >/dev/null 2>&1 || true
