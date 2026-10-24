# Auction

Scope: `MG_Auction` (19). Client evidence is the extracted Lua, configuration, localization, C# and protobuf. Behavior has not been validated against the original client at runtime. Protocol tests use the emulator's TCP/protobuf interfaces and temporary databases on free ports.

## Ownership boundary with the trading house

`MG_TradeCenter` (27) is the 326xx trading-house family: 32602-32610, 32612-32621, 32623, 32625-32626, 32628-32630 and 5033, together with player/system stalls and their settlement, are documented in [trade-stalls.md](trade-stalls.md) ("Implemented interfaces" and "Canonical mailbox integration"). The auction service does not duplicate those routes. The distinct protocol families are visible in `MG_TradeCenter.proto`, `MG_Auction.proto`, `LC_TradeServer.lua` and `LC_AuctionServer.lua`.

## Primary client contracts

Lua paths below are relative to `re/lua-src/`; the evidence searches also cover the whole second extraction under `re/clientB/lua-src/`.

| Contract | Source and consequence |
| --- | --- |
| Own auctions | `Logic/Auction/LC_AuctionServer.lua:167-191`; `Logic/Asset/LC_DAL_Auction.lua:673-785`; `MG_Auction.proto:198-206`. 82005 accepts the type with an optional guild ID, returns `selfAuctions`, and has no result field. Type 4 fills `DungeonAuctionInfo`. |
| Bid operations | `MG_Auction.proto:228-250`; `LC_AuctionServer.lua:50-79,219-325`. FIXED=1, MANUAL=2, AUTO=3; both single and batch replies carry authoritative rows and per-auction results. |
| Persistent role data | `MG_DB.proto` `DBAuction`; `MG_DB_Increment.proto` `DBAuctionIncrement`. 5063 carries pre-buy counts, guild participation history and its oldest ID. Field tags follow the existing AUTO_SYNC times-ten rule. Actual list content comes from query responses. |
| Item prices | `Config/CFG/Lua_CFG_AuctionConfig.lua`. `_iAuctionPrice`, `_iBaseRaisePrice`, `_iSellPrice`, `_iDungeonPrice`, `_iDungeonPriceMax`, `_iCurrency`, `_iTimeLast`, `_iType`, `_iTypeAttr`, `_kIsBindGive` are distinct fields. The exported table contains 529 rows, all with numeric currency ID 1. |
| Dungeon auction loot | `UI/Dungeon/Controllers/UI_Sys_Dungeon_RightInfoWidgetCtrl.lua:838-889,1640-1680` maps `_iGuildAward` to the auction/public loot preview through the current season. The threshold is compared with participant count. `_iPartAward` at lines 799-817 fills the first-pass preview; it is not an auction reward. |
| Concrete loot chain | `TenGameplayBossConfig._iGuildAward` -> `MultiverseGroupConfig._iValue_101` or `_iValue_default` -> `AwardConfig._iDropPool` -> `DropPoolConfig`. For example group 73163 resolves to award 10073163 and pool 73163; groups 73164/73165 follow the same mapping. Preview entries are not substituted for actual rolled loot. |
| Eligibility and passing | `data/loc/loc_en.tsv:19647,19771,19774-19775`. Dungeon bidders/dividend recipients must have a reward entitlement. Passing is final for that item; all-pass accelerates settlement. Unsold dungeon loot is randomly assigned among eligible participants. |
| Pre-purchase | `data/loc/loc_en.tsv:19618`. At auction start pre-purchase selects the cheapest listings for batch auto-bids; the full budget is charged upfront. Cancel/outbid refunds arrive by mail. `:4913` describes pausing pre-auction/bulk buying while offline and explicitly resuming. |
| Guild eligibility | `data/loc/loc_en.tsv:19600`. All current guild members can bid; dividends belong to eligible event participants. Leaving the guild before settlement forfeits the dividend. |
| Mail delivery | `data/loc/loc_en.tsv:4896,4908,7136,11508-11524,11557,11569` and `MailConfig`: award, refund, auto-bid surplus, dividends and unsold delivery are attachment mail, using templates 1041-1045, 1053-1056, 1100 and 1113. |

## Client decoding boundary

`Network/NW_GameServerHandler.lua:323-326` explicitly selects `no_decode_default_array` and `no_decode_default_message`. `LC_DAL_Auction.lua:182-183` reads the length of `auctions` before guarding it; lines 742-744 and 783-785 iterate the dungeon/own lists. An empty repeated protobuf message field has no wire representation that means an initialized empty Lua table. Returning a zero-valued auction row would invent an auction. True empty responses therefore retain this static client compatibility risk. The server does not alter the client. How the original client renders an empty response has not been checked at runtime; successful C# protobuf tests do not establish it.

## Evidence searches

The searches below cover every relevant surface: all `*_monsteridmap.json` files within the scene editors, binary nav data searched as text, the complete Lua trees (Config and Logic included), decompiled C# and all protos. A zero identifier match is only a search result; it is not proof that binary content has no relevant semantics.

Three case-insensitive patterns were scanned. Columns below count matching files, not inferred features; minified JSON can contain many hits on one line.

- **A:** `auction|bidding|MG_TradeCenter|QUERY_SELF_AUCTION`
- **B:** `AUCTION_BATCH_UPLOAD|GuildChallenge|GuildLeague|GamePlayAuction|no_decode_default_array|5063`
- **C:** `unsold|流拍|WorldAuction|_iBasicIncome`

| Searched surface | Files read per scan | A | B | C |
| --- | ---: | ---: | ---: | ---: |
| `data/config/*.json` | 517 | 16 | 35 | 3 |
| `data/sceneeditor/*`, recursively, including monster maps | 381 | 0 | 35 | 0 |
| `data/nav`, recursively, text-mode identifier scan | 172 | 0 | 0 | 0 |
| `data/loc`, recursively | 2 | 1 | 0 | 1 |
| Whole `re/lua-src`, including Config and Logic | 13,488 | 247 | 392 | 6 |
| Whole `re/clientB/lua-src`, including Config and Logic | 13,488 | 248 | 393 | 6 |
| `re/decomp`, recursively | 2,277 | 0 | 0 | 0 |
| All `src/Ro3Emu.Server/Protos/**/*.proto` | 147 | 44 | 42 | 0 |

A returns the auction tables, guild/challenge/league sources, localization and client protocol consumers cited here. B finds actual guild challenge/league scene descriptions, configuration and client code; those findings do not establish an emulator gameplay event producer. A search of the emulator's own sources (`league|gve|guild.*(boss|activity|event)|complet` in `Game/GuildService.cs`, `DailyActivityService.cs`, `CombatService.cs`, `TaskActions.cs`) returns ordinary daily-task completions and guild building timers, not a completed guild challenge/league event callback. Consequently the auction integration exposes a trusted configuration-backed guild event completion boundary; guild event gameplay must call it after its own authoritative result.

C returns `AuctionConfig.json`, `Localization_en.json`, `PublicParamConfig.json`, `loc_en.tsv`, both trees' auction config wrappers/data, localization, and two UI uses of `WorldAuctionNumber`. It establishes the dungeon random-unsold rule and the existence of basic-income/world limits, but does not establish a guild-to-world resale transition or a buyback formula. See the World and Event section below: **(inferred)** an unbid guild lot moves on to the World Auction (still without inventing payment from an unbid lot).

The A comparison also identifies a client-version difference: only `re/clientB/lua-src/Logic/GuildNew/LC_GuildEventHelper.lua` contains guild event type 14 (`GuildAuction`) and its winner-name/item-ID formatter. This is positive clientB evidence, not a reason to treat the earlier extraction as authoritative for guild auction event text.

Further targeted positive searches:

- `auction|auto.bid|bidder|bid price` in `data/loc/loc_en.tsv`: bid, escrow/refund, mail, eligibility, pre-purchase and dungeon random-unsold rules cited above.
- `_iGuildAward|_iPartAward|GetGuildAward|GetPartAward` across `re/lua-src/Config/CFG`, `re/lua-src/Logic` and `re/lua-src/UI/Dungeon`: dungeon UI separates auction/public loot from first-pass loot as cited above.
- `AuctionExtendTime|GamePlayAuctionDividend|GamePlayAuctionAwaitTime|preBidding|GamePlayAuctionTime` across all `re/lua-src/UI`, `Logic/Auction` and `Logic/Asset/LC_DAL_Auction.lua`: dungeon countdown and pre-bidding consumers. Numeric constants were separately read from parsed `PublicParamConfig.json`.
- `Decode|decode|default_array|GetDefault|Default` in `Network/NW_GameServerHandler.lua`, `Network/NW_Helper.lua` and decompiled network code: explicit Lua decoder options cited above.

## Authoritative reward publication

Dungeon publication runs from `DungeonService.SettleAsync`, after each participant's personal reward and remaining allowance have committed. The same transaction journals that participant's eligibility. Once all participants have been attempted, the journal becomes ready, rolls the configured group once, and commits its lots and finalized marker together. Startup recovers interrupted journals. The actual rolled lots also fill `SyncMultiDungeonAwardNotify.autionAward`; preview items do not become grants.

The trusted guild interfaces are `PublishGuildChallengeAsync(completionKey, guildId, challengeId, stage, participants)` and `PublishGuildLeagueAsync(completionKey, guildId, rank, won, participants)`. They derive awards from configuration, validate membership, deduplicate participants before count thresholds, and commit a unique completion identity with the created lots. Repeating a completion cannot publish again. Challenge activity 125 resolves through `AuctionTabConfig` to source 1003; league activities 109-111 resolve to source 1002. They use `SeasonService.PlayBook`, including the challenge's `_kMultiverseArray` gate. The gameplay-owner integration boundary and its searches are described above.

Positive config checks: challenge 1001 stage 1 selects group 351300 and award 100351300, whose pools vary with `_kServerOpenDay`. Its stage-0 aggregate award 100160102 supplies a preview; that preview is not rolled. Challenge 1002 stage 1 selects group 3 / award 1003, an explicitly empty configured award. An empty configured result is recorded as a completed source without minting lots. League rank 1 chooses groups 321110/321120/321130 at distinct participant thresholds 20/41/61 for a win.

`RewardService.RollAuctionAwardBound` reads both fixed `_kAward` tuples and `_iDropPool`, including item binding. The existing pool-only API remains unchanged for `DailyActivityService`, which already grants fixed tuples itself. A parsed traversal of `_kRewardStageAuction`, both league auction-tier lists and all dungeon `_iGuildAward` tiers through season 101/default groups, every award variant, `_iDropPool` and `_iDropGroup_*` reached 105 positive group IDs (plus the no-award value 0), 173 pools, 18,120 leaf rows and 330 distinct item IDs. All 330 have `AuctionConfig` pricing; all examined leaf rows have binding 0 and no job filter. The runtime validates rolled items and pricing before publication instead of silently discarding a grant or assigning shared loot to an arbitrary participant.

## Protocol and persistence

Requests: 82000/82001, 82004-82007, 82009-82011, 82014, 82016-82019. Notifications: 82002, 82012, 82013, 82015 and 82020. Dataset 5063 is loaded at login and updated after personal auction changes. The route list follows `data/msgtable.tsv` and `MG_Auction.proto`; all are included in evidence scan A.

Durable storage covers lots, dungeon publication journals, guild completion identities, queued pre-purchases and dividend delivery. Bid escrow uses the character owner's exchange transaction. Auction state, refunds, winning items, surplus currency and dividends commit in the same SQLite transaction. Auction transactions acquire the shared mailbox gate once for all recipients; market deliveries, ordinary mail and claims use that same gate. Distinct lot revisions identify repeated outbid/refund cycles even within one second. Mail claiming remains under `MailService` and retains binding.

A lot's JSON state is the source of truth. `auction_item` also keeps indexed copies of the fields reads filter on (`settled`, `auction_type`, `run_id`, `end_time`), and every write of the state rewrites them in the same statement (`AuctionItemColumns`). The once-a-second sweep, the market, counts and prebuys read only unsettled lots; history reads settled lots of one type, Self one type, Get the requested ids and a dungeon run its own lots. Each read joins the lot's entity revision instead of querying it per row. Settled lots are never deleted, so history and Self still grow with the server's age.

Mail uses one `Letter` and one transactional claim for bound configured attachments, exact item snapshots and 64-bit currency. The canonical persistence representation is protobuf `LanguageData` serialized as JSON base64 (inferred server storage choice). Loading accepts legacy market `string[]` parameters and auction base64 parameters; the next mailbox write uses the canonical form, preserving typed integer/string placeholders. Client evidence is `LC_DAL_Mail.lua:1799-1824`. A multi-recipient auction takes the shared mailbox gate once, avoiding recursive semaphore acquisition. Capacity validation respects binding and aggregates all currency sources before mutation. Item-only update paths reject exact/currency grant arguments instead of silently ignoring them.

Guild kicks save membership and all removed players' personal guild blobs in one transaction, then update online views and revoke chat before the first network await, preserving the response/notification order. This keeps guild membership consistent with auction dividend eligibility.

## Inferred policies

The client proves the interfaces and rules cited above; these server policy choices are explicitly inferred:

- UTC timestamps and deadline boundaries; guild exhibit/duration constants are interpreted as minutes, while dungeon item duration, public wait and bid countdown/extension constants are seconds.
- Earlier automatic bids win equal maximums; proxy competition raises the displayed price by the configured increment without exceeding the winning reservation.
- A losing live challenge is not debited. Increasing one's own automatic limit reserves only the difference and preserves the displayed price. Queued public reservations are paid upfront, including a later losing reservation, and are refunded by mail.
- Pre-purchase ordering uses durable order identity and lot identity to break equal-price ties.
- Dividend shares are equal among eligible recipients, with integer remainder assigned in ascending role ID. Dungeon distribution uses the configured 8000/10000 fraction; guild distribution uses the full realized price.
- Dungeon unsold selection is uniform over eligible participants, including those who passed. Unbid guild lots move on to the World Auction while the item is under `_iWorldAuctionCeiling`; otherwise, and for unbid World/Event lots, they finish with winner 0 and no invented proceeds.
- Auction financial attachment mails do not expire, preserving escrowed assets until claim.
- `_kServerOpenDay` ranges select the first inclusive matching award variant, using the existing server opening-day calculation.
- A committed first-clear reward or remaining reward allowance qualifies a dungeon participant; mixed-guild dungeon groups keep guild ID 0 while all committed eligible members retain their dungeon dividend rights.

## Tests

```powershell
dotnet test tests/Ro3Emu.Tests --filter 'FullyQualifiedName~AuctionTests'
```

`AuctionLotReadTests` covers the filtered reads and the backfill of a database from before the columns. `AuctionTests` (including `AuctionTests.World.cs`) covers TCP/protobuf queries and refusals; public/manual/automatic bids; equal maxima and same-second refunds; increasing one's own cap; incremental and partially failed batch escrow; lowest-price selection at the correct start time; final pass and all-pass settlement; offline/restart pause and explicit resume; 80% dungeon dividends and atomic mail claim; guild membership, participant dividends, duplicate completion, season gates and award-day boundaries. Three-player authoritative boss completion produces queryable auction rows. SQLite fault injection verifies wallet/auction/mail rollback, retry after publication failure, delivery of the already committed personal dungeon result, and retry after a failed ready-journal write. Deliberate SQLite exceptions in the test logs are these fault-injection cases.

Mail tests cover both persisted parameter formats, typed wire data, restart replay, incompatible bound-stack capacity, aggregate currency overflow, and a single mixed letter whose bound item, exact gear, 4-billion currency grant and claim flag roll back together on an injected mailbox write failure before a successful retry. A guild test aborts the second role write of a batch kick with a SQLite trigger, verifies rollback of both membership rows/blobs and unchanged session/chat state, then verifies a successful retry. A notification failure after commit is not covered by fault injection; chat revocation before network awaits follows from code ordering and the response-boundary assertion.

## World and Event auctions

Code: `src/Ro3Emu.Server/Game/AuctionService.World.cs` (its summary cites the client evidence line by line). Tests: `tests/Ro3Emu.Tests/AuctionTests.World.cs`.

- **Client contract.** The World tab lists item-type sub-tabs from the 82019 `itemTypeCount` and queries with srcType 0 plus `auctionItemTypes`; Event sub-tabs 3001-3003 need their id in `srcTypeCount` and their `_KTypeParam` activity (10000001) in `LC_ActManager`. Bidding uses the guild pricing fields. World history is capped at `WorldAuctionNumber` (300).
- **(inferred) World supply.** An unbid guild lot is re-listed in place as a World lot (same row, escrow and source key; srcType 2, the World tab, which the record cell labels "World Auction"; a World request's srcType 0 means any; guild and participants kept) while fewer than `_iWorldAuctionCeiling` lots of the item are open. It shows for `WorldAuctionExhibit` minutes and ends at the next `WorldAuctionEnd` o'clock (local). Participants still in the guild share the whole price (mail 1054, FunctionConfig 402). The original server's DB shapes (`DBAuctionGoodRecord.guildGenerateCount/systemGenerateCount`) also show system-generated World goods; their item choice is unrecoverable, so only the GM aid creates them.
- **(inferred) Event supply.** `PublishEventAuctionAsync` is a trusted boundary for an event owner (loot, participants, completion key), open only while the tab's activity window is (TimedEventService; under OpenContent an expired absolute window repeats with its own length). Lots use the guild exhibit/duration minutes; participants share the game-mode fraction `GamePlayAuctionDividend` (mail 1054, FunctionConfig 4). No gameplay producer for North-South Brown King, Space Rift or Poring Chest exists.
- **Letters.** Award 1042/1045 carry integers (price, currency, tab, item); World names tab 2. Outbid 1041 carries (item, auction type) for every non-dungeon row, matching `LC_DAL_Mail._decodeBackSelfAuction`.
- **GM aids.** `!worldauction open [itemId...]` and `!worldauction close`; `!actauction <3001|3002|3003> open [itemId...]` and `!actauction <tab> close` (forces activity 10000001 for 90 minutes, in memory).
