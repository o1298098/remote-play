namespace RemotePlay.Models.Base
{
    /// <summary>
    /// API 错误码枚举
    /// 前端根据此错误码在 i18n 文件中查找对应的翻译
    /// </summary>
    public enum ErrorCode
    {
        // 通用错误 (1000-1999)
        UnknownError = 1000,
        InvalidRequest = 1001,
        Unauthorized = 1002,
        NotFound = 1003,
        InternalServerError = 1004,
        
        // 认证相关错误 (1500-1599)
        InvalidCredentials = 1501,
        LoginFailed = 1502,
        UserNotFound = 1503,
        
        // 参数验证错误 (2000-2999)
        SessionIdRequired = 2001,
        SdpRequired = 2002,
        AnswerSdpRequired = 2003,
        CandidateRequired = 2004,
        ConfigRequired = 2005,
        HostIpRequired = 2006,
        AccountIdRequired = 2007,
        PinRequired = 2008,
        CredentialRequired = 2009,
        ButtonInvalid = 2010,
        TriggerValueRequired = 2011,
        
        // WebRTC 相关错误 (3000-3999)
        WebRtcSessionNotFound = 3001,
        WebRtcSessionExpired = 3002,
        WebRtcSessionNotBound = 3003,
        WebRtcOfferCreationFailed = 3004,
        WebRtcAnswerProcessingFailed = 3005,
        WebRtcIceCandidateProcessingFailed = 3006,
        WebRtcKeyFrameRequestFailed = 3007,
        WebRtcConnectionFailed = 3008,
        WebRtcGetCandidatesFailed = 3009,
        
        // 流相关错误 (4000-4999)
        StreamNotFound = 4001,
        StreamEnded = 4002,
        StreamHealthUnavailable = 4003,
        KeyFrameRequestFailed = 4004,
        
        // 设备相关错误 (5000-5999)
        DeviceNotFound = 5001,
        DeviceDiscoveryFailed = 5002,
        DeviceRegistrationFailed = 5003,
        DeviceBindingFailed = 5004,
        DeviceNotOnline = 5005,
        DeviceWakeFailed = 5006,
        DeviceSettingsLoadFailed = 5007,
        DeviceSettingsSaveFailed = 5008,
        
        // 控制器相关错误 (6000-6999)
        ControllerNotConnected = 6001,
        ControllerConnectionFailed = 6002,
        ControllerStartFailed = 6003,
        
        // 配置相关错误 (7000-7999)
        TurnConfigGetFailed = 7001,
        TurnConfigSaveFailed = 7002,
        WebRtcConfigGetFailed = 7003,
        WebRtcConfigSaveFailed = 7004,
        
        // 统计相关错误 (8000-8999)
        LatencyStatsServiceDisabled = 8001,
        LatencyStatsNotFound = 8002,
        LatencyStatsGetFailed = 8003,
        LatencyStatsRecordFailed = 8004,
    }
}

