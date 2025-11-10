/**
 * 服务模块统一导出
 * 方便从单个入口导入所有服务
 */

// 导出基础API客户端和类型
export { apiRequest, type ApiResponse } from './api-client'

// 导出认证服务
export { authService, type AuthResponse, type RegisterRequest, type LoginRequest } from './auth.service'

// 导出PlayStation设备服务
export { playStationService, type ConsoleInfo, type BindDeviceRequest, type UserDevice } from './playstation.service'

// 导出Profile服务
export { profileService, type ProfileCredentials, type UserProfile } from './profile.service'

// 导出串流服务
export {
  streamingService,
  type WebRTCOfferResponse,
  type WebRTCAnswerRequest,
  type WebRTCICECandidateRequest,
  type RemotePlaySession,
  type LatencyStats,
} from './streaming.service'

