# Tools

Everything here works on files from your own client (`RO3_CLIENT_DIR`, the `ROO_PC` folder) and writes into the
ignored `data/` and `re/` folders. The main [README](../README.md#1-export-the-game-data) has the export recipe.

| Tool | What it does |
|---|---|
| `LuaDecode` | turns the client's obfuscated Lua 5.4 chunks (`Recovery/LuaPayload/**/*.lua.bytes`) into standard bytecode |
| `unluac-batch` | decompiles a whole bytecode tree in one JVM with [unluac](https://sourceforge.net/projects/unluac/) (bring your own `unluac.jar`) |
| `ConfigExport` | exports the Lua config modules (`Config\DataConfig`, `Config\SceneEditor`) to JSON for the server |
| `ProtoDump` | rebuilds `.proto` files from the client's `LocalProto/proto.pb.bytes` |
| `msgtable.js` | extracts the message table (id, name, request and response types) from the decompiled Lua |
| `opcode-matrix.js` | cross-checks the Lua registries, callbacks, Scene/AutoSync consumers and protobuf descriptor, and emits the complete static numeric-union and reachable wire-schema audit |
| `WorldData/` | reads the client's VFS: nav grids (`navexport.js`, `navparse.js`, `navquery.js`), scene editor data, single assets (`vfsget.js`) |
| `Loc/` | the localization pipeline behind `Ro3Emu.Loc`: scans the client for Chinese text, builds the translation queue and the `loc_*.tsv` maps |
| `ClientTextures/` | renders English versions of images with baked-in Chinese text from a texture dump of your client |
| `EconomyProbe` | read-only economy snapshots and ledger checks against a server database (used by load scenario S3) |
| `patch-client.ps1` | rebuilds the client hooks and patches the client in `RO3_CLIENT_DIR` against a local server |
| `run-server.ps1` | runs the server and restarts it when it exits, logging to `data/logs/` |

`opcode-matrix.js` always emits the generic static decode. Version-specific evidence IDs and semantic overrides are emitted only when every recorded descriptor and Lua input hash matches a known profile; unknown builds remain unclassified instead of inheriting stale evidence.
