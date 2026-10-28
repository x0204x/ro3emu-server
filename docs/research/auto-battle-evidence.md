# Automatic battle client evidence

This page records static evidence from the shipped Asia and Genesis clients. It does not claim that pathfinding, teleport use or a player-visible battle loop was accepted in a live session.

## Client ownership

The ten `Logic/Actor/AutoAttack` Lua payloads compared for this audit are byte-identical between the Asia and Genesis clients. `LC_AutoAttack_SeekSubState`, `LC_AutoAttack_AttackSubState`, `LC_AutoAttackHelper`, `LC_DAL_AutoAttack` and `LC_System_AutoAttackMode` own target selection, reachability, movement and optional Fly Wing use. The server receives the resulting ordinary movement, skill and item-use requests; it does not select the client's next automatic target.

`LC_AutoAttackHelper.IsTargetReachable` first calls the movement layer's `CheckAutoAtkReachable`. That method does not run a fresh path search: it bounds the candidate against the current auto-battle anchor and reads the shared reachable-area grid previously filled by `UpdateReachableArea`. The immediately following `CanMoveTo` method, not `CheckAutoAtkReachable`, is the method that calls `CheckDirectWalk`; the recovered decompiler assignment boundary makes those two methods distinct.

Actual chasing separately calls `CanArriveToPos`; an unreachable target is cleared instead of being approached through an invented straight-line server move. A reachable target goes through `SetMoveTargetPos(... BattleAuto)` and `FindPathWithShareArr`, so the movement controller uses its A* path rather than a server-synthesised straight line. A zero-length path makes the controller return without entering navigation. `LC_Movement_AStarAgentNew.SetMoveTargetPos` can forward that failure only when its caller supplied a failure callback; the automatic seek dispatch does not supply one. These calls prove that the client owns and invokes a real navigation path, but they do not prove that the navigation mesh can route around a particular obstacle in a live map.

The recovered move-seek fallback has an additional bounded-loop hazard. `_pickAndDispatchToNearestSpawn` tests each unvisited spawn with `CheckCanArriveTo`, but an unreachable spawn is not added to the visited set. If no spawn can be dispatched, `_tryMoveToNearestSpawn` calls `_resetMoveSeekRuntime`, which clears the visited set. On the next update the same unreachable candidates can therefore be selected and rejected again. This is a static client-side explanation for a repeated idle decision; matching it to the reported map remains a live-trace gate.

## REA and IDA receipts

The newest inspected G-drive package records release sequence `2000000052` and build id
`ro3-asia-client-asia-wedding-stage2-mapmodule-20261022`. Its `ro3.exe` SHA-256 is
`f5f73d8616a2500e0fb0223d83774e2d7f6a74c1bbdad772f4fefc9bf5812036`. The package also identifies itself as a
reconstructed payload build, so the release label is evidence about its content manifest, not evidence that its managed
or native executables are newer than the previously inspected Asia package.

The current Asia `Assembly-CSharp.dll` (SHA-256 `4365bccfb4badbf9d73c39cd8491f8bcfaf7604fc09c91dbc71123b100602e11`) was inspected read-only with REA. The full managed inventory contains 2,044 types and 22,758 methods (`ev_f5d426c9743ff95231974ffbb7b7c7a29d7bb72cb06094e495bbc7a7e4923979`, parent `ev_56c27f5819ce203aad8ef7b2796954226f874f34a23d28d094af1b1178524005`). Its exact `PF_AStar` type contains the waypoint, async block-loading and path-search state machine, including `IsWaitWayPointFind`, `IsWaitFindPath`, `IsWaitArriveNextWayPoint`, `CantFindPath` and `CantFindNextPt`. The focused pathfinding receipt is `ev_089d6e0679f9558e9a9f5c5d534fbde2e3c7961ba0f1bd906bdad778b1095518` (parent `ev_62ca499215d1bc3f5b9032f8644dbd09a896ed3599f7c493ce22a1993159d996`). This proves that the shipped client has a real pathfinding layer; it does not prove success on the reported obstacle.

The matching `xlua.dll` (SHA-256 `c189e0fdfb23782d381dfd761a4d746c1e195508ed8eecbe25f23a39bfc7309a`) was opened in IDA through REA and closed after the read-only session. The current-session procedure inventories are `ev_eebd66a27269a87705c8a87e2f4b32439c35924a56882d0d6b98b1ea5c2f9fe1` (`xluaL_loadbuffer`) and `ev_b0fed60b20191c3ff7db85cd7e485e29f8798ad2710db9ec02ba28d7b94692ef` (`lua_pcall`). `xluaL_loadbuffer_0` only forwards to `luaL_loadbufferx` (`ev_b2c7a8ff0837240ddf4f523ede40fc467036681c92c4e3d991692e645a94bc99`, pseudocode `ev_25dceabab103bc9931a6588a497161797deaea26e256a5667a492736c8e29277`). A bounded IDA string query returned no `AutoAttack` string (`ev_fc67b3b1ed2e16f515674f308514d576d792e92d774a42b23641b71addece5ab`); that is not a whole-binary absence proof. No game-specific target-selection policy was promoted from these generic bridge functions. The relevant behavior remains in the recovered Lua payload and managed movement layer.

## Fly Wing setting

`LC_AutoAttack_SeekSubState._tryUseFly` runs during seeking, but only when all of these conditions hold:

- `kAutoAttackData.autoUseFlyItem` is true;
- the scene is an eligible field or cave;
- the configured cooldown has elapsed;
- the player owns the item in `PublicParamConfig.AutoBattleAutoUseItem` (1002001).

It then sends the ordinary item-use request. `_tryBuyFly` is separately gated by `autoBuyFlyItem`. `LC_DAL_AutoAttack._getDefaultBattleInfo` defaults both automatic purchase and automatic use to false (the use value is restored from PlayerPrefs). Consequently, automatic Fly Wing consumption is deliberately opt-in in the shipped client. A launcher or hook may expose or seed that preference, but silently forcing it on would consume player-owned items and is not a safe server correction.

## Server boundary

The server validates Fly/Butterfly Wing item use and chooses an authored walkable destination for a successful wing jump. That path is already covered by the ordinary bag and movement services. It cannot make the client choose a wing or a different auto-battle target before an item request arrives.

For the reported idle-behind-obstacle case, static evidence establishes three contributing conditions: the reachable-area filter can reject a target, a zero-path seek has no failure callback, and a fully unreachable spawn set can be cleared and retried. Automatic Fly Wing use is also off by default. The newest G-drive Lua still filters candidates with `IsTargetReachable`, calls `CanArriveToPos` before chasing, clears an unreachable target and advances to the next skill/target. Fly Wing use occurs in the seek loop only when its opt-in preference, scene and cooldown gates pass.

Whether the client should find a detour on the exact map remains `UNKNOWN` until a bounded live trace captures anchor/target position, reachable-grid result, `CanArriveToPos`, selected path count, preference values and the next seek decision. Silently forcing `autoUseFlyItem` would consume player-owned items, while replacing failed movement with a server-synthesised segment would bypass the client's authored navigation. Neither is accepted as a safe correction. A client-side retry/fallback change must first be validated against that trace.

## Acceptance state

| Gate | State |
| --- | --- |
| Asia/Genesis AutoAttack Lua identity | `VERIFIED` |
| Managed `PF_AStar` pathfinding ownership | `VERIFIED` (REA static) |
| Native xLua bridge boundary | `VERIFIED` (IDA static) |
| Target/reachability/Fly Wing decision ownership | `VERIFIED` (static) |
| Reachable-grid versus direct-walk method boundary | `VERIFIED` (recovered Lua assignment boundary) |
| Unreachable spawn retry loop | `VERIFIED` (static control flow); reported-map match `UNKNOWN` |
| Server item-use and wing-jump boundary | `VERIFIED` (source/tests) |
| Exact obstacle runtime decision | `NOT_RUN` |
| Player-visible reroute or automatic wing use | `NOT_RUN` |
