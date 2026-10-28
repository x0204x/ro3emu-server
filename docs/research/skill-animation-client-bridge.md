# Skill animation client bridge evidence

This document records the static client evidence used to qualify the server's skill-presentation routes. It does not
claim that Unity rendered an animation or effect successfully. Runtime packet order and player-visible acceptance
remain separate gates.

## Inspected client identities

The clean Asia client was the primary input. The Genesis copy was used only for file-identity comparison.

| File | Asia SHA-256 | Genesis comparison |
| --- | --- | --- |
| `ro3.exe` | `F5F73D8616A2500E0FB0223D83774E2D7F6A74C1BBDAD772F4FEFC9BF5812036` | identical |
| `ro3_Data/Managed/Assembly-CSharp.dll` | `4365BCCFB4BADBF9D73C39CD8491F8BCFAF7604FC09C91DBC71123B100602E11` | different |
| `ro3_Data/Plugins/x86_64/xlua.dll` | `C189E0FDFB23782D381DFD761A4D746C1E195508ED8EECBE25F23A39BFC7309A` | identical |

The following animation-path Lua payloads were byte-identical between the two clients: `LC_Scene_Sync`,
`LC_Bullet_Helper`, `LC_Bullet_Mgr`, `LC_Bullet`, `LC_BulletEmit_Mgr`, `LC_PerformEffect_Mgr`,
`LC_PerformEffect_Agent`, and `AnimationString`. `Assembly-CSharp.dll`, `SkillConfig`, `NW_GameServerHandler`, and
`NW_NetworkManager` differed. File identity alone does not prove which payload was loaded at runtime.

## Route-to-presentation map

The isolated Lua decompilation maps the nine admitted routes as follows:

| Route | Client handler | Presentation path |
| ---: | --- | --- |
| 31100 / 31106 | `_useInSkill` | `LC_Skill_RunTimeContext.UseServerSkill` -> skill state -> timeline animation and GFX points |
| 31121 / 31107 | `_extraUseInSkill` | `LC_PerformTimeLine_Agent.AddSkillTimeLine` -> `LC_TimeLine_Object.Build` |
| 31101 | `_skillHit` | `LC_DamageHelper.CacheDamageHit` -> damage action and hit VFX |
| 31119 / 31109 | `_skillEffectDisplay` | `LC_PerformEffect_Mgr.DisplayEffect` |
| 31120 / 31108 | `_skillFixTimeBullet` | `SkillCreateFixTimeBullet` -> `LC_Bullet_Mgr.DisplayBullet` |

These are independent presentation paths. Sending all of them for every skill is not equivalent to reproducing the
official server and can duplicate client-owned `m_kShows` effects.

REA managed inspection also closes the inbound boundary for the clean Asia build. `HappyEngineClient.OnMsgReceived`
enters `Happyengine.Network.dll/NW_CBBNetworkManager`, whose queue/drain path writes message id, payload pointer and
length into the shared Lua arrays and invokes the callback registered by `NW_GameServerHandler`. `HandlerArray` then
selects the protobuf type, calls `pb.decode_unsafe`, and publishes the decoded table through the Scene/Message event to
`LC_Scene_Sync`. `Assembly-CSharp.dll` contains the generic Lua environment and bridge, but is not the direct 311xx
receiver. This is a static call chain and does not prove that a particular packet was received in a play session.

## Cast animation

`LC_Scene_Sync._useInSkill` discards a cast only when both `chantMs` and `skillMs` are non-positive. It resolves the
source unit and calls `UseServerSkill`. That method stores the skill, session, source/target timing and positions,
then enters the skill state. Timeline animation points reach `TryExecuteSkillAnimation` and ultimately
`PlayAnimationNew`.

The current player cast producer supplies `PlayAct=true`, positive bounded cast timing, `SessionId`, `StartTime`,
source, target and facing. No missing protobuf field was established. `isPlayAnim` and `iStartClientTime` seen in
the extra-timeline path are client-created Lua fields, not fields the server can add to `UseInSkillInfo`.

### Guardian-pet extra timeline

Guardian-pet child skills use the extra `UseInSkill` timeline rather than the ordinary player-cast route. In the
recovered Asia `LC_TimeLine_Object`, an Animation event is executed only when `isPlayAnim` or the protobuf-derived
`playAct` flag is true; GFX events can still execute when both animation flags are false. The server's guardian-pet
producer supplied the child skill, pet, target, timing and session fields but omitted `PlayAct`. That omission explains
the bounded symptom "effect is present but the guardian-pet cast action is absent" without changing every skill route.

The guardian-pet producer now sets `PlayAct=true`, and its wire test asserts the flag on the emitted extra-skill
message. This is a source- and test-verified correction. It is not evidence that Fire Bolt, Fire Ball or every other
ordinary player skill is visually accepted; those skills remain on the separate general route and still require the
runtime trace described below.

## Projectile and hit presentation

The fixed-time bullet path treats `startTimestampMs` as the projectile's scheduled server-time onset. The client stores
that value and subtracts `TimerManagerIns.GetSystemTime() - startTimestampMs` from the projectile duration, bounded by
its minimum effect duration. The server therefore sends the cast's start time plus the authored attack-point offset,
not the later time at which a delayed scheduler callback happened to execute. This reconciles the two clocks and keeps
the projectile aligned with the 31100 cast timeline. The client consumption is static evidence; equivalence to an
official-server packet and visual acceptance remain **unknown** until a same-skill runtime capture and playtest.

The fixed-time bullet handler consumes `effectId`, source/target/master ids and positions, skill id, split and
`startTimestampMs`. The EffectData row must contain a Bullet damage entry before the client calls
`LC_Bullet_Mgr.DisplayBullet`.

The server's 31120/31108 producer supplies those fields only when the authored timeline has no client bullet points
and a visible type-2 Bullet exists. Its field set matches the client consumer. The authored attack-point mapping and
the projectile travel calculation are statically established for Fire Bolt 1200401 and Fire Ball 1201201, but their
equivalence to official-server packet timing remains `UNKNOWN` until a same-skill, same-distance capture is compared.

31101 feeds `LC_DamageHelper.CacheDamageHit`. Correct hit action and VFX depend on `showPoint` resolving to an
EffectData Action damage entry. The server fills the required hit fields, but its generic fallback effect can still
be a fidelity gap for a skill whose authored action point is unresolved.

## Independent effect route

31119/31109 directly calls `LC_PerformEffect_Mgr.DisplayEffect`. The server currently has a bounded send API but no
source-backed producer. This absence is statically verified. It is not evidence that every skill needs an
independent effect packet: ordinary timeline `m_kShows` can already own the same presentation. A producer may be
added only after an official trace identifies a specific trigger and proves that it does not duplicate the
client-owned show.

## REA and IDA boundary

Read-only REA analysis of the clean Asia `xlua.dll` identified the native Lua payload decoding and VM boundary. The
receipt identifies `IDA MCP adapter v1` and `ida-pro-mcp 1.0.0`, but its engine-version fields are null. It therefore
does not independently prove that the analysis engine used in this run was IDA 9.4. Native analysis supports the
payload boundary; the concrete route and animation bridge comes from the decoded Lua, protobuf schema and server
source.

Bounded type-specific managed inspection recovered the direct network-to-Lua callback chain. Native token-to-address
mapping for every generated xLua wrapper remains `UNKNOWN`; an absent native mapping must not be treated as proof that
a managed or Lua method does not exist.

## Acceptance state

| Gate | State |
| --- | --- |
| Route, Lua handler and protobuf field mapping | `VERIFIED` (static) |
| Asia/Genesis input identities | `VERIFIED` |
| REA native payload boundary | `VERIFIED` within the provider-version limitation above |
| Managed network-to-Lua callback chain | `VERIFIED` (static, type-specific scope) |
| Guardian-pet extra-skill `PlayAct` gate | `VERIFIED` (Asia Lua, server source and wire test) |
| Generated xLua wrapper native addresses | `UNKNOWN` |
| Official Asia runtime packet values and order | `NOT_RUN` |
| Player-visible guardian-pet cast action | `NOT_RUN` |
| Player-visible Fire Bolt / Fire Ball animation | `NOT_RUN` |

The server now preserves the scheduled projectile onset in `31120.startTimestampMs` and sets `PlayAct=true` on the
guardian-pet extra timeline; these are testable corrections, not player-visible acceptance. The next general-skill gate
is a bounded trace of `31100 -> 31120/31108 -> 31101`, including server send time, client receive time, timeline stage,
bullet emission and hit display for Fire Bolt 1200401 and Fire Ball 1201201 at a fixed distance. Compare it with a
separately authorized official-Asia observation before declaring timing equivalent or qualifying an independent
31119/31109 producer.
