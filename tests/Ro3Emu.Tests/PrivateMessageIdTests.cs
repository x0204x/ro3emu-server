using Google.Protobuf;
using Google.Protobuf.Reflection;
using Romsg;
using Ro3Emu.ClientHooks;

namespace Ro3Emu.Tests;

public sealed class PrivateMessageIdTests
{
    [Fact]
    public void PrivateIdsAreNamespacedUniqueAndOutsideBothClientCatalogFamilies()
    {
        var ids = PrivateMessageIds.All;
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id =>
        {
            Assert.InRange(id, 0x7F52_0001u, 0x7F52_00FFu);
            Assert.True(id <= int.MaxValue);
            Assert.False(id is >= 77000 and <= 77099);
        });

        var official = Enum.GetValues<MSG_Type>().Select(value => unchecked((uint)(int)value)).ToHashSet();
        foreach (string line in File.ReadLines(Path.Combine(ClientData.RepoRoot, "data", "msgtable.tsv")))
        {
            string value = line.Split('\t', 2)[0];
            if (uint.TryParse(value, out uint id)) official.Add(id);
        }

        Assert.DoesNotContain(ids, official.Contains);
        Assert.Contains(77010u, official);
        Assert.Contains(77011u, official);
        Assert.Contains(77012u, official);
        Assert.Contains(77013u, official);
    }

    [Fact]
    public void AsiaManagedChatDescriptorShapeRoundTrips()
    {
        var request = new ManagedChatSendReq
        {
            ClientMessageId = ByteString.CopyFrom(new byte[] { 1, 2, 3 }),
            Channel = new ManagedChatChannelRef { ChannelType = ChatChannelType.Team, TargetId = "42" },
            Content = new ManagedChatContent
            {
                Kind = ManagedChatContentKind.ManagedChatContentText,
                Text = "hello",
                LegacyExtraPayload = ByteString.CopyFrom(new byte[] { 4, 5 }),
            },
            RolloutEpoch = 9,
        };

        Assert.Equal(request, ManagedChatSendReq.Parser.ParseFrom(request.ToByteArray()));
        string[] expected =
        [
            "ManagedChatBootstrapReq", "ManagedChatBootstrapRsp", "ManagedChatChannelRef",
            "ManagedChatChannelStateNtf", "ManagedChatClientCapability", "ManagedChatContent",
            "ManagedChatCredential", "ManagedChatCredentialRefreshReq", "ManagedChatCredentialRefreshRsp",
            "ManagedChatSendReq", "ManagedChatSendRsp",
        ];
        string[] actual = ManagedChatSendReq.Descriptor.File.MessageTypes
            .Select(message => message.Name).Where(name => name.StartsWith("ManagedChat", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(), actual);
    }

    [Fact]
    public void AsiaManagedChatMessageFieldsMatchTheClientDescriptor()
    {
        AssertFields(ManagedChatClientCapability.Descriptor,
            F("supportedProviders", 1, FieldType.Enum, true, "romsg.ManagedChatProvider"),
            F("protocolVersion", 2, FieldType.UInt32),
            F("featureBits", 3, FieldType.UInt64));
        AssertFields(ManagedChatChannelRef.Descriptor,
            F("channelType", 1, FieldType.Enum, typeName: "romsg.ChatChannelType"),
            F("targetId", 2, FieldType.String));
        AssertFields(ManagedChatCredential.Descriptor,
            F("mode", 1, FieldType.Enum, typeName: "romsg.ManagedChatCredentialMode"),
            F("value", 2, FieldType.String),
            F("expiresAtMs", 3, FieldType.Int64));
        AssertFields(ManagedChatBootstrapReq.Descriptor,
            F("capability", 1, FieldType.Message, typeName: "romsg.ManagedChatClientCapability"));
        AssertFields(ManagedChatBootstrapRsp.Descriptor,
            F("error", 1, FieldType.Enum, typeName: "romsg.ManagedChatErrorCode"),
            F("provider", 2, FieldType.Enum, typeName: "romsg.ManagedChatProvider"),
            F("publicApiKey", 3, FieldType.String),
            F("providerUserId", 4, FieldType.String),
            F("credential", 5, FieldType.Message, typeName: "romsg.ManagedChatCredential"),
            F("rolloutEpoch", 6, FieldType.UInt64),
            F("policyVersion", 7, FieldType.UInt32),
            F("channels", 8, FieldType.Message, true, "romsg.ManagedChatChannelRef"),
            F("featureBits", 9, FieldType.UInt64));
        AssertFields(ManagedChatCredentialRefreshReq.Descriptor,
            F("rolloutEpoch", 1, FieldType.UInt64));
        AssertFields(ManagedChatCredentialRefreshRsp.Descriptor,
            F("error", 1, FieldType.Enum, typeName: "romsg.ManagedChatErrorCode"),
            F("credential", 2, FieldType.Message, typeName: "romsg.ManagedChatCredential"),
            F("rolloutEpoch", 3, FieldType.UInt64));
        AssertFields(ManagedChatContent.Descriptor,
            F("kind", 1, FieldType.Enum, typeName: "romsg.ManagedChatContentKind"),
            F("text", 2, FieldType.String),
            F("legacyExtraPayload", 3, FieldType.Bytes));
        AssertFields(ManagedChatSendReq.Descriptor,
            F("clientMessageId", 1, FieldType.Bytes),
            F("channel", 2, FieldType.Message, typeName: "romsg.ManagedChatChannelRef"),
            F("content", 3, FieldType.Message, typeName: "romsg.ManagedChatContent"),
            F("rolloutEpoch", 4, FieldType.UInt64));
        AssertFields(ManagedChatSendRsp.Descriptor,
            F("error", 1, FieldType.Enum, typeName: "romsg.ManagedChatErrorCode"),
            F("clientMessageId", 2, FieldType.Bytes),
            F("providerMessageId", 3, FieldType.String),
            F("providerCreatedAtMs", 4, FieldType.Int64),
            F("retryable", 5, FieldType.Bool),
            F("commitUnknown", 6, FieldType.Bool),
            F("retryAfterMs", 7, FieldType.UInt32),
            F("accepted", 8, FieldType.Bool));
        AssertFields(ManagedChatChannelStateNtf.Descriptor,
            F("rolloutEpoch", 1, FieldType.UInt64),
            F("stateVersion", 2, FieldType.UInt64),
            F("watch", 3, FieldType.Message, true, "romsg.ManagedChatChannelRef"),
            F("unwatch", 4, FieldType.Message, true, "romsg.ManagedChatChannelRef"));
    }

    [Fact]
    public void AsiaManagedChatEnumsAndOfficialOpcodeNamesMatchTheClientDescriptor()
    {
        AssertEnum("ManagedChatProvider",
            ("MANAGED_CHAT_PROVIDER_UNSPECIFIED", 0),
            ("MANAGED_CHAT_PROVIDER_TENCENT", 1),
            ("MANAGED_CHAT_PROVIDER_STREAM", 2));
        AssertEnum("ManagedChatCredentialMode",
            ("MANAGED_CHAT_CREDENTIAL_UNSPECIFIED", 0),
            ("MANAGED_CHAT_CREDENTIAL_DIRECT_TOKEN", 1),
            ("MANAGED_CHAT_CREDENTIAL_HTTPS_EXCHANGE_TICKET", 2));
        AssertEnum("ManagedChatContentKind",
            ("MANAGED_CHAT_CONTENT_UNSPECIFIED", 0),
            ("MANAGED_CHAT_CONTENT_TEXT", 1),
            ("MANAGED_CHAT_CONTENT_LEGACY_RICH", 2));
        AssertEnum("ManagedChatErrorCode",
            ("MANAGED_CHAT_ERROR_UNSPECIFIED", 0),
            ("MANAGED_CHAT_ERROR_OK", 1),
            ("MANAGED_CHAT_ERROR_UPDATE_REQUIRED", 2),
            ("MANAGED_CHAT_ERROR_TEMPORARILY_UNAVAILABLE", 3),
            ("MANAGED_CHAT_ERROR_CREDENTIAL_EXPIRED", 4),
            ("MANAGED_CHAT_ERROR_NOT_MEMBER", 5),
            ("MANAGED_CHAT_ERROR_NOT_ALLOWED", 6),
            ("MANAGED_CHAT_ERROR_RATE_LIMITED", 7),
            ("MANAGED_CHAT_ERROR_INVALID_CONTENT", 8),
            ("MANAGED_CHAT_ERROR_PROVIDER_RATE_LIMITED", 9),
            ("MANAGED_CHAT_ERROR_PROVIDER_TIMEOUT", 10),
            ("MANAGED_CHAT_ERROR_PROVIDER_UNAVAILABLE", 11),
            ("MANAGED_CHAT_ERROR_PROVIDER_CONFIGURATION", 12),
            ("MANAGED_CHAT_ERROR_IDEMPOTENCY_CONFLICT", 13),
            ("MANAGED_CHAT_ERROR_COMMIT_STATUS_UNKNOWN", 14),
            ("MANAGED_CHAT_ERROR_INTERNAL", 15));

        EnumDescriptor msgType = MGDefineReflection.Descriptor.EnumTypes.Single(item => item.Name == "MSG_Type");
        var actual = msgType.Values.Where(item => item.Number is >= 77010 and <= 77013)
            .Select(item => (item.Name, item.Number)).ToArray();
        Assert.Equal(new[]
        {
            ("TT_MANAGED_CHAT_BOOTSTRAP", 77010),
            ("TT_MANAGED_CHAT_CREDENTIAL_REFRESH", 77011),
            ("TT_MANAGED_CHAT_SEND", 77012),
            ("TT_MANAGED_CHAT_CHANNEL_STATE_NTF", 77013),
        }, actual);
    }

    private static FieldShape F(string name, int number, FieldType type, bool repeated = false, string? typeName = null) =>
        new(name, number, type, repeated, typeName);

    private static void AssertFields(MessageDescriptor message, params FieldShape[] expected)
    {
        FieldDescriptor[] actual = message.Fields.InDeclarationOrder().ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            FieldShape shape = expected[i];
            FieldDescriptor field = actual[i];
            Assert.Equal(shape.Name, field.Name);
            Assert.Equal(shape.Number, field.FieldNumber);
            Assert.Equal(shape.Type, field.FieldType);
            Assert.Equal(shape.Repeated, field.IsRepeated);
            string? typeName = field.FieldType switch
            {
                FieldType.Message or FieldType.Group => field.MessageType.FullName,
                FieldType.Enum => field.EnumType.FullName,
                _ => null,
            };
            Assert.Equal(shape.TypeName, typeName);
        }
    }

    private static void AssertEnum(string name, params (string Name, int Number)[] expected)
    {
        EnumDescriptor descriptor = MGChatReflection.Descriptor.EnumTypes.Single(item => item.Name == name);
        Assert.Equal(expected, descriptor.Values.Select(item => (item.Name, item.Number)).ToArray());
    }

    private sealed record FieldShape(string Name, int Number, FieldType Type, bool Repeated, string? TypeName);
}
