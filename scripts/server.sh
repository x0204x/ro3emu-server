#!/usr/bin/env bash
# One-step RO3Emu server on Linux from source: checks the .NET SDK, exports the game data from a copy of the client
# (first run only), builds the server and runs it, in this terminal or as a systemd service. Players join from Windows
# with RO3Emu.exe join <address>. Without building anything: the release's RO3Emu-server-linux-x64.tar.gz has its own
# server.sh (tools/release/server.sh).
#
# usage: scripts/server.sh [--client DIR] [--host ADDRESS] [--gm ACCOUNT] [--voice-port PORT] [--service] [--reexport]
#   --client DIR    the client folder copied from Windows (at least its ro3_Data/StreamingAssets); first run only
#   --host ADDRESS  the address players connect to (default: this machine's first address)
#   --gm ACCOUNT    an account that gets GM commands in game (repeatable)
#   --voice-port PORT  turn on party voice chat on this TCP port, for example 33101 (off by default)
#   --service       install and start a systemd service (needs sudo) instead of running in this terminal
#   --reexport      export the game data again (after a client update)
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")/.." # the repository (this script lives in scripts/)
root=$(pwd)

client=""; host=""; voice=""; service=0; reexport=0; gms=()
while [ $# -gt 0 ]; do
  case "$1" in
    --client) client="$2"; shift 2 ;;
    --host) host="$2"; shift 2 ;;
    --gm) gms+=("$2"); shift 2 ;;
    --voice-port)
      [[ "$2" =~ ^[0-9]+$ ]] && [ "$2" -gt 0 ] && [ "$2" -lt 65536 ] || { echo "--voice-port needs a port number"; exit 1; }
      voice="$2"; shift 2 ;;
    --service) service=1; shift ;;
    --reexport) reexport=1; shift ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "unknown option $1 (see scripts/server.sh --help)"; exit 1 ;;
  esac
done

step() { printf '\n== %s\n' "$1"; }
fail() { printf '\n%s\n' "$1" >&2; exit 1; }
ask() { local answer; read -r -p "$1 [Y/n] " answer; [[ ! "$answer" =~ ^[nN] ]]; }

# 1. Tools: the .NET 10 SDK, into ~/.dotnet with Microsoft's install script. The data export is C# too.
step 'Checking the tools'
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$PATH:$DOTNET_ROOT"
if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  echo '  .NET 10 SDK: missing'
  ask '  Install the .NET 10 SDK into ~/.dotnet now?' || fail 'Install the .NET 10 SDK (https://dotnet.microsoft.com/download/dotnet/10.0), then run this again.'
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_ROOT"
fi
echo '  .NET 10 SDK: ok'

# 2. Game data, exported once from the client.
if [ "$reexport" = 1 ] || [ ! -f data/config/MapResource.json ] || [ ! -f data/msgtable.tsv ]; then
  step 'Exporting the game data from the client (first run only, takes a few minutes)'
  if [ -z "$client" ]; then
    echo '  Copy the game folder from Windows to this machine first (at least its ro3_Data/StreamingAssets folder).'
    read -r -p '  Path of the copied game folder: ' client
  fi
  [ -d "$client/ro3_Data/StreamingAssets/Recovery/LuaPayload" ] || fail "No ro3_Data/StreamingAssets/Recovery/LuaPayload in '$client'. Copy the whole game folder and pass it with --client."
  client=$(readlink -f "$client")
  dotnet run --project src/Ro3Emu.Setup -c Release -- export "$client" "$root/data" --home "$root"
  [ -f data/config/MapResource.json ] || fail 'The export did not produce data/config. Check that the folder is a complete Ragnarok 3 PC client.'
fi

# 3. Build.
step 'Building the server'
dotnet publish src/Ro3Emu.Server -c Release -o server-bin -v q -nologo -clp:ErrorsOnly

# 4. Settings: the address players reach, GM accounts, open content and the battle pass.
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

# 5. Run: as a service, or in this terminal (Ctrl+C stops it).
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
    echo "WorkingDirectory=$root"
    echo "ExecStart=$root/server-bin/Ro3Emu.Server --Ro3Emu:PreloadGameData=true"
    for s in "${settings[@]}"; do echo "Environment=$s"; done
    echo "Environment=DOTNET_ROOT=$DOTNET_ROOT"
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
  exec env "${settings[@]}" ./server-bin/Ro3Emu.Server --Ro3Emu:PreloadGameData=true
fi
