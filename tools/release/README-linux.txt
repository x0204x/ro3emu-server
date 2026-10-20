RO3Emu server for Linux (x64)
=============================

Nothing to install: the server and the RO3Emu tool are self-contained.

  ./server.sh --client /path/to/ROO_PC --host <public IP> --gm <your account>

The game folder (ROO_PC, the one with ro3.exe) only needs to be on this machine for
the first run, to export the game data. Copy it from a Windows PC, or let RO3Emu
download only the files the server needs from the official Genesis servers:

  ./server.sh --download /srv/ro3/ROO_PC --host <public IP> --gm <your account>

Options:
  --client DIR      a copied game folder (at least ro3_Data/StreamingAssets)
  --download DIR    get the needed game files from the official servers into DIR
  --host ADDRESS    the address players connect to (default: this machine's first address)
  --gm ACCOUNT      an account that gets GM commands in game (repeatable)
  --voice-port PORT turn on party voice chat on this TCP port, for example 33101 (off by default)
  --service         install and start a systemd service (needs sudo)
  --reexport        export the game data again (after a game update)

Open TCP ports 5380, 20000 and 33100 for players, and the voice port if you set one. Players run RO3Emu.exe on
Windows:

  RO3Emu.exe join <host address>

Data (game data, database, logs) lives in ./data next to this file.

RO3Emu does not include or host any game file.
