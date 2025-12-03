import { streamingService } from '@/service/streaming.service'
import type { TurnServerConfig } from '@/service/streaming.service'

export interface TurnConnectionTestResult {
  success: boolean
  sessionId?: string
  error?: string
  connectionState?: string
  iceConnectionState?: string
  hasRelayCandidate?: boolean
  candidateTypes?: string[]
  latency?: number
}

/**
 * 测试前后端通过 TURN 服务器建立连接
 * @param timeout 超时时间（毫秒），默认 15 秒
 * @returns 测试结果
 */
export async function testTurnConnection(
  timeout: number = 15000
): Promise<TurnConnectionTestResult> {
  const result: TurnConnectionTestResult = {
    success: false,
  }

  let pc: RTCPeerConnection | null = null
  const startTime = Date.now()

  try {
    // 1. 从后端创建测试会话（强制使用 TURN）
    const offerResponse = await streamingService.createTurnTestSession()
    if (!offerResponse.success || !offerResponse.data) {
      result.error = offerResponse.errorMessage || '无法创建测试会话'
      return result
    }

    const { sessionId, sdp: offerSdp } = offerResponse.data
    result.sessionId = sessionId

    // 2. 获取 TURN 配置
    const turnConfigResponse = await streamingService.getTurnConfig()
    if (!turnConfigResponse.success || !turnConfigResponse.data) {
      result.error = '无法获取 TURN 配置'
      return result
    }

    const turnServers = turnConfigResponse.data.turnServers || []
    if (turnServers.length === 0) {
      result.error = '未配置 TURN 服务器'
      return result
    }

    // 3. 创建前端 RTCPeerConnection，强制使用 TURN
    const rtcConfig: RTCConfiguration = {
      iceServers: [
        {
          urls: 'stun:stun.l.google.com:19302',
        },
        ...turnServers
          .filter((s): s is TurnServerConfig & { url: string } => !!s.url)
          .map((s) => ({
            urls: s.url!,
            username: s.username || undefined,
            credential: s.credential || undefined,
          })),
      ],
      iceTransportPolicy: 'relay', // 强制使用 TURN
      iceCandidatePoolSize: 0,
    }

    pc = new RTCPeerConnection(rtcConfig)

    // 设置超时
    const timeoutId = setTimeout(() => {
      if (pc) {
        pc.close()
      }
      if (!result.success) {
        result.error = '测试超时'
      }
    }, timeout)

    // 监听 ICE 候选地址
    const candidates: RTCIceCandidate[] = []
    const candidateTypes: string[] = []
    let hasRelayCandidate = false

    pc.onicecandidate = async (event) => {
      if (event.candidate) {
        candidates.push(event.candidate)
        const candidateStr = event.candidate.candidate

        // 检查候选地址类型
        if (candidateStr.includes('typ relay')) {
          hasRelayCandidate = true
          candidateTypes.push('relay')
          result.hasRelayCandidate = true
        } else if (candidateStr.includes('typ srflx')) {
          candidateTypes.push('srflx')
        } else if (candidateStr.includes('typ host')) {
          candidateTypes.push('host')
        }

        // 发送 ICE 候选地址到后端
        try {
          await streamingService.sendICECandidate({
            sessionId,
            candidate: event.candidate.candidate,
            sdpMid: event.candidate.sdpMid,
            sdpMLineIndex: event.candidate.sdpMLineIndex,
          })
        } catch (error) {
          console.error('发送 ICE 候选地址失败:', error)
        }
      } else {
        // 所有候选地址收集完成
        clearTimeout(timeoutId)
        result.candidateTypes = [...new Set(candidateTypes)]
      }
    }

    // 监听 ICE 连接状态
    pc.oniceconnectionstatechange = async () => {
      if (pc) {
        result.iceConnectionState = pc.iceConnectionState
        result.connectionState = pc.connectionState

        if (pc.iceConnectionState === 'connected' || pc.iceConnectionState === 'completed') {
          clearTimeout(timeoutId)
          if (hasRelayCandidate) {
            result.success = true
            result.latency = Date.now() - startTime
          } else {
            result.error = '连接成功但未使用 TURN relay 候选地址'
          }
        } else if (
          pc.iceConnectionState === 'failed' ||
          pc.iceConnectionState === 'disconnected'
        ) {
          clearTimeout(timeoutId)
          if (!result.error) {
            result.error = `ICE 连接状态: ${pc.iceConnectionState}`
          }
        }
      }
    }

    // 监听连接状态
    pc.onconnectionstatechange = () => {
      if (pc) {
        result.connectionState = pc.connectionState
        if (pc.connectionState === 'connected') {
          if (hasRelayCandidate) {
            result.success = true
            result.latency = Date.now() - startTime
          }
        } else if (pc.connectionState === 'failed') {
          if (!result.error) {
            result.error = '连接失败'
          }
        }
      }
    }

    // 4. 设置远程描述（后端的 offer）
    await pc.setRemoteDescription({
      type: 'offer',
      sdp: offerSdp,
    } as RTCSessionDescriptionInit)

    // 5. 创建 answer
    const answer = await pc.createAnswer()
    await pc.setLocalDescription(answer)

    // 6. 发送 answer 到后端
    const answerResponse = await streamingService.sendAnswer({
      sessionId,
      sdp: answer.sdp!,
      type: 'answer',
    })

    if (!answerResponse.success) {
      result.error = answerResponse.errorMessage || '发送 Answer 失败'
      return result
    }

    // 7. 获取后端的 ICE 候选地址
    const getBackendCandidates = async () => {
      try {
        const candidatesResponse = await streamingService.getPendingIceCandidates(sessionId)
        if (candidatesResponse.success && candidatesResponse.data) {
          for (const candidate of candidatesResponse.data.candidates) {
            try {
              await pc!.addIceCandidate({
                candidate: candidate.candidate,
                sdpMid: candidate.sdpMid,
                sdpMLineIndex: candidate.sdpMLineIndex,
              } as RTCIceCandidateInit)
            } catch (error) {
              console.error('添加后端 ICE 候选地址失败:', error)
            }
          }
        }
      } catch (error) {
        console.error('获取后端 ICE 候选地址失败:', error)
      }
    }

    // 定期获取后端的 ICE 候选地址
    const candidateInterval = setInterval(getBackendCandidates, 500)
    setTimeout(() => clearInterval(candidateInterval), timeout)

    // 等待连接建立或超时
    await new Promise<void>((resolve) => {
      const checkInterval = setInterval(() => {
        if (pc) {
          if (
            pc.iceConnectionState === 'connected' ||
            pc.iceConnectionState === 'completed' ||
            pc.iceConnectionState === 'failed' ||
            pc.connectionState === 'failed'
          ) {
            clearInterval(checkInterval)
            clearTimeout(timeoutId)
            resolve()
          }
        } else {
          clearInterval(checkInterval)
          resolve()
        }
      }, 100)

      setTimeout(() => {
        clearInterval(checkInterval)
        clearTimeout(timeoutId)
        resolve()
      }, timeout)
    })

    clearInterval(candidateInterval)
    clearTimeout(timeoutId)

    // 8. 获取最终状态
    if (pc) {
      result.connectionState = pc.connectionState
      result.iceConnectionState = pc.iceConnectionState
    }

    // 获取后端会话信息
    try {
      const sessionInfoResponse = await streamingService.getTurnTestSessionInfo(sessionId)
      if (sessionInfoResponse.success && sessionInfoResponse.data) {
        const info = sessionInfoResponse.data
        if (!result.connectionState) {
          result.connectionState = info.connectionState
        }
        if (!result.iceConnectionState) {
          result.iceConnectionState = info.iceConnectionState
        }
      }
    } catch (error) {
      console.error('获取会话信息失败:', error)
    }

    // 9. 判断测试结果
    if (!result.success && !result.error) {
      if (hasRelayCandidate) {
        result.success = true
        result.latency = Date.now() - startTime
      } else if (candidates.length > 0) {
        result.error = '未找到 TURN relay 候选地址'
      } else {
        result.error = '未收集到任何 ICE 候选地址'
      }
    }

    result.candidateTypes = [...new Set(candidateTypes)]
  } catch (error) {
    result.error = error instanceof Error ? error.message : '未知错误'
  } finally {
    // 清理资源
    if (pc) {
      try {
        pc.close()
      } catch (e) {
        // 忽略关闭错误
      }
    }

    // 清理后端会话
    if (result.sessionId) {
      try {
        await streamingService.deleteSession(result.sessionId)
      } catch (error) {
        console.error('清理测试会话失败:', error)
      }
    }
  }

  return result
}

