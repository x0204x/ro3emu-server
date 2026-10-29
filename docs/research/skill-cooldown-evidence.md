# Skill cooldown refusal and client presentation

The server validates the selected `SkillConfig` row's group/mode cooldown before the busy,
target, and cost checks in `CombatService.CastCoreAsync`. `SkillCastGate` serializes requests,
so a repeated 31102 cannot enter before the first request reserves its cooldown. Manual and
automatic requests use this same handler. Cooldown durations and modifiers come from the
client configuration; this change does not replace them with a fixed delay.

There was a separate repeated-input gap after a short CD expired, or on a zero-CD row:
`_bCancelCast=1` allowed a new request for the same effective non-normal row to reach
`CancelCastAsync`, restarting or interrupting the accepted cast before its chant/action
finished. The busy gate now refuses that same-row repeat until the action finishes. This
is an **inferred server safeguard**; different rows retain their configured cancellation
behavior, and normal attacks retain their existing swing/jitter and target-switch rules.
The cooldown check remains first, so a running cooldown still returns 3012.

G's `LC_Skill_Function.GetSkillCDInfoKey` keys the 31113 cooldown by group and mode.
`CheckIsSkillCD` compares its end time with the client clock. The skill button's
`UI_Sys_Main_JoyStickSkillItemWidgetBaseCtrl` checks that state before dispatch: a blocked
press returns false and may call `LC_PbHelper.Decode(ERR_Battle_SkillInCd, {rowId})`.
`ErrorCodeConfig[3012]` also sets a 1500 ms tip limit, so a blocked press can be silent.

G's `LC_Scene_Sync._skillCDNotify` overwrites each cache key in message order
(`LC_Scene_Sync.lua:4857-4864,4884-4899`). Back Slide 1510401 has a 15-second own
CD and a 1-second public CD naming its own group; the former server notification
sent both under the same key, leaving G with only the shorter end. Later public
updates could similarly shorten a longer running server cooldown. The server now
coalesces `(group, shownMode)` entries and publishes the authoritative maximum of
group and mode end times. `SkillCombatTests.CooldownNotify` covers the same-packet
case and existing longer group/mode cooldowns. Original skill CD values remain the
configuration's values.

`LC_PbHelper`'s 3012 decoder (`LC_PbHelper.lua:244-283,549-552`) reads the first parameter
as a **SkillConfig row ID**, looks up its `_iName`, and formats localization 10630000802
with that name. The previous server refusal left this parameter absent, so that decoder
returned without a named tip. `RefuseCoreAsync` now supplies the refused row ID for 3012.
No custom UI string is needed.

G's `LC_Scene_Sync._useSkillReq` clears its local wait state on 31102's response; it does
not decode the error. `_skillEndNotify` decodes a nonzero result and also interrupts the
current local skill context (`LC_Scene_Sync.lua:4568-4740,4917-4961`). A refusal during an
accepted cast therefore sends only the 3012 response, preserving that cast; an idle refusal
also sends 31103 with the row parameter so the existing client may show the named tip.

`SkillCombatTests.Cooldown` sends repeated Fire Ball 31102 requests for both manual and
automatic casts. It checks 3012 precedence over busy, the row parameter, no second timeline,
SP charge or hit, survival of the first accepted cast, an idle named-tip packet, and a new
accepted damaging cast after the unmodified configured cooldown naturally expires. These
are LAB protocol assertions. Actual player-side animation and tip rendering remain a
separate runtime acceptance gate; a locally predicted animation alone does not establish
that a second server cast was accepted.

## Manual PC input and cast animation boundary

This chain was rechecked against exact G client bytes in a static copy. Paths below are
relative to Client/ro3_Data/StreamingAssets/Recovery/LuaPayload; line numbers refer to
readable decompilations of those bytes. Normalization and decompilation did not execute the client.

| Client source | SHA-256 |
| --- | --- |
| Logic/Skill/LC_Skill_Function.lua.bytes | C270485E3D0F0959FA48C174B4F3C5090BD49C2C591D4C39730C83451B722C5B |
| Logic/Scene/LC_Scene_Sync.lua.bytes | 437E47FCC98257DE1261996AA784E096C1FCCEC106DA25CE80CB362B6D73E01D |
| Framework/Core/Lua_TimerManager.lua.bytes | D5CE16A81F991638780B3DD9F908EAFB63946F76DCD8FBD2E2FB8DC38555B4AC |
| UI/Main/Controllers/UI_Sys_Main_JoyStickSkillItemWidgetBaseCtrl.lua.bytes | 025468E4F9770CAD53769119C8619E5E395C612659D450C77692A35CB13B375F |
| UI/Main/Controllers/UI_Sys_Main_JoyStickSkillItemWidget_PCCtrl.lua.bytes | C1B8D51DAB5B29609422D7D26B2B6D9FE92CA5C754C510DADE9C21BC977230FB |
| UI/Main/Controllers/UI_Sys_Main_JoyStickSkillWidget_PCCtrl.lua.bytes | 310BC0B4DCADE12C09219EDC9FBD041DA0E7C3D8FFD1E7C74FBAA8B4C30F52F7 |
| Logic/Actor/LC_Actor_ControlMove.lua.bytes | 075D2329E530027F974962EB16AE85EFD5245A327C093908DA4FB985CF96FCC3 |
| Logic/Actor/LC_Actor_AutoAttack.lua.bytes | BF62873C074C795730B0A8940C1CC859C93A407A351A1B0C1BA7D79C4C24E30C |
| Logic/Actor/LC_Actor_SkillInput.lua.bytes | 15917909D7EF0063FB050D4850745F3A2BC8E9B2B151DFB81D82BF951D703C71 |
| Logic/Actor/LC_Actor_SkillInput_PC.lua.bytes | BD36652DC3905EB4B2C399C4911D4EDFB37981D22554440F7CAFB40BBFAE19C3 |
| Logic/Actor/SkillSelectorHandler/LC_SkillInput_Handler_PC.lua.bytes | 88FA882AE4A9100BD70E7E7E8B8D183016B637ADF8938FA0B4A808C74A4CA069 |
| Logic/Actor/SkillSelectorHandler/LC_SkillInput_Handler_Default.lua.bytes | A1AB0E7DB68A7DCBB67059359F4802F3F4E91987BF69034184E3A55E94B50BAC |
| Logic/Actor/AutoAttack/LC_AutoAttackSkillHelper.lua.bytes | 58917C256626359841CE656CA8DD7BA4A4B89C13D4D925F29FB0C53EEB987693 |
| Logic/Skill/LC_Skill_ActionPreCheck.lua.bytes | 33EEAF0A20EDE24984939729BAB92F0D0F53D52B3DEDE6E8D731E042C3008F1C |
| Logic/Skill/LC_Skill_ActionPreCheck_PC.lua.bytes | 64A70D66F281FB799B0F3E1137DCB0F745BEEB15142F52A8EBFF0F3DA06C8C82 |
| Logic/Skill/LC_Skill_ActionPreCheckManager.lua.bytes | 64A70D66F281FB799B0F3E1137DCB0F745BEEB15142F52A8EBFF0F3DA06C8C82 |
| Logic/Skill/LC_Skill_SelectorEucli_PC.lua.bytes | 320372B3F69D757CECBEE843EE98D09D239DE6CBC0ACE7E41A649C74373DF759 |
| Logic/Skill/LC_Skill_SelectorEucli.lua.bytes | DFEA0FBB10D53CD205B2344B62C82B10B3D455664E578CE53DD51B19103E449C |
| Logic/Unit/Component/LC_TimeLine_Object.lua.bytes | D15D44B85A47009050F9C3CC0A8A75F3F1F86D0AFD08134FCCE817A6490B5990 |
| Logic/Unit/Component/LC_PerformTimeLine_Agent.lua.bytes | B8DE487179BE726C1B2932415F9FDE7456D7AB1C14432235ACDFA0C22E8970C3 |
| Logic/Skill/LC_Skill_RunTimeContext.lua.bytes | 902448378C449E6790C4A8A9D6E90EFD2BA2515AD5E5BDB60E42F1821DBB24BF |
| Logic/Skill/LC_LocalSkillComponent.lua.bytes | 2D0983B1FE457CC18F8F7F15F50835BBCF567916E84C1C41A6CD56A3CDF92F72 |

The PC skill button's `_onSkillBtnDown` and `_onSkillBtnClick` both call
`_checkSkillCanCast` before dispatching the battle input event
(`UI_Sys_Main_JoyStickSkillItemWidgetBaseCtrl.lua:626–665,685–725`). That check
calls `_isSkillCDNow`, which calls `CheckIsSkillCD` with the configured row ID and
unit ID (`:1310–1374,1579–1607`). `LC_Actor_SkillInput_PC` routes these events
to the PC handler (`:262–327`); `LC_SkillInput_Handler_PC.HandleSkillRelease`
calls `LC_Skill_ActionPreCheck.CheckSkillPreSendToServer` (`:1302–1324`). Its
`CheckSkillSendServer` builds and sends `USE_SKILL_REQ` and then sets
`WaitServerSkill` / `WaitServerSkillTime` (`LC_Skill_ActionPreCheck.lua:389–446`).

For ordinary casts, the inspected input/send path does not construct a cast
animation. `LC_Scene_Sync` binds `USEINSKILL` to `_useInSkill` (`:408–415`),
which calls the unit's `m_kSkillContext.UseServerSkill` only after that server
notification arrives (`:3730–3896`). `LC_Skill_RunTimeContext.UseServerSkill`
copies `chantMs`, `skillMs`, and `startTime` from the notification and enters
the skill state (`:2816–2904`); its Animation event calls
`TryExecuteSkillAnimation` (`:10–45`). The separate extra-skill timeline route
also starts from a server message via `LC_Scene_Sync._extraUseInSkill` and
`LC_PerformTimeLine_Agent.AddSkillTimeLine` (`LC_Scene_Sync.lua:4328–4434`,
`LC_PerformTimeLine_Agent.lua:36–75`). `LC_TimeLine_Object` executes animation
only when the received payload has `isPlayAnim` or `playAct` true (`:13–35`).

Thus, for this ordinary manual PC route, rejecting a repeated 31102 without
emitting another 31100 prevents a new **cast** animation; the rejection also
must not emit an interrupting 31103 while an earlier cast is active. The button
can still show press, target indicator, or tip feedback before any server reply.
`LC_LocalSkillComponent.UseLocalSkill` is a separate local-trigger route and is
outside this ordinary manual-cast conclusion. `SkillTrace.cs` only wraps scene
message handlers and timeline `Build`, invoking each original once; it does not
replace the PC input gate. No client hook change is supported by this source
chain. Player-side visual acceptance remains `NOT_RUN`.

`SameSkillWithShortOrZeroCooldownCannotCancelItsAcceptedCast` additionally covers Soul
Expansion's short cooldown and Fire Burst's zero cooldown using their original rows. A
repeat while the first action still runs must return busy, preserve its session ID and
cost state, publish no second timeline, and let the first hit complete.
