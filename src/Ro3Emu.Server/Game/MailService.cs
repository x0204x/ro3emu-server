using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Current mailbox (LC_DAL_Mail.lua): micro list/content/read/delete and game 31602 claim.
/// One durable per-role blob is read for every operation. Converted plans reserve recipients together
/// with their economic source. The mailbox gate remains only for unowned compatibility callers.
/// </summary>
public sealed partial class MailService(AccountStore accounts, RoleStore roles, SessionRegistry sessions, MicroSessions micro, CharacterService character,
    SeasonService seasons, GameData data, TimeProvider clock, ILogger<MailService> log, GameLoop? loop = null) : IMicroService
{
    public const uint ClaimMail = 31602;
    public string Name => "MailService";

    private readonly SemaphoreSlim gate = new(1, 1);
    private SemaphoreSlim Gate(long _) => gate;
    private async Task<IDisposable> EnterMailboxAsync(long role)
    {
        if (loop?.IsOnLoop == true && loop.Owners?.Current is { RuntimeOnly: false } context)
        {
            context.AssertRole(role);
            return new RecipientLock(null);
        }
        await gate.WaitAsync();
        return new RecipientLock(gate);
    }
    internal Task EnterTransactionAsync() => gate.WaitAsync();
    internal void LeaveTransaction() => gate.Release();
    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    public sealed class Mailbox
    {
        public long NextId { get; set; }
        public List<Letter> Letters { get; set; } = new();
    }

    public sealed class Letter
    {
        public string Id { get; set; } = "";
        public int Category { get; set; } = 1;
        public long CreatedTime { get; set; }
        public long RemoveTime { get; set; }
        public string Title { get; set; } = "";
        public string Content { get; set; } = "";
        public string Abstract { get; set; } = "";
        public string Sender { get; set; } = "RO3";
        public string SenderId { get; set; } = "0";
        public int TemplateId { get; set; }
        public int ImportantType { get; set; }
        public string DeliveryKey { get; set; } = "";
        // Read-only compatibility with the previous source-owned once-only mail rows.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SourceKey { get; set; }
        // Canonical persistence is protobuf bytes (JSON base64). Old market mail stored string[].
        [JsonConverter(typeof(LanguageDataBytesConverter))]
        public byte[]? ContentParams { get; set; }
        public bool Read { get; set; }
        public bool Claimed { get; set; }
        public List<Attachment> Attachments { get; set; } = new();
        public List<InvItem> Items { get; set; } = new();
        public Dictionary<int, long> Currency { get; set; } = new();
    }

    /// <summary>Configured entry/count/binding grants, including virtual Base/Job EXP 100/101; market instances use Items.</summary>
    public sealed record Attachment(int Entry, int Count, int Bind = 0);

    private sealed class LanguageDataBytesConverter : JsonConverter<byte[]?>
    {
        public LanguageDataBytesConverter() { }

        public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType == JsonTokenType.String) return reader.GetBytesFromBase64();
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Invalid mail content parameters.");
            var strings = new List<object>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String) throw new JsonException("Invalid legacy mail content parameter.");
                strings.Add(reader.GetString()!);
            }
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Unterminated mail content parameters.");
            return EncodeLanguageData(strings.ToArray());
        }

        public override void Write(Utf8JsonWriter writer, byte[]? value, JsonSerializerOptions options)
        {
            if (value == null) writer.WriteNullValue();
            else writer.WriteBase64StringValue(value);
        }
    }

    private static Mailbox DecodeMailbox(byte[]? saved)
    {
        var box = saved is { Length: > 0 }
            ? JsonSerializer.Deserialize<Mailbox>(saved) ?? new Mailbox() : new Mailbox();
        foreach (var letter in box.Letters)
        {
            if (letter.DeliveryKey.Length == 0 && letter.SourceKey is { Length: > 0 })
                letter.DeliveryKey = letter.SourceKey;
            letter.SourceKey = null;
        }
        return box;
    }

    private Mailbox CaptureMailbox(long role, RoleItemPlanBuilder transaction) =>
        DecodeMailbox(transaction.ReadRoleBlob(accounts, role, "mailbox"));

    private Task CommitMailboxAsync(long role, Mailbox box, RoleItemPlanBuilder transaction, string label)
    {
        transaction.RoleBlob(role, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        transaction.WithEscrowEconomy(new EscrowEconomyIntent(label,
            transferReason: EconomyReason.MailClaim).Evidence(new { Role = role, Label = label }));
        return roles.CommitAsync(transaction, label);
    }

    private EconomyAsset MailAsset(int entry)
    {
        if (data.Row("ItemConfig", entry) is not { } row)
            throw new InvalidOperationException($"Mail asset {entry} is not configured.");
        return new EconomyAsset(GameData.Int(row, "_iType") == 1
            ? EconomyAssetKind.Currency : EconomyAssetKind.Item, entry);
    }

    private void AuthorizeMailExpiry(long role, RoleItemPlanBuilder transaction, IEnumerable<Letter> expired)
    {
        var owner = new EconomyOwner(EconomyOwnerKind.Role, role.ToString(CultureInfo.InvariantCulture));
        foreach (var letter in expired.Where(m => !m.Claimed))
        {
            foreach (var attachment in letter.Attachments.Where(a => a.Entry is not (100 or 101)))
                transaction.EscrowSink(MailAsset(attachment.Entry), attachment.Count, EconomyReason.MailExpiry,
                    EconomyCompartment.Mail, owner);
            foreach (var item in letter.Items)
                transaction.EscrowSink(MailAsset(item.Entry), item.Stack, EconomyReason.MailExpiry,
                    EconomyCompartment.Mail, owner);
            foreach (var (entry, amount) in letter.Currency)
                transaction.EscrowSink(MailAsset(entry), amount, EconomyReason.MailExpiry,
                    EconomyCompartment.Mail, owner);
        }
    }

    private IReadOnlyList<EconomyEffect> ExpiredMailEffects(long role, IEnumerable<Letter> expired)
    {
        var owner = new EconomyOwner(EconomyOwnerKind.Role, role.ToString(CultureInfo.InvariantCulture));
        var effects = new List<EconomyEffect>();
        foreach (var letter in expired.Where(m => !m.Claimed))
        {
            foreach (var attachment in letter.Attachments.Where(a => a.Entry is not (100 or 101)))
                effects.Add(new(MailAsset(attachment.Entry), owner, EconomyCompartment.Mail,
                    -attachment.Count, EconomyLegKind.Sink, EconomyReason.MailExpiry));
            foreach (var item in letter.Items)
                effects.Add(new(MailAsset(item.Entry), owner, EconomyCompartment.Mail,
                    -item.Stack, EconomyLegKind.Sink, EconomyReason.MailExpiry));
            foreach (var (entry, amount) in letter.Currency)
                effects.Add(new(MailAsset(entry), owner, EconomyCompartment.Mail,
                    -amount, EconomyLegKind.Sink, EconomyReason.MailExpiry));
        }
        return effects;
    }

    private bool Purge(Mailbox box, long now, RoleItemPlanBuilder? transaction = null, long role = 0)
    {
        var expired = box.Letters.Where(m => m.RemoveTime != 0 && now >= m.RemoveTime).ToArray();
        if (transaction != null)
        {
            if (role <= 0) throw new ArgumentOutOfRangeException(nameof(role));
            AuthorizeMailExpiry(role, transaction, expired);
        }
        if (expired.Length == 0) return false;
        box.Letters.RemoveAll(m => m.RemoveTime != 0 && now >= m.RemoveTime);
        return true;
    }

    private static string? DeliveryEconomySourceKey(long role, string deliveryKey)
    {
        if (deliveryKey.Length == 0) return null;
        string prefix = $"mail/deliver/{role}/";
        return prefix.Length + deliveryKey.Length <= 512 ? prefix + deliveryKey
            : prefix + "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deliveryKey)));
    }

    /// <summary>Hold the shared mailbox gate across an external atomic plan (auction escrow/settlement).</summary>
    internal async Task<IDisposable> LockRecipientsAsync(IEnumerable<long> recipients)
    {
        if (!recipients.Any()) return new RecipientLock(null);
        await gate.WaitAsync();
        return new RecipientLock(gate);
    }

    private sealed class RecipientLock(SemaphoreSlim? held) : IDisposable
    {
        public void Dispose() => held?.Release();
    }

    /// <summary>
    /// An idempotent financial letter written with the auction row and currency debit. Caller holds the recipient gate.
    /// (inferred) Financial attachments never expire, since expiry would destroy reserved player assets.
    /// </summary>
    internal void QueueTemplateLocked(long role, string key, int templateId, IReadOnlyList<Attachment> attachments,
        RoleItemPlanBuilder transaction, params object[] contentParams)
    {
        if (roles.Get(role) == null || data.Row("MailConfig", templateId) == null
            || attachments.Any(a => a.Entry is 100 or 101 || a.Count <= 0 || data.Row("ItemConfig", a.Entry) == null))
            throw new InvalidOperationException("Invalid auction mail recipient, template or attachment.");
        var now = clock.GetUtcNow();
        var letter = new Letter
        {
            DeliveryKey = key, CreatedTime = now.ToUnixTimeSeconds(), RemoveTime = 0,
            TemplateId = templateId, Attachments = attachments.ToList(),
            ContentParams = EncodeLanguageData(contentParams),
        };
        transaction.Add(new FinancialMailAppendPlan(role, key, letter, now.UtcTicks));
    }

    // Award letters (1042/1045) written before 11.10 carry the currency, tab and item as strings; the client decoder
    // reads integers and logs a Lua error per letter on every mail sync. Without parameters it shows the bare text.
    internal static bool LegacyAwardParams(Letter mail) => mail.TemplateId is 1042 or 1045
        && LanguageData.Parser.ParseFrom(mail.ContentParams).Param.Any(p => p.Tp != LanguageParamType.Int);

    private static byte[]? EncodeLanguageData(object[] values)
    {
        if (values.Length == 0) return null;
        var data = new LanguageData();
        foreach (var value in values)
        {
            switch (value)
            {
                case string str: data.Param.Add(new LanguageParam { Tp = LanguageParamType.Str, StrParam = str }); break;
                case int number: data.Param.Add(new LanguageParam { Tp = LanguageParamType.Int, IntParam = number }); break;
                case long number: data.Param.Add(new LanguageParam { Tp = LanguageParamType.Int, IntParam = number }); break;
                default: throw new ArgumentException("Auction mail localization parameters must be string or integer.");
            }
        }
        return data.ToByteArray();
    }

    internal Task NotifyRecipientAsync(long role) => NotifyAsync(role, 1);

    private static readonly HashSet<int> MarketTemplates = [1027, 1028, 1029, 1030, 1031, 1032, 1063, 1064, 1065, 1066, 1067];

    /// <summary>Caller-side market capture; the builder guards the original mailbox and stages the new blob.</summary>
    internal string DeliverMarket(RoleItemPlanBuilder transaction, DateTimeOffset now, long role, int templateId,
        IEnumerable<InvItem>? items, IReadOnlyDictionary<int, long>? currency, string[] contentParams)
    {
        if (role <= 0 || !MarketTemplates.Contains(templateId) || data.Row("MailConfig", templateId) == null)
            throw new ArgumentOutOfRangeException(nameof(templateId));
        var attached = items?.Select(i => i.Clone()).ToList() ?? [];
        if (attached.Any(i => i.Entry is 100 or 101 || i.Entry <= 0 || i.Stack <= 0 || i.Grid != 0
            || data.Row("ItemConfig", i.Entry) is not { } row || GameData.Int(row, "_iType") == 1))
            throw new ArgumentException("Invalid mail item", nameof(items));
        var amounts = currency?.ToDictionary(p => p.Key, p => p.Value) ?? new();
        if (amounts.Any(p => p.Key <= 0 || p.Value <= 0 || data.Row("ItemConfig", p.Key) is not { } row
            || GameData.Int(row, "_iType") != 1))
            throw new ArgumentException("Invalid mail currency", nameof(currency));
        var box = CaptureMailbox(role, transaction);
        Purge(box, now.ToUnixTimeSeconds(), transaction, role);
        string id = NextId(box, now);
        box.Letters.Add(new Letter
        {
            Id = id, TemplateId = templateId, Sender = "", CreatedTime = now.ToUnixTimeSeconds(),
            RemoveTime = attached.Count > 0 || amounts.Count > 0 ? 0 : now.AddDays(30).ToUnixTimeSeconds(),
            Items = attached, Currency = amounts, ContentParams = EncodeLanguageData(contentParams.Cast<object>().ToArray()),
        });
        transaction.RoleBlob(role, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        return id;
    }

    private static string NextId(Mailbox box, DateTimeOffset now)
    {
        box.NextId = Math.Max(box.NextId + 1, now.UtcTicks);
        return box.NextId.ToString("D18", CultureInfo.InvariantCulture);
    }

    /// <summary>Translate a guarded legacy market-mail snapshot into the same atomic builder as its row deletion.</summary>
    internal void EnlistLegacyMigration(RoleItemPlanBuilder transaction, IReadOnlyList<(string Id, string Data)> rows)
    {
        var decoded = rows.OrderBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => (r.Id, Row: JsonSerializer.Deserialize<LegacyMarketMailRow>(r.Data)!)).ToArray();
        foreach (var group in decoded.GroupBy(r => r.Row.RoleId))
        {
            var box = CaptureMailbox(group.Key, transaction);
            foreach (var (key, row) in group)
            {
                string id = row.Id.Length > 0 ? row.Id : key;
                while (box.Letters.Any(m => m.Id == id)) id = NextId(box, DateTimeOffset.FromUnixTimeSeconds(row.CreatedTime));
                if (long.TryParse(id, CultureInfo.InvariantCulture, out long numeric)) box.NextId = Math.Max(box.NextId, numeric);
                box.Letters.Add(new Letter
                {
                    Id = id, TemplateId = row.TemplateId, Sender = "", CreatedTime = row.CreatedTime,
                    RemoveTime = row.Items.Count > 0 || row.Currency.Count > 0 ? 0 : row.RemoveTime,
                    Read = row.Read, Claimed = row.Claimed, Items = row.Items.Select(i => i.Clone()).ToList(),
                    Currency = new(row.Currency), ContentParams = EncodeLanguageData(row.ContentParams.Cast<object>().ToArray()),
                });
            }
            transaction.RoleBlob(group.Key, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        }
    }

    private sealed class LegacyMarketMailRow
    {
        public string Id { get; set; } = "";
        public long RoleId { get; set; }
        public int TemplateId { get; set; }
        public long CreatedTime { get; set; }
        public long RemoveTime { get; set; }
        public bool Read { get; set; }
        public bool Claimed { get; set; }
        public List<InvItem> Items { get; set; } = [];
        public Dictionary<int, long> Currency { get; set; } = new();
        public string[] ContentParams { get; set; } = [];
    }

    internal Task NotifyMarketAsync(long role) => NotifyAsync(role, 1);

    private async Task NotifyAsync(long role, int category)
    {
        var account = sessions.All().FirstOrDefault(s => s.Role?.Id == role)?.Account;
        if (account != null) await micro.PushAsync(account, Name,
            category == 3 ? "newGroupMailNotice" : "newMailNotice", new Dictionary<string, object?>());
    }

    /// <summary>
    /// Server delivery to an existing role, online or offline. (inferred) Raw system mail defaults to
    /// PublicParamConfig.MailTextTimeMax=7 or MailAttachmentTimeMax=30 days,
    /// and Unix seconds are used for CreatedTime/RemoveTime (UI_Sys_MailListWidgetCtrl compares system time).
    /// Set expiresAt explicitly to choose another boundary; DateTimeOffset.MaxValue means no expiry.
    /// </summary>
    public Task<string> SendRawAsync(long role, string title, string content, IReadOnlyList<Attachment>? attachments = null,
        string sender = "RO3", DateTimeOffset? expiresAt = null, int category = 1, int importantType = 0) =>
        DeliverAsync(role, new Letter { Title = title, Content = content, Sender = sender, Category = category,
            ImportantType = importantType, Attachments = attachments?.ToList() ?? new() }, expiresAt,
            attachments is { Count: > 0 } ? 30 : 7);

    /// <summary>
    /// A known MailConfig template with an explicit source-owned reward list. _iMailType is a template option,
    /// not the UI category. The separate overload resolves eligible fixed _iAward rows as an inferred policy.
    /// (inferred) _kTimes is retention in days for _iTimerMethod=0. Other timer modes need explicit expiry.
    /// </summary>
    public Task<string> SendTemplateAsync(long role, int templateId, IReadOnlyList<Attachment> attachments,
        DateTimeOffset? expiresAt = null, int category = 1, int importantType = 0)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (data.Row("MailConfig", templateId) is not { } template)
            throw new ArgumentException($"Unknown MailConfig {templateId}.", nameof(templateId));
        if (GameData.Int(template, "_iTimerMethod") != 0 && expiresAt == null)
            throw new ArgumentException("This MailConfig timer mode requires an explicit expiry.", nameof(expiresAt));
        int days = (int)GameData.Int(template, "_kTimes");
        return DeliverAsync(role, new Letter { TemplateId = templateId, SenderId = "0", Category = category,
            ImportantType = importantType, Attachments = attachments.ToList() }, expiresAt, days > 0 ? days : 30);
    }

    /// <summary>
    /// Resolve MailConfig._iAward through MultiverseGroupConfig and its selected fixed AwardConfig triples.
    /// (inferred) The client only previews this field; the actual original-server mail creation is not captured.
    /// Conditional, pooled, restricted, expiring and bound awards need their own source decision and are refused.
    /// </summary>
    public Task<string> SendTemplateAsync(long role, int templateId, DateTimeOffset? expiresAt = null,
        int category = 1, int importantType = 0) =>
        SendTemplateAsync(role, templateId, ResolveTemplateAward(templateId), expiresAt, category, importantType);

    /// <summary>Source-owned once-only delivery with the source's state saved in the same SQLite transaction.</summary>
    public Task<string> SendTemplateOnceAsync(long role, int templateId, IReadOnlyList<Attachment> attachments,
        string sourceKey, Action<RoleItemPlanBuilder> persist, Ownership.OwnerContext? ownerContext = null,
        Action<EscrowEconomyIntent>? authorize = null,
        Func<IReadOnlyList<EconomyEffect>, EconomyEnvelope>? parentEconomy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentNullException.ThrowIfNull(persist);
        if (data.Row("MailConfig", templateId) is not { } template)
            throw new ArgumentException($"Unknown MailConfig {templateId}.", nameof(templateId));
        if (GameData.Int(template, "_iTimerMethod") != 0)
            throw new AwardResolutionException($"Template {templateId} requires a timer policy");
        int days = (int)GameData.Int(template, "_kTimes");
        return DeliverAsync(role, new Letter { TemplateId = templateId, SenderId = "0", DeliveryKey = sourceKey,
            Attachments = attachments.ToList() }, null, days > 0 ? days : 30, persist, ownerContext, authorize,
            parentEconomy);
    }

    /// <summary>A template's fixed _iAward grants (as <see cref="SendTemplateAsync(long, int, DateTimeOffset?, int, int)"/>
    /// resolves them), for a source that delivers the letter once with <see cref="SendTemplateOnceAsync"/>.</summary>
    internal IReadOnlyList<Attachment> TemplateAward(int templateId) => ResolveTemplateAward(templateId);

    private IReadOnlyList<Attachment> ResolveTemplateAward(int templateId)
    {
        var template = data.Row("MailConfig", templateId)
            ?? throw new ArgumentException($"Unknown MailConfig {templateId}.", nameof(templateId));
        long groupId = GameData.Int(template, "_iAward");
        if (groupId == 0) return [];
        var group = data.Row("MultiverseGroupConfig", groupId)
            ?? throw new AwardResolutionException($"Missing mail award group {groupId}");
        if (GameData.Int(group, "_itype") != 0)
            throw new AwardResolutionException($"Mail award group {groupId} is not a fixed AwardConfig route");
        long id = GameData.Int(group, $"_iValue_{seasons.PlayBook}");
        if (id == 0) id = GameData.Int(group, "_iValue_default");
        if (data.Row("AwardConfig", id) is not { ValueKind: JsonValueKind.Array } rows || rows.GetArrayLength() != 1)
            throw new AwardResolutionException($"Mail award {id} has no unique AwardConfig row");
        var row = rows[0];
        if (GameData.Int(row, "_iDropPool") != 0 || HasValues(row, "_kCondition") || HasValues(row, "_kEXP")
            || data.Table("AwardRestricConfig").Values.Any(r => GameData.Int(r, "_iRewardID") == id))
            throw new AwardResolutionException($"Mail award {id} has unresolved pool, condition, EXP or restriction");
        if (!row.TryGetProperty("_kAward", out var configured) || configured.ValueKind != JsonValueKind.Array)
            throw new AwardResolutionException($"Mail award {id} has no fixed grants");
        var grants = new List<Attachment>();
        foreach (var tuple in configured.EnumerateArray())
        {
            if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() != 3
                || !tuple[0].TryGetInt32(out int entry) || !tuple[1].TryGetInt32(out int count)
                || !tuple[2].TryGetInt32(out int bind) || bind != 0 || count <= 0 || data.Row("ItemConfig", entry) == null)
                throw new AwardResolutionException($"Mail award {id} has unsupported fixed grant");
            grants.Add(new Attachment(entry, count));
        }
        return grants;
    }

    private static bool HasValues(JsonElement row, string field) => row.TryGetProperty(field, out var value) && (value.ValueKind switch
    {
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        JsonValueKind.Number => GameData.Number(value) != 0,
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        _ => true,
    });

    private bool ConfiguredAttachments(IEnumerable<Attachment> attachments) =>
        attachments.All(a => a.Count > 0 && data.Row("ItemConfig", a.Entry) != null);

    /// <summary>Add a validated letter while holding the shared mailbox gate.</summary>
    private string AddLetter(Mailbox box, Letter letter, long now, DateTimeOffset? expiresAt, int days)
    {
        // (inferred) Ticks provide sortable numeric string IDs; NextId preserves monotonicity across restart.
        letter.Id = NextId(box, clock.GetUtcNow());
        letter.CreatedTime = now;
        letter.RemoveTime = expiresAt == DateTimeOffset.MaxValue ? 0
            : expiresAt?.ToUnixTimeSeconds() ?? checked(now + (long)days * 86400);
        box.Letters.Add(letter);
        return letter.Id;
    }

    private async Task<string> DeliverAsync(long role, Letter letter, DateTimeOffset? expiresAt, int days,
        Action<RoleItemPlanBuilder>? persist = null, Ownership.OwnerContext? ownerContext = null,
        Action<EscrowEconomyIntent>? authorize = null,
        Func<IReadOnlyList<EconomyEffect>, EconomyEnvelope>? parentEconomy = null)
    {
        string id;
        if (ownerContext != null)
        {
            ownerContext.AssertRole(role);
            id = await DeliverCoreAsync(role, letter, expiresAt, days, persist, authorize, parentEconomy);
        }
        else id = loop?.Owners is { } owners ? await loop.InvokeAsync(async () =>
        {
            Ownership.ReservationKey[] keys = persist == null
                ? [Ownership.ReservationKey.Role(role)] : [Ownership.ReservationKey.Realm];
            if (owners.Current is { RuntimeOnly: false })
                return await owners.RunAsync("mail delivery/owned", keys,
                    () => DeliverCoreAsync(role, letter, expiresAt, days, persist, authorize, parentEconomy));
            while (true)
            {
                Task? predecessor;
                using (await owners.EnterAsync("mail delivery", keys))
                {
                    // An external delivery offered during socket cleanup belongs after its durable
                    // handover fence. Release our reservation before waiting for that cleanup.
                    predecessor = owners.Cached(role)?.Detaching;
                    if (predecessor == null)
                        return await DeliverCoreAsync(role, letter, expiresAt, days, persist, authorize, parentEconomy);
                }
                await predecessor;
            }
        }) : await DeliverCoreAsync(role, letter, expiresAt, days, persist, authorize, parentEconomy);
        // External delivery releases its reservation before optional transport work.
        try { await NotifyAsync(role, letter.Category); }
        catch (Exception e) { log.LogWarning(e, "Mail {Mail} notice failed after delivery", id); }
        log.LogInformation("Delivered mail {Mail} to role {Role}, template {Template}, category {Category}", id, role, letter.TemplateId, letter.Category);
        return id;
    }

    private async Task<string> DeliverCoreAsync(long role, Letter letter, DateTimeOffset? expiresAt, int days,
        Action<RoleItemPlanBuilder>? persist, Action<EscrowEconomyIntent>? authorize,
        Func<IReadOnlyList<EconomyEffect>, EconomyEnvelope>? parentEconomy)
    {
        if (await roles.GetAsync(role) == null) throw new ArgumentException("Recipient role does not exist.", nameof(role));
        if (letter.Category is not (1 or 3) || letter.ImportantType is < 0 or > 2)
            throw new ArgumentException("Unsupported mail category or importance.");
        if (!ConfiguredAttachments(letter.Attachments))
            throw new ArgumentException("Attachment needs a configured item and positive count.");
        using var mailbox = await EnterMailboxAsync(role);
        string? deliverySource = DeliveryEconomySourceKey(role, letter.DeliveryKey);
        string? deliveryHash = deliverySource == null ? null : DeliverySemanticHash(letter, expiresAt, days);
        if (deliverySource != null && await accounts.ReadAsync<string?>(
                new MailDeliveryReceiptReadPlan(deliverySource, role, letter.DeliveryKey, deliveryHash!)) is { } delivered)
            return delivered;
        string id;
        {
            var transaction = accounts.NewTransaction();
            var box = await CaptureMailboxAsync(role, transaction);
            long now = Now;
            var expired = parentEconomy == null ? null : ExpiredMailEffects(role,
                box.Letters.Where(m => m.RemoveTime != 0 && now >= m.RemoveTime));
            Purge(box, now, parentEconomy == null ? transaction : null, role);
            if (letter.DeliveryKey.Length > 0 && box.Letters.FirstOrDefault(m => m.DeliveryKey == letter.DeliveryKey) is { } existing)
                return existing.Id;
            id = AddLetter(box, letter, now, expiresAt, days);
            transaction.RoleBlob(role, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
            persist?.Invoke(transaction);
            if (parentEconomy != null)
            {
                if (authorize != null || transaction.HasEconomy)
                    throw new InvalidOperationException("Mail delivery has more than one economic family.");
                transaction.WithEconomy(parentEconomy(expired!));
                await roles.CommitAsync(transaction, "mail/deliver");
                return id;
            }
            var intent = new EscrowEconomyIntent("mail/deliver",
                sourceKey: deliverySource,
                transferReason: EconomyReason.MailDelivery);
            var reason = letter.TemplateId == 0 ? EconomyReason.GmGrant : EconomyReason.ActivityReward;
            foreach (var attachment in letter.Attachments.Where(a => a.Entry is not (100 or 101)))
                intent.Source(MailAsset(attachment.Entry), attachment.Count, reason);
            foreach (var item in letter.Items)
                intent.Source(MailAsset(item.Entry), item.Stack, reason);
            foreach (var (entry, amount) in letter.Currency)
                intent.Source(MailAsset(entry), amount, reason);
            authorize?.Invoke(intent);
            transaction.WithEscrowEconomy(intent.Evidence(new { Role = role, LetterId = id,
                letter.DeliveryKey, DeliverySemanticHash = deliveryHash,
                letter.TemplateId, letter.Category,
                Exp = letter.Attachments.Where(a => a.Entry is 100 or 101).ToArray() }));
            await roles.CommitAsync(transaction, "mail/deliver");
        }
        return id;
    }

    /// <summary>(inferred) Expired unclaimed attachments leave the durable mailbox only with MailExpiry sinks.</summary>

    /// <summary>After role select, invalidate pending rows from offline delivery.</summary>
    public async Task LoginAsync(GameSession session)
    {
        var (pending, group) = await PendingNoticeAsync(session.Role!.Id);
        if (session.Account != null && pending)
            await micro.PushAsync(session.Account, Name, group ? "newGroupMailNotice" : "newMailNotice",
                new Dictionary<string, object?>());
    }

    /// <summary>
    /// (inferred) A read mail with unclaimed attachments still needs a refresh after micro reconnect. Both list
    /// categories are invalidated by newMailNotice; no read/claim flag is changed by this check.
    /// </summary>
    private async Task<(bool Pending, bool Group)> PendingNoticeAsync(long role)
    {
        using (await EnterMailboxAsync(role))
        {
            var transaction = accounts.NewTransaction();
            var box = await CaptureMailboxAsync(role, transaction);
            if (Purge(box, Now, transaction, role)) await CommitMailboxAsync(role, box, transaction, "mail/pending-purge");
            bool Pending(Letter m) => m.Category is 1 or 3 && (!m.Read || HasAssets(m) && !m.Claimed);
            return (box.Letters.Any(Pending), box.Letters.Any(m => m.Category == 3 && Pending(m)));
        }
    }

    private async ValueTask<object?> InvokeMicroCoreAsync(MicroSession session, string method, object? args)
    {
        if (method is not ("getUserMailList" or "getMailContent" or "setMailHaveRead" or "deleteMail" or "syncGroupMail")) return null;
        var role = sessions.All().FirstOrDefault(s => s.Account != null
            && string.Equals(s.Account, session.Account, StringComparison.Ordinal))?.Role;
        if (role == null) return MicroServiceRouter.Fail("no selected role");
        string? requestedUid = method is "getUserMailList" or "deleteMail"
            ? MicroServiceRouter.MapString(args, "Uid") : (args as object?[])?.FirstOrDefault() as string;
        if (requestedUid != role.Id.ToString(CultureInfo.InvariantCulture)) return MicroServiceRouter.Fail("role mismatch");
        if (method == "syncGroupMail")
        {
            // LC_System_Mail.lua sends this after a micro reconnect while the game role can remain selected.
            // Push only after PendingNoticeAsync has released Gate; then return the exact ACK shape.
            if ((await PendingNoticeAsync(role.Id)).Pending && session.Account != null)
                await micro.PushAsync(session.Account, Name, "newMailNotice", new Dictionary<string, object?>());
            return Success();
        }
        using (await EnterMailboxAsync(role.Id))
        {
            var transaction = accounts.NewTransaction();
            var box = await CaptureMailboxAsync(role.Id, transaction);
            long now = Now;
            if (Purge(box, now, transaction, role.Id))
            {
                await CommitMailboxAsync(role.Id, box, transaction, "mail/micro-purge");
                transaction = accounts.NewTransaction();
                box = await CaptureMailboxAsync(role.Id, transaction);
            }
            if (method == "getUserMailList")
            {
                if (!TryMapInt(args, "MailType", out int category) || !TryMapInt(args, "Start", out int start)
                    || !TryMapInt(args, "Limit", out int limit)) return MicroServiceRouter.Fail("bad list range");
                if (category is < 1 or > 3 || start < 0 || limit < 0) return MicroServiceRouter.Fail("bad list range");
                // (inferred) Fixed cap and no queued overflow semantics; category 2 has no delivery route yet.
                var list = box.Letters.Where(m => m.Category == category && Active(m, now))
                    .OrderByDescending(m => m.CreatedTime).ThenByDescending(m => m.Id, StringComparer.Ordinal)
                    .Skip(start).Take(limit == 0 ? 150 : Math.Min(limit, 150))
                    .Select(Row).ToArray();
                return new Dictionary<string, object?> { ["Ret"] = MicroServiceRouter.Ret(true), ["MailList"] = list };
            }
            if (method is "getMailContent" or "setMailHaveRead")
            {
                string? id = (args as object?[])?.Skip(1).FirstOrDefault() as string;
                var mail = box.Letters.FirstOrDefault(m => m.Id == id && Active(m, now));
                if (mail == null) return MicroServiceRouter.Fail("mail missing or expired");
                if (method == "getMailContent") return new Dictionary<string, object?>
                    { ["Ret"] = MicroServiceRouter.Ret(true), ["Content"] = mail.Content };
                mail.Read = true;
                await CommitMailboxAsync(role.Id, box, transaction, "mail/micro-read");
                return Success();
            }
            if (method == "deleteMail")
            {
                var supplied = Map(args, "MailIds") as object?[];
                if (supplied == null || supplied.Length == 0 || supplied.Any(x => x is not string { Length: > 0 }))
                    return MicroServiceRouter.Fail("bad mail ids");
                var ids = supplied.Cast<string>().ToHashSet();
                var found = box.Letters.Where(m => ids.Contains(m.Id)).ToArray();
                if (found.Length != ids.Count || found.Any(m => HasAssets(m) && !m.Claimed && Active(m, now)))
                    return MicroServiceRouter.Fail("mail missing or has unclaimed items");
                box.Letters.RemoveAll(m => ids.Contains(m.Id));
                await CommitMailboxAsync(role.Id, box, transaction, "mail/micro-delete");
                return Success();
            }
            return null;
        }
    }

    /// <summary>31602: each distinct eligible id is one item/currency/EXP grant plus one claimed flag, in one SQLite transaction.</summary>
    public async Task ClaimAsync(GameSession session, ClaimMailReq request)
    {
        var response = new ClaimMailRsp();
        Ownership.OwnerNotices? notices = null;
        if (session.Role == null || session.Progress == null || request.MailUid.Count == 0)
        {
            response.Result = ErrorCodes.Parameter;
            await session.SendGameAsync(ClaimMail, response);
            return;
        }
        using (await CharacterService.Lock(session))
        {
            using var capture = session.Owners?.Current is { } context ? notices = new(context) : null;
            long role = session.Role.Id;
            using (await EnterMailboxAsync(role))
            {
                var source = accounts.ReadSync<RoleBlobSnapshot>(new RoleBlobSnapshotRead(role, "mailbox"));
                var box = DecodeMailbox(source.Value);
                long now = Now;
                var expiredIds = box.Letters.Where(m => m.RemoveTime != 0 && now >= m.RemoveTime)
                    .Select(m => m.Id).ToHashSet();
                var purge = accounts.NewTransaction();
                if (Purge(box, now, purge, role))
                {
                    purge.Guard(new RoleBlobGuardPlan(role, "mailbox", source));
                    byte[] purged = JsonSerializer.SerializeToUtf8Bytes(box);
                    purge.RoleBlob(role, "mailbox", purged);
                    purge.WithEscrowEconomy(new EscrowEconomyIntent("mail/claim-purge", session.Account ?? "",
                        role, transferReason: EconomyReason.MailClaim).Evidence(new { Role = role,
                            Expired = expiredIds.Order().ToArray() }));
                    await roles.CommitAsync(purge, "mail/claim-purge");
                    source = new RoleBlobSnapshot(true, purged, purge.CommittedVector(role, "mailbox"));
                }
                int refusal = ErrorCodes.MailMissing;
                foreach (string id in request.MailUid.Distinct())
                {
                    var mail = box.Letters.FirstOrDefault(m => m.Id == id);
                    if (mail == null) { if (expiredIds.Contains(id)) refusal = ErrorCodes.MailExpired; continue; }
                    if (mail.Claimed) { refusal = ErrorCodes.MailAlreadyClaimed; continue; }
                    if (mail.Attachments.Count == 0 && mail.Items.Count == 0 && mail.Currency.Count == 0)
                    { refusal = ErrorCodes.MailNoAttachments; continue; }
                    var attachments = mail.Attachments.Select(a => (a.Entry, a.Count, a.Bind)).ToArray();
                    var boundGoods = attachments.Where(a => a.Entry is not (100 or 101)).ToArray();
                    long baseExp = attachments.Where(a => a.Entry == 100).Sum(a => (long)a.Count);
                    long jobExp = attachments.Where(a => a.Entry == 101).Sum(a => (long)a.Count);
                    int preflight = character.PlainGrantError(session, [], mail.Items, mail.Currency,
                        boundGoods: attachments);
                    if (preflight != 0) { refusal = preflight; continue; }
                    // A copy is persisted, so a failed transaction leaves both mailbox and in-memory bag untouched.
                    var next = Clone(box);
                    var changed = next.Letters.Single(m => m.Id == id);
                    changed.Read = true;
                    changed.Claimed = true;
                    byte[] claimed = JsonSerializer.SerializeToUtf8Bytes(next);
                    var expected = source;
                    RoleItemPlanBuilder? claimPlan = null;
                    try
                    {
                        var granted = await character.ExchangeLockedAsync(session, [], [], popup: false, sync: false,
                            persist: tx =>
                            {
                                claimPlan = tx;
                                tx.Guard(new RoleBlobGuardPlan(role, "mailbox", expected));
                                tx.RoleBlob(role, "mailbox", claimed);
                                var intent = new EscrowEconomyIntent("mail/claim", session.Account ?? "",
                                    role, sourceKey: $"mail/claim/{role}/{id}", transferReason: EconomyReason.MailClaim)
                                    .Evidence(new { Role = role, MailId = id, Attachments = mail.Attachments,
                                        Items = mail.Items.Select(i => new { i.Entry, i.Stack, i.Guid }).ToArray(),
                                        Currency = mail.Currency, BaseExp = baseExp, JobExp = jobExp });
                                for (int index = 0; index < mail.Attachments.Count; index++)
                                {
                                    var attachment = mail.Attachments[index];
                                    if (attachment.Entry is not (100 or 101))
                                    {
                                        var asset = MailAsset(attachment.Entry);
                                        if (asset.Kind == EconomyAssetKind.Item)
                                            intent.RealizeMailAttachment(asset, attachment.Count,
                                                role, id, index, attachment.Bind);
                                    }
                                }
                                tx.WithEscrowEconomy(intent);
                            },
                            boundGoods: boundGoods, exactGoods: mail.Items, currencyGoods: mail.Currency,
                            baseExp: baseExp, jobExp: jobExp, committed: () => box = next, asyncCommit: true);
                        if (granted == null) continue;
                    }
                    catch (Exception e) when (e is SqliteException or InvalidOperationException)
                    {
                        log.LogWarning(e, "Mail {Mail} grant transaction failed", id);
                        refusal = ErrorCodes.Unknown;
                        continue;
                    }
                    box = next;
                    source = new RoleBlobSnapshot(true, claimed, claimPlan!.CommittedVector(role, "mailbox"));
                    response.MailUid.Add(id);
                }
                response.Result = response.MailUid.Count > 0 ? 0 : refusal; // (inferred) partial bulk success is success.
            }
            if (response.MailUid.Count > 0) await character.ItemsChangedAsync(session, stats: false);
            await session.SendGameAsync(ClaimMail, response);
        }
        // Both packets are after commit/apply and owner release. A failed write cannot reopen a claim.
        if (notices != null) await notices.FlushAsync();
    }

    private static Mailbox Clone(Mailbox box) => JsonSerializer.Deserialize<Mailbox>(JsonSerializer.SerializeToUtf8Bytes(box))!;
    private static bool HasAssets(Letter mail) => mail.Attachments.Count > 0 || mail.Items.Count > 0 || mail.Currency.Count > 0;
    private static bool Active(Letter mail, long now) => mail.RemoveTime == 0 || now < mail.RemoveTime;
    private static object? Map(object? args, string key) => args is IDictionary<object, object?> map && map.TryGetValue(key, out var v) ? v : null;
    private static bool TryMapInt(object? args, string key, out int result)
    {
        result = 0;
        var value = Map(args, key);
        if (value is not (sbyte or byte or short or ushort or int or uint or long or ulong)) return false;
        try { result = Convert.ToInt32(value, CultureInfo.InvariantCulture); return true; }
        catch (OverflowException) { return false; }
    }
    private static Dictionary<string, object?> Success() => new() { ["Ret"] = MicroServiceRouter.Ret(true) };

    private Dictionary<string, object?> Row(Letter mail)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["haveRead"] = mail.Read, ["giftReceived"] = mail.Claimed, ["important_type"] = mail.ImportantType,
        };
        if (mail.TemplateId > 0) parameters["cfg_id"] = mail.TemplateId;
        // Raw content must omit localization_cfg_id; even numeric zero is truthy in Lua.
        if (mail.Attachments.Count > 0 || mail.Items.Count > 0 || mail.Currency.Count > 0)
        {
            var items = new MailItemList();
            foreach (var item in mail.Attachments)
                items.Items.Add(character.ItemInfoOf(new InvItem { Entry = item.Entry, Stack = item.Count, BindType = item.Bind }));
            items.Items.AddRange(mail.Items.Select(character.ItemInfoOf));
            foreach (var (entry, amount) in mail.Currency)
                for (long left = amount; left > 0; left -= Math.Min(left, int.MaxValue))
                    items.Items.Add(character.ItemInfoOf(new InvItem
                    { Entry = entry, Stack = (int)Math.Min(left, int.MaxValue) }));
            parameters["items"] = items.ToByteArray();
        }
        if (mail.ContentParams is { Length: > 0 } && !LegacyAwardParams(mail)) parameters["content_params"] = mail.ContentParams;
        return new Dictionary<string, object?>
        {
            ["MailId"] = mail.Id, ["CreatedTime"] = mail.CreatedTime, ["RemoveTime"] = mail.RemoveTime,
            ["MailType"] = mail.Category, ["Title"] = mail.Title, ["SrcName"] = mail.Sender,
            ["SrcId"] = mail.SenderId, ["Abstract"] = mail.Abstract, ["Content"] = mail.Content,
            ["Params"] = parameters,
        };
    }
}
