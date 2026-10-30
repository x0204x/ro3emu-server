# Gift codes

Players redeem a gift code on the client's **Redemption Code** screen (Settings, `SystemSettingConfig` 103001). An
operator creates the codes on the server's [web panel](../guide/panel.md) (Gift codes), which writes them through the
server's writer with an audit row, or with an external tool that writes the server's database. The server reads them,
checks them, and sends the items to the player's mailbox once.

## Client contract (from the client)

- The screen (`UI_Com_RedeemcodeContentPanel`) calls `LC_RedemptionCodeManager.UseGiftCard`, which posts JSON to
  `GMTUrl() + "/gift-card/use/v2"` with the header `GameCode: ro3`.
- The body has these fields: `gift_card_code`, `game_code`, `platform_code`, `group_id`, `open_id`, `role_id`,
  `language`, `big_area`, `account_id` and `account_token`.
- `account_id` and `account_token` are the micro-gate login token the client holds. `NW_CBBNetworkManager` reads
  `HappyEngineClient.GetLoginToken()` and passes its `Username` and `Sign`. `Username` is the account name, and `Sign`
  is the engine sign that `LoginService.verifySdk` issues (`AccountStore.IssueEngineSignAsync`, kind `engine`, valid
  for a day).
- The answer must be JSON `{ "code": <number>, "message": <string> }`.
  - `code` 1959 (`GiftCardUseSuccess`) or 1960 means success. Any other code is a refusal.
  - The client shows `message` as is in both cases, so the texts below come from the server.
- `ClientPatcher` points the request at the server. It rewrites the GMT origin inside `GMT.GMTManager`, and the
  redemption URL that `Obf_dI.RecoveryRedemption_IsAllowedPost` allows, to the server's base URL.

## Server endpoint

`POST /gift-card/use/v2` on the HTTP port.

- It is public and rate limited: `Ro3Emu:GiftCodeAttemptsPerMinute` attempts per remote address (default 10), and the
  same number of authenticated attempts per account. A request whose token does not match its `account_id` only spends
  the address budget, so nobody can use up another account's attempts. Loopback is exempt from the address budget unless
  `LoginLimitIncludesLoopback` is set.
- The body may be at most 8 KB.
- Every answer is HTTP 200 with the JSON above. The client treats any other response as a network error.

A request is accepted only when all of these hold:

1. `account_token` resolves to a live engine sign of the account named by `account_id`.
2. `role_id` is a role of that account that has not been deleted.
3. The code exists, its number columns hold numbers, it is enabled and it is within its dates.
4. The code is not used up, overall or for this account.
5. Every item entry exists in `ItemConfig`.

The items then go by mail. The redemption row and the mail are written in one transaction, so a code is never
granted twice. Inside that transaction the code and its counts are read again, so a code disabled or used up in the
meantime rolls the mail back.

| code | constant | message |
|---|---|---|
| 1959 | `GiftCardUseSuccess` | Redeemed. The rewards were sent to your mailbox. |
| 1961 | `GiftCardBadRequest` | Invalid request. |
| 1962 | `GiftCardBadCredentials` | Your login is no longer valid. Please log in again. |
| 1963 | `GiftCardRoleNotOwned` | This character does not belong to your account. |
| 1964 | `GiftCardUnknown` | Invalid code. |
| 1965 | `GiftCardDisabled` | This code is no longer active. |
| 1966 | `GiftCardNotStarted` | This code is not active yet. |
| 1967 | `GiftCardExpired` | This code has expired. |
| 1968 | `GiftCardExhausted` | This code has been fully redeemed. |
| 1969 | `GiftCardAlreadyRedeemed` | You have already redeemed this code. |
| 1970 | `GiftCardInvalidItems` | This code cannot be redeemed right now. |
| 1971 | `GiftCardRateLimited` | (too many attempts) |
| 1972 | `GiftCardFailed` | (internal failure) |

The mail's title is "Gift code reward" and its body is "Here are the rewards of gift code \<code\>." It expires after
the default 30 days for raw mail with an attachment.

## Database contract for the admin panel

The server creates both tables (`GiftCodeSchemaPlan`).

**The panel writes only `gift_code`.** The server reads it fresh on every redemption. Its own web panel writes it
through the server (`GiftCodeCreatePlan`, `GiftCodeDisablePlan`); an external tool may write it directly.

| column | type | meaning |
|---|---|---|
| `code` | TEXT, primary key, `COLLATE NOCASE`, 1 to 64 characters, no surrounding spaces | the code players type; matched trimmed and case-insensitively (ASCII) |
| `items` | TEXT | a JSON array of `[itemEntry, count]` pairs, e.g. `[[10001,5],[20003,1]]`; not empty, each entry an `ItemConfig` id, each count above 0 |
| `max_uses` | INTEGER, default 0 | total redemptions allowed across all accounts; 0 means unlimited |
| `per_account` | INTEGER, default 1, at least 1 | redemptions allowed per account |
| `starts_utc` | INTEGER, nullable | unix seconds; the code works from this time on |
| `expires_utc` | INTEGER, nullable | unix seconds; the code stops working at this time |
| `disabled` | INTEGER 0 or 1, default 0 | 1 turns the code off without deleting it |
| `created_by` | TEXT, nullable | free text for the panel (e.g. the admin's name) |
| `created_utc` | INTEGER, default now | unix seconds |
| `note` | TEXT, nullable | free text for the panel |

**The server alone writes `gift_code_redemption`.** The panel may read it, for example to show how many times a code
was used, but must never write it.

| column | meaning |
|---|---|
| `redemption_key` | primary key: `giftcode:<length of code>:<code>:<use number>:<account>` (use number 1, 2, … per account); also the mail's delivery key. The code is length-delimited and the account comes last, so a `:` in a code or an account name cannot make two keys equal |
| `code` | the code as stored in `gift_code` |
| `account` | the account name |
| `role_id` | the role that received the mail |
| `redeemed_utc` | unix seconds |

Uses are counted from these rows, never from a counter in `gift_code`.

## Inferred

The client does not establish any of these:

- the refusal codes 1961-1972, all the messages, and the mail's title and body;
- the trimmed, case-insensitive match, and the 64-character limit;
- the start and expiry boundaries (start inclusive, expiry exclusive);
- answering a repeated request for a use already granted with 1969 instead of 1959 (two identical requests that
  arrive at the same moment may both answer 1959; the items are still granted once);
- refusing a code (1970) whose number columns hold something other than a number, instead of reading it as 0;
- not checking `game_code` and the `GameCode` header, and accepting a body without a JSON content type;
- the rate limit and the body size limit.
