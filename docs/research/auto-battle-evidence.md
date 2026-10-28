# Automatic battle client evidence

This page records static evidence from the shipped Asia and Genesis clients. It does not claim that pathfinding, teleport use or a player-visible battle loop was accepted in a live session.

## Client ownership

The ten `Logic/Actor/AutoAttack` Lua payloads compared for this audit are byte-identical between the Asia and Genesis clients. `LC_AutoAttack_SeekSubState`, `LC_AutoAttack_AttackSubState`, `LC_AutoAttackHelper`, `LC_DAL_AutoAttack` and `LC_System_AutoAttackMode` own target selection, reachability, movement and optional Fly Wing use. The server receives the resulting ordinary movement, skill and item-use requests; it does not select the client's next automatic target.

`LC_AutoAttackHelper.IsTargetReachable` calls the movement layer's `CheckAutoAtkReachable`. Chasing calls `CanArriveToPos`; an unreachable target is cleared instead of being approached through an invented straight-line server move. Seek movement also checks reachability before `SetMoveTargetPos(... BattleAuto)`. These calls prove that the client asks its navigation layer for a route, but they do not prove that the navigation mesh can route around a particular obstacle in a live map.

## REA and IDA receipts

The current Asia `Assembly-CSharp.dll` (SHA-256 `4365bccfb4badbf9d73c39cd8491f8bcfaf7604fc09c91dbc71123b100602e11`) was inspected read-only with REA. Its exact `PF_AStar` type contains the waypoint, async block-loading and path-search state machine, including `IsWaitWayPointFind`, `IsWaitFindPath`, `IsWaitArriveNextWayPoint`, `CantFindPath` and `CantFindNextPt`. The managed inventory receipt is `ev_089d6e0679f9558e9a9f5c5d534fbde2e3c7961ba0f1bd906bdad778b1095518` (parent `ev_62ca499215d1bc3f5b9032f8644dbd09a896ed3599f7c493ce22a1993159d996`). This proves that the shipped client has a real pathfinding layer; it does not prove success on the reported obstacle.

The matching `xlua.dll` (SHA-256 `c189e0fdfb23782d381dfd761a4d746c1e195508ed8eecbe25f23a39bfc7309a`) was opened in IDA through REA and closed after the read-only session. `xluaL_loadbuffer_0` only forwards to `luaL_loadbufferx` (`ev_38dea9c38b708e452816d746e7c3e576a112b1a171bfa0e448d829f8f4f39672`); the native Lua load and hook receipts are `ev_6047a2c1ba73a5a9762a77c60ef755c1bde6601a3f359cff3ebc04862f8c5769` and `ev_9895ed7fe6651ae30d341a939ae914fa0699869239e2ed09a06b8b3edb009890`. No game-specific target-selection policy was promoted from those generic bridge functions. The relevant behavior remains in the signed Lua payload and managed movement layer.

## Fly Wing setting

`LC_AutoAttack_SeekSubState._tryUseFly` runs during seeking, but only when all of these conditions hold:

- `kAutoAttackData.autoUseFlyItem` is true;
- the scene is an eligible field or cave;
- the configured cooldown has elapsed;
- the player owns the item in `PublicParamConfig.AutoBattleAutoUseItem` (1002001).

It then sends the ordinary item-use request. `_tryBuyFly` is separately gated by `autoBuyFlyItem`. `LC_DAL_AutoAttack._getDefaultBattleInfo` defaults both automatic purchase and automatic use to false (the use value is restored from PlayerPrefs). Consequently, automatic Fly Wing consumption is deliberately opt-in in the shipped client. A launcher or hook may expose or seed that preference, but silently forcing it on would consume player-owned items and is not a safe server correction.

## Server boundary

The server validates Fly/Butterfly Wing item use and chooses an authored walkable destination for a successful wing jump. That path is already covered by the ordinary bag and movement services. It cannot make the client choose a wing or a different auto-battle target before an item request arrives.

For the reported idle-behind-obstacle case, static evidence establishes two contributing conditions: the target can be rejected as unreachable, and automatic Fly Wing use is off by default. Whether the client should find a detour on the exact map remains `UNKNOWN` until a bounded live trace captures target position, `CanArriveToPos`, selected path, preference values and the next seek decision. A client-side reroute change must be validated on that trace rather than by sending synthetic movement from the server.

## Acceptance state

| Gate | State |
| --- | --- |
| Asia/Genesis AutoAttack Lua identity | `VERIFIED` |
| Managed `PF_AStar` pathfinding ownership | `VERIFIED` (REA static) |
| Native xLua bridge boundary | `VERIFIED` (IDA static) |
| Target/reachability/Fly Wing decision ownership | `VERIFIED` (static) |
| Server item-use and wing-jump boundary | `VERIFIED` (source/tests) |
| Exact obstacle runtime decision | `NOT_RUN` |
| Player-visible reroute or automatic wing use | `NOT_RUN` |
