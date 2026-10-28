namespace Ro3Emu.Server.Game;

/// <summary>romsg message ids (client Lua NW_Helper_PBDefine: ProtobufMap[id] = {name, req, res}).</summary>
public static class MsgId
{
    public const uint Heartbeat = 1;
    /// <summary>COMMON_ERROR_NTF; on S→C it is carried as inner id 2 in the outer id 2 batch envelope.</summary>
    public const uint CommonError = 2;
    public const uint LoginRoleCreate = 1006;
    public const uint LoginRoleSelect = 1007;
    /// <summary>LOGIN_RECONNECT: a server push only, after a silent gate reconnect.</summary>
    public const uint LoginReconnect = 1011;
    public const uint UploadClientSdkInfo = 1019;
    public const uint Logout = 1036;
    public const uint PreSwitchRole = 1037;
    public const uint AutoSyncSettings = 5061;
    public const uint SaveSetting = 39000;
    public const uint PlayerAsset = 1023;
    public const uint ClientEnterMainUi = 1038;
    public const uint SceneLoaded = 30005;
    public const uint EntitySelf = 31005;
    public const uint ChangeFace = 31020;
    public const uint SyncAoiSelf = 31044;
    public const uint SelfInfoEndFrameNotify = 31053;
    public const uint Walk = 31082;
    public const uint SwitchSceneLine = 70010;
    public const uint ImSdkUserSig = 77001;
}
