import { apiRequest, type ApiResponse } from './api-client'

// WebRTC Offer 请求
export interface WebRTCOfferRequest {
  remotePlaySessionId?: string
  preferLanCandidates?: boolean
}

// WebRTC Offer 响应
export interface WebRTCOfferResponse {
  sessionId: string
  sdp: string
}

// WebRTC Answer 请求
export interface WebRTCAnswerRequest {
  sessionId: string
  sdp: string
  type: string
}

// WebRTC ICE Candidate 请求
export interface WebRTCICECandidateRequest {
  sessionId: string
  candidate: string
  sdpMid: string | null
  sdpMLineIndex: number | null
}

// Remote Play Session 信息
export interface RemotePlaySession {
  id: string
  hostId: string
  status?: string
  [key: string]: any
}

// 延时统计信息
export interface LatencyStats {
  currentLatency?: number
  totalLatencyAvg?: number
  totalLatencyMin?: number
  totalLatencyMax?: number
  networkTransmitAvg?: number
  serverProcessingAvg?: number
  sampleCount?: number
}

export interface StreamHealth {
  timestamp: string
  status: string
  message?: string | null
  consecutiveFailures: number
  totalRecoveredFrames: number
  totalFrozenFrames: number
  videoReceived: number
  videoLost: number
  audioReceived: number
  audioLost: number
}

// WebRTC 配置（包含 TURN 服务器配置）
export interface WebRTCConfig {
  publicIp?: string | null
  icePortMin?: number | null
  icePortMax?: number | null
  shufflePorts?: boolean
  turnServers: TurnServerConfig[]
  preferLanCandidates?: boolean
}

export interface TurnServerConfig {
  url?: string | null
  username?: string | null
  credential?: string | null
}

/**
 * 串流服务
 */
export const streamingService = {
  /**
   * 创建 WebRTC Offer
   */
  createOffer: async (
    payload?: WebRTCOfferRequest
  ): Promise<ApiResponse<WebRTCOfferResponse>> => {
    const hasPayload = payload && Object.keys(payload).length > 0
    return apiRequest<WebRTCOfferResponse>('/webrtc/offer', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: hasPayload ? JSON.stringify(payload) : undefined,
    })
  },

  /**
   * 发送 WebRTC Answer
   */
  sendAnswer: async (data: WebRTCAnswerRequest): Promise<ApiResponse<any>> => {
    return apiRequest('/webrtc/answer', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(data),
    })
  },

  /**
   * 发送 ICE Candidate
   */
  sendICECandidate: async (data: WebRTCICECandidateRequest): Promise<ApiResponse<any>> => {
    return apiRequest('/webrtc/ice', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(data),
    })
  },

  /**
   * 获取后端生成的待处理 ICE Candidate
   */
  getPendingIceCandidates: async (sessionId: string): Promise<ApiResponse<{ candidates: Array<{ candidate: string; sdpMid: string | null; sdpMLineIndex: number | null }> }>> => {
    return apiRequest<{ candidates: Array<{ candidate: string; sdpMid: string | null; sdpMLineIndex: number | null }> }>(`/webrtc/ice/${encodeURIComponent(sessionId)}`, {
      method: 'GET',
    })
  },

  /**
   * 从 Host ID 创建或获取 Remote Play Session
   */
  startSession: async (hostId: string): Promise<ApiResponse<RemotePlaySession>> => {
    return apiRequest<RemotePlaySession>(`/playstation/start-session?hostId=${encodeURIComponent(hostId)}`, {
      method: 'POST',
    })
  },

  /**
   * 连接 WebRTC Session 到 Remote Play Session
   */
  connectToRemotePlaySession: async (
    webrtcSessionId: string,
    remotePlaySessionId: string
  ): Promise<ApiResponse<any>> => {
    return apiRequest(`/webrtc/connect/${webrtcSessionId}/${remotePlaySessionId}`, {
      method: 'POST',
    })
  },

  /**
   * 获取延时统计
   */
  getLatencyStats: async (sessionId: string): Promise<ApiResponse<LatencyStats>> => {
    return apiRequest<LatencyStats>(`/webrtc/latency/${sessionId}`, {
      method: 'GET',
    })
  },

  /**
   * 删除 WebRTC Session
   */
  deleteSession: async (sessionId: string): Promise<ApiResponse<any>> => {
    return apiRequest(`/webrtc/session/${sessionId}`, {
      method: 'DELETE',
    })
  },

  /**
   * 主动请求关键帧
   */
  requestKeyframe: async (sessionId: string): Promise<ApiResponse<boolean>> => {
    return apiRequest<boolean>(`/webrtc/session/${encodeURIComponent(sessionId)}/keyframe`, {
      method: 'POST',
    })
  },

  /**
   * 获取流健康状态
   */
  getStreamHealth: async (sessionId: string): Promise<ApiResponse<StreamHealth>> => {
    return apiRequest<StreamHealth>(`/streaming/session/${encodeURIComponent(sessionId)}/health`, {
      method: 'GET',
    })
  },

  /**
   * 获取用户的 WebRTC TURN 服务器配置
   */
  getTurnConfig: async (): Promise<ApiResponse<WebRTCConfig>> => {
    return apiRequest<WebRTCConfig>('/streaming/webrtc/turn-config', {
      method: 'GET',
    })
  },
}

