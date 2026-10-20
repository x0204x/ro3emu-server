#!/usr/bin/env bash
# RO3Emu server on Linux, from the release archive: nothing to install (the server and the RO3Emu tool are
# self-contained). Exports the game data from a game folder on the first run, then runs the server in this terminal
# or as a systemd service. Players join from Windows with: RO3Emu.exe join <address>
#
# usage: ./server.sh [--client DIR | --download DIR] [--host ADDRESS] [--gm ACCOUNT] [--voice-port PORT] [--service] [--reexport]
#   --client DIR    a game folder copied from Windows (at least its ro3_Data/StreamingAssets); first run only
#   --download DIR  download only the game files the export needs from the official Genesis servers into DIR
#   --host ADDRESS  the address players connect to (default: this machine's first address)
#   --gm ACCOUNT    an account that gets GM commands in game (repeatable)
#   --voice-port PORT  turn on party voice chat on this TCP port, for example 33101 (off by default)
#   --service       install and start a systemd service (needs sudo) instead of running in this terminal
#   --reexport      export the game data again (after a game update)
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
root=$(pwd)

client=""; download=""; host=""; voice=""; service=0; reexport=0; gms=()
while [ $# -gt 0 ]; do
  case "$1" in
    --client) client="$2"; shift 2 ;;
    --download) download="$2"; shift 2 ;;
    --host) host="$2"; shift 2 ;;
    --gm) gms+=("$2"); shift 2 ;;
    --voice-port)
      [[ "$2" =~ ^[0-9]+$ ]] && [ "$2" -gt 0 ] && [ "$2" -lt 65536 ] || { echo "--voice-port needs a port number"; exit 1; }
      voice="$2"; shift 2 ;;
    --service) service=1; shift ;;
    --reexport) reexport=1; shift ;;
    -h|--help) sed -n '2,14p' "$0"; exit 0 ;;
    *) echo "unknown option $1 (see ./server.sh --help)"; exit 1 ;;
  esac
done

step() { printf '\n== %s\n' "$1"; }
fail() { printf '\n%s\n' "$1" >&2; exit 1; }

chmod +x ./RO3Emu ./server/Ro3Emu.Server 2>/dev/null || true
export RO3EMU_HOME="$root"

# 1. Game data, exported once from the game folder.
if [ -n "$download" ]; then
  step 'Downloading the game files the server needs from the official servers'
  ./RO3Emu install "$download" --server-files --yes
  client="$download"
fi
if [ "$reexport" = 1 ] || [ ! -f data/config/MapResource.json ] || [ ! -f data/msgtable.tsv ]; then
  step 'Exporting the game data (first run only, about a minute)'
  if [ -z "$client" ]; then
    echo '  Copy the game folder from Windows to this machine first (at least its ro3_Data/StreamingAssets folder),'
    echo '  or run ./server.sh --download <folder> to get the needed files from the official servers.'
    read -r -p '  Path of the game folder: ' client
  fi
  [ -d "$client/ro3_Data/StreamingAssets/Recovery/LuaPayload" ] || fail "No ro3_Data/StreamingAssets/Recovery/LuaPayload in '$client'. Copy the whole game folder and pass it with --client."
  ./RO3Emu export "$(readlink -f "$client")" "$root/data"
fi

# 2. Settings: the address players reach, GM accounts, open content and the battle pass.
if [ -z "$host" ]; then
  host=$(hostname -I 2>/dev/null | awk '{print $1}')
  [ -n "$host" ] || host=127.0.0.1
fi
case "$host" in
  127.*|10.*|192.168.*|172.1[6-9].*|172.2[0-9].*|172.3[01].*)
    echo "  Note: $host is a local network address. Players outside your network need your public address: --host <public IP>." ;;
esac
settings=(
  "ASPNETCORE_ENVIRONMENT=Production"
  "Ro3Emu__DataDir=$root/data"
  "Ro3Emu__PublicHost=$host"
  "Ro3Emu__OpenContent__Enabled=true"
  "Ro3Emu__Pass__Play=true"
  "Ro3Emu__Pass__FreePremium=true"
)
for i in "${!gms[@]}"; do settings+=("Ro3Emu__GmAccounts__$i=${gms[$i]}"); done
[ -z "$voice" ] || settings+=("Ro3Emu__Voice__Port=$voice")
[ ${#gms[@]} -gt 0 ] || echo '  No --gm account: nobody has GM commands in game.'

ports_note() {
  if [ -n "$voice" ]; then
    echo "Open TCP ports 5380, 20000, 33100 and $voice (voice) for players (firewall, and port forwarding behind a router)."
  else
    echo "Open TCP ports 5380, 20000 and 33100 for players (firewall, and port forwarding behind a router)."
  fi
  if command -v ufw >/dev/null && sudo -n ufw status 2>/dev/null | grep -q 'Status: active'; then
    echo "  ufw: sudo ufw allow 5380/tcp && sudo ufw allow 20000/tcp && sudo ufw allow 33100/tcp${voice:+ && sudo ufw allow $voice/tcp}"
  fi
  echo "Players: RO3Emu.exe join $host"
}

# 3. Run: as a service, or in this terminal (Ctrl+C stops it).
if [ "$service" = 1 ]; then
  step 'Installing the systemd service ro3emu'
  unit=/etc/systemd/system/ro3emu.service
  {
    echo '[Unit]'
    echo 'Description=RO3Emu server'
    echo 'After=network.target'
    echo
    echo '[Service]'
    echo "User=$(id -un)"
    echo "WorkingDirectory=$root/server"
    echo "ExecStart=$root/server/Ro3Emu.Server --Ro3Emu:PreloadGameData=true"
    for s in "${settings[@]}"; do echo "Environment=$s"; done
    echo 'Restart=always'
    echo
    echo '[Install]'
    echo 'WantedBy=multi-user.target'
  } | sudo tee "$unit" >/dev/null
  sudo systemctl daemon-reload
  sudo systemctl enable --now ro3emu
  sudo systemctl restart ro3emu
  echo "Running. Logs: journalctl -u ro3emu -f   Stop: sudo systemctl stop ro3emu"
  ports_note
else
  step "Starting the server on $host (Ctrl+C stops it)"
  ports_note
  cd "$root/server"
  exec env "${settings[@]}" ./Ro3Emu.Server --Ro3Emu:PreloadGameData=true
fi
