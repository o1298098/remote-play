import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useGamepadInput, useGamepad } from '@/hooks/use-gamepad'
import { streamingService } from '@/service/streaming.service'
import { streamingHubService } from '@/service/streaming-hub.service'
import { controllerService } from '@/service/controller.service'
import {
  applyControllerRumbleToGamepads,
  getRumbleSettings,
  onRumbleSettingsChange,
  type RumbleSettings,
} from '@/service/rumble.service'
import { playStationService } from '@/service/playstation.service'
import { apiRequest } from '@/service/api-client'
import { optimizeSdpForLowLatency, optimizeVideoForLowLatency } from '@/utils/webrtc-optimization'
import { createKeyboardHandler } from '@/utils/keyboard-mapping'
import { GamepadButton, PS5_BUTTON_MAP, type GamepadInputEvent } from '@/service/gamepad.service'
import { AXIS_DEADZONE, MAX_HEARTBEAT_INTERVAL_MS, SEND_INTERVAL_MS, MOBILE_SEND_INTERVAL_MS, TRIGGER_DEADZONE } from './use-streaming-connection/constants'
import { useStickInputState } from './use-streaming-connection/stick-input-state'
import { useMouseRightStick } from './use-streaming-connection/use-mouse-right-stick'
import { isMobileDevice } from '@/utils/device-detection'

type ToastFn = (props: { title?: string; description?: string; variant?: 'default' | 'destructive'; [key: string]: any }) => void

interface UseStreamingConnectionParams {
  hostId: string | null
  deviceName: string
  isLikelyLan: boolean
  videoRef: React.RefObject<HTMLVideoElement>
  toast: ToastFn
}

export interface StreamingMonitorStats {
  downloadKbps: number | null
  uploadKbps: number | null
  videoBitrateKbps: number | null
  resolution: { width: number; height: number } | null
  latencyMs: number | null
}

export function useStreamingConnection({ hostId, deviceName, isLikelyLan, videoRef, toast }: UseStreamingConnectionParams) {
  const { t } = useTranslation()
  const [isConnected, setIsConnected] = useState(false)
  const [isConnecting, setIsConnecting] = useState(false)
  const [connectionState, setConnectionState] = useState<string>(() => t('streaming.connection.state.disconnected'))
  const [webrtcSessionId, setWebrtcSessionId] = useState<string | null>(null)
  const [remotePlaySessionId, setRemotePlaySessionId] = useState<string | null>(null)
  const [connectionStats, setConnectionStats] = useState<StreamingMonitorStats | null>(null)
  const [isStatsEnabled, setIsStatsEnabled] = useState(false)

  const peerConnectionRef = useRef<RTCPeerConnection | null>(null)
  const videoOptimizeCleanupRef = useRef<(() => void) | null>(null)
  const keyboardCleanupRef = useRef<(() => void) | null>(null)
  const gamepadEnabledRef = useRef<boolean>(false)
  const isConnectedRef = useRef<boolean>(false)
  const hasAttemptedInitialConnectRef = useRef<boolean>(false)
  const rumbleSettingsRef = useRef<RumbleSettings>(getRumbleSettings())
  
  // ✅ ICE Restart 相关状态
  const iceRestartTimeoutRef = useRef<number | null>(null)
  const iceDisconnectedTimeRef = useRef<number | null>(null)

  const {
    getNormalizedState,
    snapshotGamepadAxes,
    handleGamepadAxis,
    setPointerLock,
    setMouseVelocity,
    setKeyboardLeftStick,
    setTriggerPressure,
    reset: resetStickInput,
  } = useStickInputState()

  const { setup: setupMouseRightStick, tearDown: tearDownMouseRightStick } = useMouseRightStick({
    videoRef,
    onPointerLockChange: setPointerLock,
    onMouseMove: setMouseVelocity,
  })

  useEffect(() => {
    const unsubscribe = onRumbleSettingsChange((settings) => {
      rumbleSettingsRef.current = settings
    })
    return () => {
      unsubscribe()
    }
  }, [])

  const lastSentRef = useRef<{ leftX: number; leftY: number; rightX: number; rightY: number; l2: number; r2: number; timestamp: number }>({
    leftX: 0,
    leftY: 0,
    rightX: 0,
    rightY: 0,
    l2: 0,
    r2: 0,
    timestamp: 0,
  })

  const stickProcessingActiveRef = useRef<boolean>(false)
  const stickIntervalRef = useRef<number | null>(null)
  const { isEnabled: isGamepadEnabled } = useGamepad()
  const statsIntervalRef = useRef<number | null>(null)
  const isStatsEnabledRef = useRef<boolean>(false)
  const previousStatsRef = useRef<{
    timestamp: number
    bytesReceived: number
    bytesSent: number
    videoBytesReceived: number
  } | null>(null)
  const webrtcSessionIdRef = useRef<string | null>(null)
  const isStreamBoundRef = useRef<boolean>(false)
  const hasVideoTrackRef = useRef<boolean>(false)
  const keyframeMonitorIntervalRef = useRef<number | null>(null)
  const lastVideoActivityRef = useRef<number>(0)
  const lastDecodedFrameCountRef = useRef<number | null>(null)
  const lastPlaybackPositionRef = useRef<number | null>(null)
  const lastKeyframeRequestRef = useRef<number>(0)
  const pendingKeyframeRequestRef = useRef<boolean>(false)
  const initialKeyframeRequestedRef = useRef<boolean>(false)
  const remotePlaySessionIdRef = useRef<string | null>(null)
  const lastStreamHealthRef = useRef<{ frozen: number; recovered: number } | null>(null)
  const healthCheckInFlightRef = useRef<boolean>(false)
  const lastHealthCheckAtRef = useRef<number>(0)
  const lastNeutralHealthKeyframeRef = useRef<number>(0)

  const KEYFRAME_REQUEST_COOLDOWN_MS = 8000
  const HEALTH_CHECK_COOLDOWN_MS = 3000
  // 过去用于自动触发 neutral 关键帧的冷却时间（已不再使用）
  // const HEALTH_NEUTRAL_KEYFRAME_COOLDOWN_MS = 5000

  const requestKeyframe = useCallback(
    (reason: string) => {
      const now = Date.now()
      const sessionId = webrtcSessionIdRef.current || webrtcSessionId
      if (!sessionId) {
        console.debug('⚠️ 无法请求关键帧，缺少 SessionId', { reason })
        return false
      }

      if (!isStreamBoundRef.current) {
        console.debug('⚠️ 无法请求关键帧，会话尚未绑定远程流', { reason })
        return false
      }

      if (pendingKeyframeRequestRef.current) {
        console.debug('⚠️ 关键帧请求进行中，跳过', { reason })
        return false
      }

      if (now - lastKeyframeRequestRef.current < KEYFRAME_REQUEST_COOLDOWN_MS) {
        console.debug('⚠️ 关键帧请求冷却中', {
          reason,
          elapsed: now - lastKeyframeRequestRef.current,
        })
        return false
      }

      lastKeyframeRequestRef.current = now
      pendingKeyframeRequestRef.current = true

      console.warn('⚠️ 触发关键帧请求', {
        reason,
        sessionId,
      })

      const sendKeyframeRequest = async () => {
        try {
          const signalrResult = await streamingHubService.requestKeyframe(sessionId)
          if (!signalrResult) {
            console.warn('⚠️ SignalR 请求关键帧未成功，尝试使用 HTTP 备用方案', { reason })
            const response = await streamingService.requestKeyframe(sessionId)
            if (!response.success) {
              console.warn('⚠️ HTTP 关键帧请求失败', {
                reason,
                message: response.message,
                error: response.errorMessage,
              })
            } else {
              console.log('🎯 HTTP 关键帧请求已发送', { reason })
            }
          } else {
            console.log('🎯 SignalR 关键帧请求已发送', { reason })
          }
        } catch (error) {
          console.error('❌ 请求关键帧失败，尝试使用 HTTP 备用方案', error, { reason })
          try {
            const response = await streamingService.requestKeyframe(sessionId)
            if (!response.success) {
              console.warn('⚠️ HTTP 关键帧请求失败', {
                reason,
                message: response.message,
                error: response.errorMessage,
              })
            } else {
              console.log('🎯 HTTP 关键帧请求已发送', { reason })
            }
          } catch (httpError) {
            console.error('❌ HTTP 请求关键帧异常:', httpError, { reason })
          }
        } finally {
          pendingKeyframeRequestRef.current = false
        }
      }

      void sendKeyframeRequest()

      return true
    },
    [KEYFRAME_REQUEST_COOLDOWN_MS, webrtcSessionId]
  )

  // 向外暴露的手动刷新方法（请求关键帧）
  const refreshStream = useCallback(() => {
    const ok = requestKeyframe('manual-refresh')
    if (!ok) {
      try {
        toast({
          title: t('streaming.refresh.unavailableTitle', '无法刷新'),
          description: t('streaming.refresh.unavailableDesc', '当前会话不可用或仍在冷却中'),
          variant: 'destructive',
        })
      } catch {
        // ignore toast failure in environments without i18n/toast
      }
    } else {
      try {
        toast({
          title: t('streaming.refresh.sentTitle', '已发送刷新请求'),
          description: t('streaming.refresh.sentDesc', '请稍候，尝试恢复画面'),
        })
      } catch {
        // ignore
      }
    }
    return ok
  }, [requestKeyframe, t, toast])

  const resolveWebrtcSessionId = useCallback(() => {
    if (webrtcSessionIdRef.current) {
      return webrtcSessionIdRef.current
    }

    if (webrtcSessionId) {
      webrtcSessionIdRef.current = webrtcSessionId
      return webrtcSessionId
    }

    return null
  }, [webrtcSessionId])

  const resolveRemotePlaySessionId = useCallback(() => {
    if (remotePlaySessionIdRef.current) {
      return remotePlaySessionIdRef.current
    }

    if (remotePlaySessionId) {
      remotePlaySessionIdRef.current = remotePlaySessionId
      return remotePlaySessionId
    }

    return null
  }, [remotePlaySessionId])

  const handleStreamHealthCheck = useCallback(
    async (reason: string, context?: { forceNeutral?: boolean }) => {
      const streamSessionId = resolveRemotePlaySessionId()
      if (!streamSessionId) {
        return
      }

      if (!resolveWebrtcSessionId()) {
        return
      }

      const now = Date.now()
      if (healthCheckInFlightRef.current) {
        return
      }

      const forceNeutral = context?.forceNeutral ?? false

      if (now - lastHealthCheckAtRef.current < HEALTH_CHECK_COOLDOWN_MS) {
        const snapshot = lastStreamHealthRef.current
        if (snapshot && snapshot.frozen > snapshot.recovered) {
          // 冻结时不再自动请求关键帧，只记录最后活动时间用于节流
          lastVideoActivityRef.current = now
          return
        }

        if (forceNeutral) {
          // 冷却期内的 neutral 不再自动请求关键帧
          // 仅更新最后活动时间，避免短时间内重复触发
          lastVideoActivityRef.current = now
        }
        return
      }

      healthCheckInFlightRef.current = true
      try {
        const response = await streamingService.getStreamHealth(streamSessionId)
        if (!response.success || !response.data) {
          throw new Error(response.errorMessage || response.message || 'Unavailable stream health data')
        }

        lastHealthCheckAtRef.current = Date.now()
        const health = response.data
        const previous = lastStreamHealthRef.current
        
        // 计算增量值（基于之前存储的值）
        const deltaFrozen = previous ? Math.max(0, health.totalFrozenFrames - previous.frozen) : 0
        const deltaRecovered = previous ? Math.max(0, health.totalRecoveredFrames - previous.recovered) : 0
        
        lastStreamHealthRef.current = {
          frozen: health.totalFrozenFrames,
          recovered: health.totalRecoveredFrames,
        }

        const hasNewFreeze = deltaFrozen > 0
        const hasNewRecovery = deltaRecovered > 0

        if (hasNewFreeze || health.totalFrozenFrames > health.totalRecoveredFrames) {
          console.warn('⚠️ 流健康检测到画面冻结（已禁用自动关键帧请求）', {
            reason,
            totalFrozenFrames: health.totalFrozenFrames,
            totalRecoveredFrames: health.totalRecoveredFrames,
            deltaFrozenFrames: deltaFrozen,
          })

          // 不再自动请求关键帧，仅更新活动时间
          lastVideoActivityRef.current = Date.now()
          return
        }

        let lastHandled = false
        if (hasNewRecovery || (previous && health.totalRecoveredFrames > previous.recovered)) {
          console.log('✅ 流媒体帧已恢复', {
            reason,
            totalRecoveredFrames: health.totalRecoveredFrames,
            totalFrozenFrames: health.totalFrozenFrames,
            deltaRecoveredFrames: deltaRecovered,
          })
          lastHandled = true
          lastVideoActivityRef.current = Date.now()
        }

        if (!lastHandled && (health.totalFrozenFrames > 0 || forceNeutral)) {
          // 不再自动请求 neutral 关键帧，仅更新时间与 neutral 时间戳
          lastVideoActivityRef.current = Date.now()
          lastNeutralHealthKeyframeRef.current = now
        }
      } catch (error) {
        lastHealthCheckAtRef.current = Date.now()
        console.warn('⚠️ 获取流健康状态失败（已禁用自动关键帧回退）', error)
        if (error instanceof Error && /不存在或已结束/.test(error.message)) {
          remotePlaySessionIdRef.current = null
        }
        // 不再自动回退请求关键帧，仅更新活动时间
        lastVideoActivityRef.current = Date.now()
      } finally {
        healthCheckInFlightRef.current = false
      }
    },
    [
      HEALTH_CHECK_COOLDOWN_MS,
      resolveRemotePlaySessionId,
      resolveWebrtcSessionId,
      requestKeyframe,
      t,
      toast,
    ]
  )

  const applyReceiverLatencyHints = useCallback((receiver: RTCRtpReceiver) => {
    const anyReceiver = receiver as any
    const trackKind = receiver.track?.kind
    const isAudioTrack = trackKind === 'audio'
    const preferredDelay = isAudioTrack ? 0.12 : 0
    try {
      if (typeof anyReceiver?.playoutDelayHint === 'number') {
        anyReceiver.playoutDelayHint = preferredDelay
      }
      if (typeof anyReceiver?.jitterBufferDelayHint === 'number') {
        anyReceiver.jitterBufferDelayHint = preferredDelay
      }
    } catch (error) {
      console.warn('⚠️ 设置接收器延迟提示失败:', error)
    }
  }, [])

  const reinforceLatencyHints = useCallback(
    (pc: RTCPeerConnection | null) => {
      if (!pc) return
      try {
        pc.getReceivers().forEach((receiver) => applyReceiverLatencyHints(receiver))
      } catch (error) {
        console.warn('⚠️ 刷新接收器延迟提示失败:', error)
      }
    },
    [applyReceiverLatencyHints]
  )

  const stopStickProcessing = useCallback(() => {
    if (stickIntervalRef.current !== null) {
      clearInterval(stickIntervalRef.current)
      stickIntervalRef.current = null
    }
    stickProcessingActiveRef.current = false
    resetStickInput()
    lastSentRef.current = { leftX: 0, leftY: 0, rightX: 0, rightY: 0, l2: 0, r2: 0, timestamp: 0 }
  }, [resetStickInput])

  const collectConnectionStats = useCallback(async () => {
    if (!isStatsEnabledRef.current) {
      return
    }

    const peerConnection = peerConnectionRef.current
    if (!peerConnection) {
      return
    }

    try {
      const statsReport = await peerConnection.getStats()

      let totalInboundBytes = 0
      let totalOutboundBytes = 0
      let videoInboundBytes = 0
      let frameWidth: number | null = null
      let frameHeight: number | null = null
      let latencyMs: number | null = null

      statsReport.forEach((report) => {
        const anyReport = report as any

        if (report.type === 'inbound-rtp' && !report.isRemote) {
          const bytesReceived = typeof anyReport.bytesReceived === 'number' ? anyReport.bytesReceived : 0
          totalInboundBytes += bytesReceived

          if (anyReport.kind === 'video') {
            videoInboundBytes += bytesReceived
            if (typeof anyReport.frameWidth === 'number') {
              frameWidth = anyReport.frameWidth
            }
            if (typeof anyReport.frameHeight === 'number') {
              frameHeight = anyReport.frameHeight
            }
          }
        }

        if (report.type === 'outbound-rtp' && !report.isRemote) {
          const bytesSent = typeof anyReport.bytesSent === 'number' ? anyReport.bytesSent : 0
          totalOutboundBytes += bytesSent
        }

        if (report.type === 'candidate-pair' && anyReport.state === 'succeeded' && anyReport.nominated) {
          if (typeof anyReport.currentRoundTripTime === 'number') {
            latencyMs = anyReport.currentRoundTripTime * 1000
          }
        }
      })

      const now = performance.now()
      const previous = previousStatsRef.current

      if (!previous) {
        previousStatsRef.current = {
          timestamp: now,
          bytesReceived: totalInboundBytes,
          bytesSent: totalOutboundBytes,
          videoBytesReceived: videoInboundBytes,
        }

        setConnectionStats((prev) => ({
          downloadKbps: prev?.downloadKbps ?? null,
          uploadKbps: prev?.uploadKbps ?? null,
          videoBitrateKbps: prev?.videoBitrateKbps ?? null,
          resolution:
            frameWidth !== null && frameHeight !== null
              ? { width: frameWidth, height: frameHeight }
              : prev?.resolution ?? null,
          latencyMs: latencyMs ?? prev?.latencyMs ?? null,
        }))

        return
      }

      const elapsedSeconds = (now - previous.timestamp) / 1000
      if (elapsedSeconds <= 0) {
        return
      }

      const downloadDiff = Math.max(0, totalInboundBytes - previous.bytesReceived)
      const uploadDiff = Math.max(0, totalOutboundBytes - previous.bytesSent)
      const videoDiff = Math.max(0, videoInboundBytes - previous.videoBytesReceived)

      const downloadKbps = downloadDiff > 0 ? (downloadDiff * 8) / elapsedSeconds / 1000 : 0
      const uploadKbps = uploadDiff > 0 ? (uploadDiff * 8) / elapsedSeconds / 1000 : 0
      const videoBitrateKbps = videoDiff > 0 ? (videoDiff * 8) / elapsedSeconds / 1000 : 0

      previousStatsRef.current = {
        timestamp: now,
        bytesReceived: totalInboundBytes,
        bytesSent: totalOutboundBytes,
        videoBytesReceived: videoInboundBytes,
      }

      setConnectionStats((prev) => ({
        downloadKbps: Number.isFinite(downloadKbps) ? downloadKbps : prev?.downloadKbps ?? null,
        uploadKbps: Number.isFinite(uploadKbps) ? uploadKbps : prev?.uploadKbps ?? null,
        videoBitrateKbps: Number.isFinite(videoBitrateKbps) ? videoBitrateKbps : prev?.videoBitrateKbps ?? null,
        resolution:
          frameWidth !== null && frameHeight !== null
            ? { width: frameWidth, height: frameHeight }
            : prev?.resolution ?? null,
        latencyMs: latencyMs ?? prev?.latencyMs ?? null,
      }))
    } catch (error) {
      console.warn('获取 WebRTC 统计信息失败:', error)
    }
  }, [])

  const prepareDevice = useCallback(async (): Promise<boolean> => {
    if (!hostId) {
      return false
    }

    try {
      setConnectionState(t('streaming.connection.state.fetchingDevice'))
      const devicesResponse = await playStationService.getMyDevices()
      if (!devicesResponse.success || !devicesResponse.result) {
        throw new Error(t('streaming.connection.errors.fetchDeviceFailed'))
      }

      const device = devicesResponse.result.find((d) => d.hostId === hostId)
      if (!device) {
        throw new Error(t('streaming.connection.errors.deviceNotFound'))
      }

      if (!device.ipAddress) {
        throw new Error(t('streaming.connection.errors.ipNotSet'))
      }

      const deviceIp = device.ipAddress

      setConnectionState(t('streaming.connection.state.checkingStatus'))
      let firstStatusCheck = await playStationService.discoverDevice(deviceIp, 5000).catch(() => {
        console.warn('首次状态查询失败，将在等待循环中继续查询...')
        return { success: false, result: null }
      })

      if (!firstStatusCheck.success || !firstStatusCheck.result) {
        console.warn('首次状态查询失败，重试一次...')
        await new Promise((resolve) => setTimeout(resolve, 1000))
        firstStatusCheck = await playStationService.discoverDevice(deviceIp, 5000).catch(() => {
          console.warn('首次状态查询重试也失败，将在等待循环中继续查询...')
          return { success: false, result: null }
        })
      }

      let needWaitForReady = false
      if (firstStatusCheck.success && firstStatusCheck.result) {
        const deviceStatus = firstStatusCheck.result.status?.toUpperCase() || ''
        console.log('设备当前状态:', deviceStatus)

        if (deviceStatus.includes('STANDBY')) {
          setConnectionState(t('streaming.connection.state.wakingUp'))
          toast({
            title: t('streaming.connection.toast.wakingTitle'),
            description: t('streaming.connection.toast.wakingDescription'),
          })

          const wakeResponse = await playStationService.wakeUpConsole(hostId)
          if (!wakeResponse.success || !wakeResponse.result) {
            throw new Error(t('streaming.connection.errors.wakeDeviceFailed'))
          }

          console.log('✅ 设备唤醒命令已发送，等待设备就绪...')
          needWaitForReady = true
        } else if (deviceStatus === 'OK' || deviceStatus.includes('READY') || deviceStatus.includes('AVAILABLE')) {
          console.log('✅ 设备已就绪，状态:', deviceStatus)
          return true
        } else {
          console.log('⚠️ 设备状态:', deviceStatus, '，等待设备就绪...')
          needWaitForReady = true
        }
      } else {
        console.log('⚠️ 首次状态查询失败，等待设备就绪...')
        needWaitForReady = true
      }

      if (needWaitForReady) {
        setConnectionState(t('streaming.connection.state.waitingReady'))
        const timeout = 30000
        const checkInterval = 1000
        const startTime = Date.now()

        console.log('🔄 开始主动查询设备状态...')

        while (Date.now() - startTime < timeout) {
          try {
            const elapsed = Math.floor((Date.now() - startTime) / 1000)
            console.log(`📡 主动查询设备状态... (${elapsed}s)`)
            const statusResponse = (await Promise.race([
              playStationService.discoverDevice(deviceIp, 5000),
              new Promise((_, reject) => setTimeout(() => reject(new Error('查询超时')), 6000)),
            ]).catch((error) => {
              console.log(`⚠️ 设备状态查询超时或失败 (${elapsed}s):`, error)
              return { success: false, result: null }
            })) as any

            if (statusResponse.success && statusResponse.result) {
              const currentStatus = statusResponse.result.status?.toUpperCase() || ''
              console.log(`✅ 设备状态检查 (${elapsed}s):`, currentStatus)

              if (currentStatus === 'OK' || currentStatus.includes('READY') || currentStatus.includes('AVAILABLE')) {
                console.log('✅ 设备已就绪，状态:', currentStatus)
                return true
              } else {
                console.log(`⏳ 设备尚未就绪，当前状态: ${currentStatus}，继续等待...`)
              }
            } else {
              console.log(`⚠️ 设备状态查询失败 (${elapsed}s)，继续尝试...`)
            }
          } catch (queryError) {
            const elapsed = Math.floor((Date.now() - startTime) / 1000)
            console.log(`⚠️ 设备状态查询异常 (${elapsed}s):`, queryError, '，继续尝试...')
          }

          const elapsed = Math.floor((Date.now() - startTime) / 1000)
          setConnectionState(t('streaming.connection.state.waitingReadyWithTime', { seconds: elapsed }))

          if (Date.now() - startTime >= timeout) {
            break
          }

          await new Promise((resolve) => setTimeout(resolve, checkInterval))
        }

        const finalElapsed = Math.floor((Date.now() - startTime) / 1000)
        console.error(`❌ 设备就绪超时（${finalElapsed}秒）`)
        throw new Error(t('streaming.connection.errors.deviceReadyTimeout', { seconds: finalElapsed }))
      }

      return false
    } catch (error) {
      console.error('设备准备失败:', error)
      const errorMessage = error instanceof Error ? error.message : t('streaming.connection.errors.unknown')
      const normalizedErrorMessage = errorMessage.toLowerCase()
      if (normalizedErrorMessage.includes('timeout') || errorMessage.includes('超时')) {
        toast({
          title: t('streaming.connection.toast.prepareFailedTitle'),
          description: errorMessage,
          variant: 'destructive',
        })
      } else {
        console.warn('设备准备遇到错误，但继续等待:', errorMessage)
      }
      return false
    }
  }, [hostId, t, toast])

  const setupKeyboardControl = useCallback(() => {
    if (keyboardCleanupRef.current) {
      keyboardCleanupRef.current()
      keyboardCleanupRef.current = null
    }

    const cleanup = createKeyboardHandler(
      async (buttonName: string, action: 'press' | 'release') => {
        console.log('🎮 键盘控制触发:', buttonName, action, {
          isConnected: controllerService.isConnected(),
          buttonName,
          action,
        })

        try {
          let retries = 0
          const maxRetries = 10
          while (!controllerService.isConnected() && retries < maxRetries) {
            await new Promise((resolve) => setTimeout(resolve, 100))
            retries++
          }

          if (!controllerService.isConnected()) {
            console.warn('⚠️ 控制器未就绪，但尝试发送按键:', buttonName, action)
          }

          console.log('📤 发送按钮命令:', buttonName, action)
          if (action === 'press') {
            await controllerService.sendButton(buttonName, 'press')
            console.log('✅ 按钮命令发送成功:', buttonName, 'press')
          } else {
            await controllerService.sendButton(buttonName, 'release')
            console.log('✅ 按钮命令发送成功:', buttonName, 'release')
          }
        } catch (error) {
          console.error('❌ 键盘控制失败:', error, '按钮:', buttonName, '动作:', action)
        }
      },
      {
        onLeftStickChange: (x: number, y: number) => {
          setKeyboardLeftStick(x, y)
        },
      }
    )

    keyboardCleanupRef.current = cleanup
    console.log('✅ 键盘控制已启用')
  }, [])

  const connectController = useCallback(
    async (sessionId: string) => {
      try {
        const stateUnsubscribe = controllerService.onStateChange((state) => {
          if (state.isConnected && !state.isConnecting) {
            console.log('✅ 控制器状态：已连接且就绪')
            if (!keyboardCleanupRef.current) {
              setupKeyboardControl()
            }
            stateUnsubscribe()
          }
        })

        await controllerService.connect(sessionId)
        console.log('✅ 控制器连接成功')

        if (controllerService.isConnected()) {
          console.log('✅ 控制器已就绪，立即启用键盘控制')
          setupKeyboardControl()
          stateUnsubscribe()
        } else {
          let waitCount = 0
          const maxWait = 20
          while (!controllerService.isConnected() && waitCount < maxWait) {
            await new Promise((resolve) => setTimeout(resolve, 100))
            waitCount++
          }

          if (controllerService.isConnected()) {
            console.log('✅ 控制器已就绪，启用键盘控制')
            setupKeyboardControl()
            stateUnsubscribe()
          } else {
            console.warn('⚠️ 控制器未完全就绪，但仍启用键盘控制（将自动重试）')
            setupKeyboardControl()
          }
        }
      } catch (error) {
        console.error('❌ 控制器连接失败:', error)
        toast({
          title: t('streaming.connection.toast.controllerFailedTitle'),
          description: error instanceof Error ? error.message : t('streaming.connection.errors.unknown'),
          variant: 'destructive',
        })
        setupKeyboardControl()
      }
    },
    [setupKeyboardControl, t, toast]
  )

  const startStickProcessing = useCallback(() => {
    if (stickProcessingActiveRef.current) {
      return
    }

    stickProcessingActiveRef.current = true
    lastSentRef.current.timestamp = 0

    const readGamepadAxes = () => {
      try {
        const gamepads = navigator.getGamepads?.()
        if (!gamepads) {
          return
        }

        for (let i = 0; i < gamepads.length; i++) {
          const gamepad = gamepads[i]
          if (!gamepad) {
            continue
          }

          snapshotGamepadAxes(gamepad)
          break
        }
      } catch (error) {
        console.warn('⚠️ 读取手柄状态失败:', error)
      }
    }

    const sendLatest = () => {
      if (!isConnectedRef.current || !controllerService.isConnected() || !gamepadEnabledRef.current || !isGamepadEnabled) {
        return
      }

      readGamepadAxes()

      const now = performance.now()
      const normalized = getNormalizedState()
      const lastSent = lastSentRef.current
      const stickDiff =
        Math.abs(normalized.leftX - lastSent.leftX) +
        Math.abs(normalized.leftY - lastSent.leftY) +
        Math.abs(normalized.rightX - lastSent.rightX) +
        Math.abs(normalized.rightY - lastSent.rightY)
      const triggerDiff = Math.abs(normalized.l2 - lastSent.l2) + Math.abs(normalized.r2 - lastSent.r2)
      const shouldHeartbeat = now - lastSent.timestamp >= MAX_HEARTBEAT_INTERVAL_MS
      const shouldSendSticks = stickDiff > AXIS_DEADZONE || shouldHeartbeat
      const shouldSendTriggers = triggerDiff > TRIGGER_DEADZONE || shouldHeartbeat

      if (shouldSendSticks) {
        controllerService.sendSticks(normalized.leftX, normalized.leftY, normalized.rightX, normalized.rightY).catch((error) => {
          console.error('❌ 发送摇杆输入失败:', error)
        })
      }

      if (shouldSendTriggers) {
        controllerService.sendTriggers(normalized.l2, normalized.r2).catch((error) => {
          console.error('❌ 发送扳机压力失败:', error)
        })
      }

      if (shouldSendSticks || shouldSendTriggers) {
        lastSentRef.current = { ...normalized, timestamp: now }
      }
    }

    sendLatest()
    // 移动端使用更长的发送间隔以优化性能
    const sendInterval = isMobileDevice() ? MOBILE_SEND_INTERVAL_MS : SEND_INTERVAL_MS
    stickIntervalRef.current = window.setInterval(sendLatest, sendInterval)
  }, [getNormalizedState, isGamepadEnabled])

  const handleGamepadInput = useCallback(
    async (event: GamepadInputEvent) => {
      if (!isConnectedRef.current || !controllerService.isConnected() || !gamepadEnabledRef.current || !isGamepadEnabled) {
        return
      }

      try {
        if (event.buttonIndex !== undefined && event.buttonState) {
          const buttonIndex = event.buttonIndex
          const buttonState = event.buttonState
          const isPressed = buttonState.pressed
          const psButtonName = PS5_BUTTON_MAP[buttonIndex as GamepadButton]

          if (buttonIndex === GamepadButton.LeftTrigger) {
            setTriggerPressure('l2', buttonState.value ?? 0)
          } else if (buttonIndex === GamepadButton.RightTrigger) {
            setTriggerPressure('r2', buttonState.value ?? 0)
          }

          if (psButtonName) {
            const action = isPressed ? 'press' : 'release'
            console.log('🎮 手柄按钮输入:', {
              buttonIndex,
              psButtonName,
              action,
              value: buttonState.value,
            })
            await controllerService.sendButton(psButtonName, action)
          } else if (buttonIndex >= 12 && buttonIndex <= 15) {
            const dpadMap: Record<number, string> = {
              12: 'up',
              13: 'down',
              14: 'left',
              15: 'right',
            }
            const dpadButton = dpadMap[buttonIndex]
            if (dpadButton) {
              const action = isPressed ? 'press' : 'release'
              await controllerService.sendButton(dpadButton, action)
            }
          }
        }

        if (event.axisIndex !== undefined && event.axisValue !== undefined) {
          handleGamepadAxis(event.axisIndex, event.axisValue)

          const now = performance.now()
          const normalized = getNormalizedState()
          const lastSent = lastSentRef.current
          const stickDiff =
            Math.abs(normalized.leftX - lastSent.leftX) +
            Math.abs(normalized.leftY - lastSent.leftY) +
            Math.abs(normalized.rightX - lastSent.rightX) +
            Math.abs(normalized.rightY - lastSent.rightY)
          const triggerDiff = Math.abs(normalized.l2 - lastSent.l2) + Math.abs(normalized.r2 - lastSent.r2)
          // 移动端使用更长的发送间隔
          const sendInterval = isMobileDevice() ? MOBILE_SEND_INTERVAL_MS : SEND_INTERVAL_MS
          const shouldHeartbeat = now - lastSent.timestamp >= sendInterval
          const shouldSendSticks = stickDiff > AXIS_DEADZONE || shouldHeartbeat
          const shouldSendTriggers = triggerDiff > TRIGGER_DEADZONE || shouldHeartbeat

          if (shouldSendSticks) {
            controllerService.sendSticks(normalized.leftX, normalized.leftY, normalized.rightX, normalized.rightY).catch((error) => {
              console.error('❌ 发送摇杆输入失败:', error)
            })
          }

          if (shouldSendTriggers) {
            controllerService.sendTriggers(normalized.l2, normalized.r2).catch((error) => {
              console.error('❌ 发送扳机压力失败:', error)
            })
          }

          if (shouldSendSticks || shouldSendTriggers) {
            lastSentRef.current = { ...normalized, timestamp: now }
          }
        }
      } catch (error) {
        console.error('❌ 手柄输入处理失败:', error)
      }
    },
    [getNormalizedState, isGamepadEnabled, setTriggerPressure]
  )

  const disconnect = useCallback(() => {
    stopStickProcessing()
    gamepadEnabledRef.current = false
    tearDownMouseRightStick()

    isStreamBoundRef.current = false
    hasVideoTrackRef.current = false

    if (videoOptimizeCleanupRef.current) {
      videoOptimizeCleanupRef.current()
      videoOptimizeCleanupRef.current = null
    }

    if (keyframeMonitorIntervalRef.current !== null) {
      window.clearInterval(keyframeMonitorIntervalRef.current)
      keyframeMonitorIntervalRef.current = null
    }
    lastVideoActivityRef.current = 0
    lastDecodedFrameCountRef.current = null
    lastPlaybackPositionRef.current = null
    lastKeyframeRequestRef.current = 0
    pendingKeyframeRequestRef.current = false
    initialKeyframeRequestedRef.current = false

    if (keyboardCleanupRef.current) {
      keyboardCleanupRef.current()
      keyboardCleanupRef.current = null
    }

    controllerService.disconnect().catch(() => {})
    // ✅ 清理 ICE Restart 相关资源
    if (typeof window !== 'undefined') {
      // 清理会在组件卸载时自动处理
    }
    
    // ✅ 清理 ICE Restart 相关资源
    if (iceRestartTimeoutRef.current !== null) {
      window.clearTimeout(iceRestartTimeoutRef.current)
      iceRestartTimeoutRef.current = null
    }
    
    // ✅ 清理 SignalR 事件监听
    streamingHubService.onIceRestartOffer = undefined
    streamingHubService.onIceRestartFailed = undefined
    
    streamingHubService.disconnect().catch(() => {})

    if (peerConnectionRef.current) {
      peerConnectionRef.current.close()
      peerConnectionRef.current = null
    }

    previousStatsRef.current = null
    setConnectionStats(null)

    if (videoRef.current) {
      videoRef.current.srcObject = null
    }

    const currentWebrtcSessionId = webrtcSessionId
    if (currentWebrtcSessionId) {
      setWebrtcSessionId(null)
      webrtcSessionIdRef.current = null
      streamingService
        .deleteSession(currentWebrtcSessionId)
        .then(() => {
          console.log('✅ WebRTC Session 已关闭')
        })
        .catch((error) => {
          console.error('❌ 关闭 WebRTC Session 失败:', error)
        })
    }

    const currentRemotePlaySessionId = remotePlaySessionId
    if (currentRemotePlaySessionId) {
      setRemotePlaySessionId(null)
      apiRequest(`/playstation/stop-session?sessionId=${encodeURIComponent(currentRemotePlaySessionId)}`, {
        method: 'POST',
      })
        .then(() => {
          console.log('✅ Remote Play Session 已关闭')
        })
        .catch((error) => {
          console.error('❌ 关闭 Remote Play Session 失败:', error)
        })
    }

    setIsConnected(false)
    isConnectedRef.current = false
    setIsConnecting(false)
    setConnectionState(t('streaming.connection.state.disconnected'))
  }, [remotePlaySessionId, stopStickProcessing, t, videoRef, webrtcSessionId])

  const connect = useCallback(async () => {
    if (!hostId) {
      toast({
        title: t('common.error'),
        description: t('streaming.connection.errors.missingDeviceInfo'),
        variant: 'destructive',
      })
      return
    }

    if (isConnecting || isConnected) {
      return
    }

    if (!hasAttemptedInitialConnectRef.current) {
      hasAttemptedInitialConnectRef.current = true
    }

    setIsConnecting(true)
    setConnectionState(t('streaming.connection.state.connecting'))

    try {
      const deviceReady = await prepareDevice()
      if (!deviceReady) {
        throw new Error(t('streaming.connection.errors.deviceNotReady'))
      }

      setConnectionState(t('streaming.connection.state.creatingSession'))
      toast({
        title: t('streaming.connection.toast.connectingTitle'),
        description: t('streaming.connection.toast.connectingDescription', { name: deviceName }),
      })

      const sessionResponse = await streamingService.startSession(hostId)
      console.log('会话创建响应:', sessionResponse)
      console.log('响应数据字段:', {
        success: sessionResponse.success,
        hasData: !!sessionResponse.data,
        hasResult: !!sessionResponse.result,
        data: sessionResponse.data,
        result: sessionResponse.result,
      })

      if (!sessionResponse.success) {
        throw new Error(
          sessionResponse.errorMessage ||
            sessionResponse.message ||
            t('streaming.connection.errors.sessionCreateFailed')
        )
      }

      const sessionData = sessionResponse.data || sessionResponse.result
      if (!sessionData) {
        console.error('会话响应中没有 data 或 result 字段:', sessionResponse)
        throw new Error(t('streaming.connection.errors.sessionDataMissing'))
      }

      const sessionId = sessionData.id || sessionData.Id || sessionData.sessionId || sessionData.session_id

      console.log('提取的 Session ID:', sessionId, '完整数据:', sessionData)

      setRemotePlaySessionId(sessionId)

      if (!sessionId) {
        console.error('无法从响应中提取 Session ID，可用字段:', Object.keys(sessionData))
        throw new Error(t('streaming.connection.errors.sessionIdMissing'))
      }

      const offerResponse = await streamingService.createOffer({
        remotePlaySessionId: sessionId,
        preferLanCandidates: isLikelyLan,
      })
      console.log('Offer 响应:', offerResponse)

      if (!offerResponse.success) {
        throw new Error(
          offerResponse.errorMessage || offerResponse.message || t('streaming.connection.errors.offerCreateFailed')
        )
      }

      const offerData = offerResponse.data || offerResponse.result
      if (!offerData) {
        console.error('Offer 响应中没有 data 或 result 字段:', offerResponse)
        throw new Error(t('streaming.connection.errors.offerDataMissing'))
      }

      const { sessionId: webrtcSessionIdValue, sdp: offerSdp } = offerData
      setWebrtcSessionId(webrtcSessionIdValue)
      webrtcSessionIdRef.current = webrtcSessionIdValue

      // 默认的 STUN 服务器列表
      const defaultIceServers: RTCIceServer[] = [
        { urls: 'stun:stun.l.google.com:19302' },
      ]

      // 获取用户配置的 TURN 服务器
      let turnServers: RTCIceServer[] = []
      try {
        const turnConfigResponse = await streamingService.getTurnConfig()
        if (turnConfigResponse.success && turnConfigResponse.data) {
          const turnConfig = turnConfigResponse.data
          if (turnConfig.turnServers && turnConfig.turnServers.length > 0) {
            turnServers = turnConfig.turnServers
              .filter((server) => server.url) // 过滤掉没有 URL 的服务器
              .map((server) => {
                const iceServer: RTCIceServer = {
                  urls: server.url!,
                }
                if (server.username) {
                  iceServer.username = server.username
                }
                if (server.credential) {
                  iceServer.credential = server.credential
                }
                return iceServer
              })
            console.log('✅ 加载了用户配置的 TURN 服务器:', turnServers.length, '个')
          }
        }
      } catch (error) {
        console.warn('⚠️ 获取 TURN 配置失败，使用默认配置:', error)
      }

      // 合并 STUN 和 TURN 服务器配置
      // TURN 服务器优先，因为它们在 NAT 穿透方面更可靠
      const iceServers: RTCIceServer[] = [...turnServers, ...defaultIceServers]

      console.log('🔧 RTCPeerConnection 配置:', {
        iceServers: iceServers.map((s) => ({
          urls: s.urls,
          username: s.username ? '***' : undefined,
          credential: s.credential ? '***' : undefined,
        })),
        iceCandidatePoolSize: isLikelyLan ? 1 : 4,
        bundlePolicy: 'max-bundle',
        rtcpMuxPolicy: 'require',
      })

      const peerConnection = new RTCPeerConnection({
        iceServers,
        iceCandidatePoolSize: isLikelyLan ? 1 : 4,
        bundlePolicy: 'max-bundle',
        rtcpMuxPolicy: 'require',
      })
      
      // ✅ 监听 DataChannel 事件（用于 keepalive）
      // 注意：DataChannel 由后端在 createOffer 前创建，前端只需要监听
      peerConnection.ondatachannel = (event) => {
        const channel = event.channel
        console.log('📡 收到 DataChannel:', {
          label: channel.label,
          id: channel.id,
          readyState: channel.readyState,
        })
        
        // ✅ 如果是 keepalive DataChannel，监听其状态
        if (channel.label === 'keepalive') {
          channel.onopen = () => {
            console.log('✅ Keepalive DataChannel 已打开')
          }
          
          channel.onclose = () => {
            console.warn('⚠️ Keepalive DataChannel 已关闭')
          }
          
          channel.onerror = (error) => {
            console.warn('⚠️ Keepalive DataChannel 错误:', error)
          }
          
          // ✅ 监听 keepalive 消息（可选，用于确认连接活跃）
          channel.onmessage = (_event) => {
            // keepalive 消息是 1 字节的 0x00（由后端自动发送，前端只需确认收到）
            console.debug('📥 收到 Keepalive 消息')
          }
        }
      }

      console.log('✅ RTCPeerConnection 已创建:', {
        connectionState: peerConnection.connectionState,
        iceConnectionState: peerConnection.iceConnectionState,
        signalingState: peerConnection.signalingState,
        iceGatheringState: peerConnection.iceGatheringState,
      })

      try {
        const currentConfig = peerConnection.getConfiguration()
        peerConnection.setConfiguration({
          ...currentConfig,
          sdpSemantics: 'unified-plan',
        } as RTCConfiguration)
      } catch (configError) {
        console.debug('⚠️ 设置 sdpSemantics 失败，使用默认值', configError)
      }

      peerConnectionRef.current = peerConnection
      reinforceLatencyHints(peerConnection)

      const receivedTracks: { video?: MediaStreamTrack; audio?: MediaStreamTrack } = {}
      let mediaStream: MediaStream | null = null

      peerConnection.ontrack = (event) => {
        console.log('📺 收到媒体轨道:', event.track.kind, event.streams)
        console.log('📺 轨道详情:', {
          kind: event.track.kind,
          id: event.track.id,
          enabled: event.track.enabled,
          readyState: event.track.readyState,
          streamsCount: event.streams?.length || 0,
          receiver: event.receiver,
        })

        applyReceiverLatencyHints(event.receiver)
        reinforceLatencyHints(peerConnection)

        if (event.track.kind === 'video') {
          receivedTracks.video = event.track
          hasVideoTrackRef.current = true
          if (isStreamBoundRef.current) {
            if (!initialKeyframeRequestedRef.current) {
              if (requestKeyframe('initial-video-track')) {
                initialKeyframeRequestedRef.current = true
              }
            }
          } else {
            console.debug('⚠️ 已收到视频轨道，但会话尚未完成绑定，等待后续触发关键帧请求', {
              trackId: event.track.id,
            })
          }
        } else if (event.track.kind === 'audio') {
          receivedTracks.audio = event.track
        }

        if (!mediaStream) {
          mediaStream = new MediaStream()
          console.log('🎬 创建新的媒体流')
        }

        if (event.track && !mediaStream.getTracks().find((t) => t.id === event.track.id)) {
          mediaStream.addTrack(event.track)
          console.log(`✅ 已添加 ${event.track.kind} 轨道到流，当前轨道数: ${mediaStream.getTracks().length}`)
        }

        const setupVideoStream = () => {
          if (videoRef.current) {
            const video = videoRef.current

            console.log('🎥 设置视频流:', {
              videoElement: video,
              streamId: mediaStream?.id,
              tracks: mediaStream?.getTracks().map((t) => ({
                kind: t.kind,
                id: t.id,
                enabled: t.enabled,
                readyState: t.readyState,
              })),
              hasVideo: !!receivedTracks.video,
              hasAudio: !!receivedTracks.audio,
            })

            if (video.srcObject !== mediaStream) {
              video.srcObject = mediaStream
              console.log('✅ 视频源已设置')
            }

            return true
          }
          return false
        }

        const processVideoStream = (video: HTMLVideoElement) => {
          if (!mediaStream) {
            console.error('❌ 媒体流不存在')
            return
          }

          const audioTracks = mediaStream.getAudioTracks()
          const videoTracks = mediaStream.getVideoTracks()
          console.log('🎵 音频轨道:', audioTracks.length, audioTracks.map((t) => ({ id: t.id, enabled: t.enabled, readyState: t.readyState })))
          console.log('🎥 视频轨道:', videoTracks.length, videoTracks.map((t) => ({ id: t.id, enabled: t.enabled, readyState: t.readyState })))

          audioTracks.forEach((track) => {
            if (!track.enabled) {
              track.enabled = true
              console.log('✅ 已启用音频轨道:', track.id)
            }
          })
          videoTracks.forEach((track) => {
            if (!track.enabled) {
              track.enabled = true
              console.log('✅ 已启用视频轨道:', track.id)
            }
          })

          video.style.backgroundColor = '#000000'
          video.style.background = '#000000'
          video.style.display = 'block'
          video.style.visibility = 'visible'
          video.style.opacity = '1'

          console.log('🎥 视频元素样式已设置:', {
            display: video.style.display,
            visibility: video.style.visibility,
            opacity: video.style.opacity,
            computedDisplay: window.getComputedStyle(video).display,
            computedVisibility: window.getComputedStyle(video).visibility,
            computedOpacity: window.getComputedStyle(video).opacity,
          })

          const originalVolume = video.volume
          video.muted = true
          video.volume = 0
          video.autoplay = true
          video.playsInline = true

          console.log('🎥 视频播放属性设置:', {
            muted: video.muted,
            autoplay: video.autoplay,
            playsInline: video.playsInline,
            paused: video.paused,
            readyState: video.readyState,
          })

          let hasStartedPlaying = false
          const handlePlaying = () => {
            if (!hasStartedPlaying) {
              hasStartedPlaying = true
              console.log('✅ 视频开始播放，开始淡入音量')
              const fadeDurationMs = 500
              const targetVolume = originalVolume > 0 ? originalVolume : 1
              const startTime = performance.now()
              video.muted = false

              const fadeIn = (timestamp: number) => {
                const elapsed = timestamp - startTime
                const progress = Math.min(1, elapsed / fadeDurationMs)
                const volume = Math.max(0, Math.min(1, targetVolume * progress)) // 确保音量在 [0, 1] 范围内
                video.volume = volume
                if (progress < 1) {
                  requestAnimationFrame(fadeIn)
                } else {
                  video.volume = Math.max(0, Math.min(1, targetVolume)) // 确保最终音量在 [0, 1] 范围内
                  console.log('🔊 音量淡入完成，音频已启用')
                }
              }

              requestAnimationFrame(fadeIn)
            }
          }
          video.addEventListener('playing', handlePlaying, { once: true })

          const handleLoadedMetadata = () => {
            console.log('✅ 视频元数据已加载，开始播放')
            if (!video.muted) {
              video.muted = true
            }
            video
              .play()
              .then(() => {
                console.log('✅ 视频播放成功（静音模式）')
              })
              .catch((error) => {
                console.error('❌ 视频播放失败:', error)
                console.log('⚠️ 播放失败，将在 canplay 事件时重试')
              })
            video.removeEventListener('loadedmetadata', handleLoadedMetadata)
          }

          video.addEventListener('loadedmetadata', handleLoadedMetadata)

          if (video.readyState >= 1) {
            handleLoadedMetadata()
          }

          console.log('🎥 视频流已设置，等待元数据加载后播放')

          if (event.track.kind === 'video' && receivedTracks.video) {
            if (videoOptimizeCleanupRef.current) {
              videoOptimizeCleanupRef.current()
            }
            videoOptimizeCleanupRef.current = optimizeVideoForLowLatency(video)

            video.playbackRate = 1.0
            video.defaultPlaybackRate = 1.0
            const videoAny = video as any
            if (typeof videoAny?.latencyHint !== 'undefined') {
              try {
                videoAny.latencyHint = 'interactive'
                console.log('✅ 视频 latencyHint 已设置为 interactive')
              } catch (latencyError) {
                console.warn('⚠️ 设置视频 latencyHint 失败:', latencyError)
              }
            }

            console.log('✅ 视频轨道已连接，已优化低延迟播放')
          }

          if (event.track.kind === 'audio' && receivedTracks.audio) {
            console.log('🎵 音频轨道已连接')
          }

          if (!video.dataset.listenersSetup) {
            video.dataset.listenersSetup = 'true'

            video.addEventListener('loadedmetadata', () => {
              console.log('✅ 视频元数据已加载，尺寸:', video.videoWidth, 'x', video.videoHeight)
              const computedStyle = window.getComputedStyle(video)
              console.log('✅ 视频状态:', {
                readyState: video.readyState,
                paused: video.paused,
                muted: video.muted,
                currentTime: video.currentTime,
                srcObject: !!video.srcObject,
                display: computedStyle.display,
                visibility: computedStyle.visibility,
                opacity: computedStyle.opacity,
                width: video.videoWidth,
                height: video.videoHeight,
              })

              if (computedStyle.display === 'none') {
                console.warn('⚠️ 视频元素被隐藏，强制显示')
                video.style.display = 'block'
              }
              if (computedStyle.visibility === 'hidden') {
                console.warn('⚠️ 视频元素不可见，强制显示')
                video.style.visibility = 'visible'
              }
            })

            video.addEventListener('loadeddata', () => {
              console.log('✅ 视频数据已加载')
            })

            video.addEventListener('canplay', () => {
              console.log('✅ 视频可以播放')
              if (video.paused) {
                console.log('⚠️ 视频暂停中，尝试播放')
                video
                  .play()
                  .then(() => {
                    console.log('✅ canplay 事件后播放成功')
                  })
                  .catch((err) => {
                    console.error('❌ 自动播放失败:', err)
                  })
              }
            })

            video.addEventListener('canplaythrough', () => {
              console.log('✅ 视频可以流畅播放')
              if (video.paused) {
                console.log('⚠️ 视频暂停中，尝试播放（canplaythrough）')
                video
                  .play()
                  .then(() => {
                    console.log('✅ canplaythrough 事件后播放成功')
                  })
                  .catch((err) => {
                    console.error('❌ canplaythrough 播放失败:', err)
                  })
              }
            })

            video.addEventListener('playing', () => {
              console.log('✅ 视频开始播放')
              console.log('✅ 播放状态:', {
                paused: video.paused,
                currentTime: video.currentTime,
                duration: video.duration,
                videoWidth: video.videoWidth,
                videoHeight: video.videoHeight,
                srcObject: !!video.srcObject,
                display: window.getComputedStyle(video).display,
                visibility: window.getComputedStyle(video).visibility,
                opacity: window.getComputedStyle(video).opacity,
              })
              setIsConnecting(false)
              setIsConnected(true)
              isConnectedRef.current = true
              setConnectionState(t('streaming.connection.state.connected'))
              // 移动端默认不显示连接成功提示
              if (!isMobileDevice()) {
                toast({
                  title: t('streaming.connection.toast.connectedTitle'),
                  description: t('streaming.connection.toast.connectedDescription'),
                })
              }
            })

            video.addEventListener('pause', () => {
              console.warn('⚠️ 视频已暂停')
            })

            video.addEventListener('waiting', () => {
              console.warn('⚠️ 视频等待缓冲')
              if (video.paused) {
                console.log('🔄 视频暂停中，尝试恢复播放')
                video.play().catch((err) => {
                  console.error('❌ 恢复播放失败:', err)
                })
              }
              requestKeyframe('video-waiting')
            })

            video.addEventListener('stalled', () => {
              console.warn('⚠️ 视频加载停滞')
              if (video.paused) {
                console.log('🔄 视频停滞，尝试恢复播放')
                video.play().catch((err) => {
                  console.error('❌ 恢复播放失败:', err)
                })
              }
              requestKeyframe('video-stalled')
            })

            video.addEventListener('progress', () => {
              if (video.paused && video.readyState >= 2) {
                console.log('🔄 检测到缓冲数据，尝试播放')
                video.play().catch((err) => {
                  console.error('❌ 播放失败:', err)
                })
              }
            })

            video.addEventListener('error', (e) => {
              console.error('❌ 视频播放错误:', e, video.error)
              if (video.error) {
                console.error('❌ 视频错误详情:', {
                  code: video.error.code,
                  message: video.error.message,
                })
              }
            })
          }
        }

        if (setupVideoStream()) {
          const video = videoRef.current!
          processVideoStream(video)
        } else {
          console.warn('⚠️ 视频元素尚未渲染，等待渲染...')
          let attempts = 0
          const maxAttempts = 20
          const checkInterval = setInterval(() => {
            attempts++
            if (setupVideoStream()) {
              clearInterval(checkInterval)
              const video = videoRef.current!
              processVideoStream(video)
            } else if (attempts >= maxAttempts) {
              clearInterval(checkInterval)
              console.error('❌ 等待视频元素超时')
            }
          }, 100)
        }
      }

      let receivedCandidateTypes = new Set<string>()
      let hasRelayCandidate = false
      let hasSrflxCandidate = false
      let hasHostCandidate = false
      
      peerConnection.onicecandidate = async (event) => {
        if (event.candidate && webrtcSessionIdValue) {
          const candidateType = event.candidate.type || 'unknown'
          const candidateStr = event.candidate.candidate || ''
          
          // 统计候选地址类型
          receivedCandidateTypes.add(candidateType)
          if (candidateType === 'relay') {
            hasRelayCandidate = true
            console.log('🌐 收到 TURN relay 候选地址（最稳定）')
          } else if (candidateType === 'srflx') {
            hasSrflxCandidate = true
            console.log('📡 收到 STUN 服务器反射候选地址')
          } else if (candidateType === 'host') {
            hasHostCandidate = true
          }
          
          console.log('🧊 ICE Candidate 收到:', {
            candidate: candidateStr.substring(0, 80) + (candidateStr.length > 80 ? '...' : ''),
            type: candidateType,
            protocol: event.candidate.protocol,
            address: event.candidate.address,
            port: event.candidate.port,
            priority: event.candidate.priority,
            sdpMid: event.candidate.sdpMid,
            sdpMLineIndex: event.candidate.sdpMLineIndex,
            summary: {
              hasRelay: hasRelayCandidate,
              hasSrflx: hasSrflxCandidate,
              hasHost: hasHostCandidate,
            },
          })
          
          try {
            await streamingService.sendICECandidate({
              sessionId: webrtcSessionIdValue,
              candidate: event.candidate.candidate,
              sdpMid: event.candidate.sdpMid,
              sdpMLineIndex: event.candidate.sdpMLineIndex,
            })
            console.log('✅ ICE Candidate 已发送')
          } catch (error) {
            console.error('❌ 发送 ICE Candidate 失败:', error)
          }
        } else if (!event.candidate) {
          console.log('🧊 ICE Candidate gathering 完成', {
            receivedTypes: Array.from(receivedCandidateTypes),
            hasRelay: hasRelayCandidate,
            hasSrflx: hasSrflxCandidate,
            hasHost: hasHostCandidate,
            recommendation: !hasRelayCandidate && !hasSrflxCandidate 
              ? '⚠️ 只有 host 候选地址，多层 NAT 环境下可能连接不稳定'
              : hasRelayCandidate 
              ? '✅ 有 TURN relay 候选地址，连接应该更稳定'
              : hasSrflxCandidate 
              ? '⚠️ 有 STUN 反射候选地址，但无 TURN，多层 NAT 可能有问题'
              : '✅ 候选地址收集完成',
          })
        }
      }

      peerConnection.onconnectionstatechange = () => {
        const state = peerConnection.connectionState
        const iceState = peerConnection.iceConnectionState
        const signalingState = peerConnection.signalingState
        const iceGatheringState = peerConnection.iceGatheringState
        
        console.log('🔌 WebRTC 连接状态变化:', {
          connectionState: state,
          iceConnectionState: iceState,
          signalingState: signalingState,
          iceGatheringState: iceGatheringState,
          timestamp: new Date().toISOString(),
        })
        
        const localizedState =
          state === 'connected'
            ? t('streaming.connection.state.connected')
            : state === 'connecting'
            ? t('streaming.connection.state.connecting')
            : state === 'disconnected' || state === 'closed'
            ? t('streaming.connection.state.disconnected')
            : state === 'failed'
            ? t('streaming.connection.state.failed')
            : state
        setConnectionState(localizedState)
        
        if (state === 'connected') {
          console.log('✅ WebRTC 连接已建立', {
            iceConnectionState: iceState,
            signalingState: signalingState,
          })
          reinforceLatencyHints(peerConnection)
          setIsConnecting(false)
          setIsConnected(true)
          isConnectedRef.current = true

          const playCheckInterval = setInterval(() => {
            if (videoRef.current && videoRef.current.paused && videoRef.current.srcObject) {
              const video = videoRef.current
              const stream = video.srcObject as MediaStream
              if (stream && stream.getTracks().length > 0 && video.readyState >= 2) {
                console.log('🔄 定期检查：视频暂停，尝试播放')
                video.muted = true
                video
                  .play()
                  .then(() => {
                    console.log('✅ 定期检查播放成功')
                    clearInterval(playCheckInterval)
                    setTimeout(() => {
                      video.muted = false
                      console.log('🔊 已取消静音（定期检查）')
                    }, 300)
                  })
                  .catch((err) => {
                    console.warn('⚠️ 定期检查播放失败:', err)
                  })
              }
            } else if (videoRef.current && !videoRef.current.paused) {
              clearInterval(playCheckInterval)
            }
          }, 1000)

          setTimeout(() => {
            clearInterval(playCheckInterval)
          }, 10000)

          if (videoRef.current && videoRef.current.srcObject) {
            const video = videoRef.current
            const stream = video.srcObject as MediaStream
            console.log('📹 连接建立后检查视频状态:', {
              hasStream: !!stream,
              tracks: stream?.getTracks().length || 0,
              videoTracks: stream?.getVideoTracks().length || 0,
              audioTracks: stream?.getAudioTracks().length || 0,
              paused: video.paused,
              readyState: video.readyState,
            })

            if (video.paused && stream && stream.getTracks().length > 0) {
              console.log('⚠️ 视频暂停中，尝试播放')

              if (!video.muted) {
                video.muted = true
              }

              if (video.readyState >= 2) {
                video
                  .play()
                  .then(() => {
                    console.log('✅ 连接建立后播放成功')
                    setTimeout(() => {
                      video.muted = false
                      console.log('🔊 已取消静音')
                    }, 300)
                  })
                  .catch((err) => {
                    console.error('❌ 连接建立后播放失败:', err)
                  })
              } else {
                console.log('⚠️ 视频未准备好，等待 readyState >= 2 (当前:', video.readyState, ')')
              }
            }
          }
        } else if (state === 'disconnected' || state === 'failed' || state === 'closed') {
          console.warn('⚠️ WebRTC 连接断开或失败:', {
            connectionState: state,
            iceConnectionState: iceState,
            signalingState: signalingState,
            iceGatheringState: iceGatheringState,
            timestamp: new Date().toISOString(),
          })
          
          // 分析断开原因
          if (iceState === 'failed') {
            console.error('❌ 断开原因：ICE 连接失败，可能是网络不可达或 TURN 服务器问题')
          } else if (iceState === 'disconnected') {
            console.warn('⚠️ 断开原因：ICE 连接断开，可能是网络波动或 NAT 映射过期')
          } else if (state === 'failed') {
            console.error('❌ 断开原因：WebRTC 连接失败')
          }
          
          setIsConnected(false)
          isConnectedRef.current = false
          setIsConnecting(false)
        }
      }

      // ✅ ICE Restart 处理函数（使用 ref 存储状态，避免闭包问题）
      const handleIceRestart = async () => {
        if (!webrtcSessionIdRef.current) {
          console.warn('⚠️ 无法执行 ICE Restart：SessionId 为空')
          return
        }
        
        try {
          console.log('🔄 开始处理 ICE Restart...')
          
          // ✅ 方法1：尝试从后端获取待处理的 Offer
          const offer = await streamingHubService.getIceRestartOffer(webrtcSessionIdRef.current)
          
          if (offer) {
            console.log('✅ 收到 ICE Restart Offer，重新协商...')
            await handleIceRestartOffer(offer)
            return
          }
          
          // ✅ 方法2：如果后端没有待处理的 Offer，主动触发 ICE Restart
          const success = await streamingHubService.handleIceRestart(webrtcSessionIdRef.current)
          if (success) {
            // 等待后端创建新的 Offer
            setTimeout(async () => {
              const newOffer = await streamingHubService.getIceRestartOffer(webrtcSessionIdRef.current!)
              if (newOffer) {
                await handleIceRestartOffer(newOffer)
              }
            }, 1000)
          }
        } catch (error) {
          console.error('❌ ICE Restart 处理失败:', error)
        }
      }
      
      // ✅ 处理 ICE Restart Offer（在 PeerConnection 创建后定义，以便访问）
      const handleIceRestartOffer = async (offerSdp: string) => {
        const currentPeerConnection = peerConnectionRef.current
        if (!currentPeerConnection || !webrtcSessionIdRef.current) {
          console.warn('⚠️ 无法处理 ICE Restart Offer：PeerConnection 或 SessionId 为空')
          return
        }
        
        try {
          console.log('🔄 设置新的 ICE Restart Offer...')
          
          // ✅ 设置新的 remote description
          await currentPeerConnection.setRemoteDescription({
            type: 'offer',
            sdp: offerSdp,
          })
          
          // ✅ 创建新的 Answer
          const answer = await currentPeerConnection.createAnswer({
            offerToReceiveAudio: true,
            offerToReceiveVideo: true,
          })
          
          if (answer.sdp) {
            try {
              const optimizedSdp = optimizeSdpForLowLatency(answer.sdp, {
                preferLanCandidates: isLikelyLan,
              })
              if (optimizedSdp && optimizedSdp.length > 10) {
                answer.sdp = optimizedSdp
              }
            } catch (sdpError) {
              console.warn('SDP 优化出错，使用原始 SDP:', sdpError)
            }
          }
          
          await currentPeerConnection.setLocalDescription(answer)
          reinforceLatencyHints(currentPeerConnection)
          
          // ✅ 发送新的 Answer
          await streamingService.sendAnswer({
            sessionId: webrtcSessionIdRef.current,
            sdp: answer.sdp || '',
            type: 'answer',
          })
          
          console.log('✅ ICE Restart Answer 已发送')
        } catch (error) {
          console.error('❌ 处理 ICE Restart Offer 失败:', error)
        }
      }
      
      // ✅ 更新 SignalR 事件监听，使用已定义的 handleIceRestartOffer
      streamingHubService.onIceRestartOffer = handleIceRestartOffer
      
      peerConnection.oniceconnectionstatechange = () => {
        const state = peerConnection.iceConnectionState
        const connectionState = peerConnection.connectionState
        const signalingState = peerConnection.signalingState
        const iceGatheringState = peerConnection.iceGatheringState
        
        console.log('🧊 ICE 连接状态变化:', {
          iceConnectionState: state,
          connectionState: connectionState,
          signalingState: signalingState,
          iceGatheringState: iceGatheringState,
        })
        
        if (state === 'connected' || state === 'completed') {
          console.log('✅ ICE 连接已建立:', state)
          reinforceLatencyHints(peerConnection)
          
          // ✅ 连接恢复，清除断开计时器
          if (iceRestartTimeoutRef.current !== null) {
            window.clearTimeout(iceRestartTimeoutRef.current)
            iceRestartTimeoutRef.current = null
          }
          iceDisconnectedTimeRef.current = null
        } else if (state === 'failed') {
          console.error('❌ ICE 连接失败', {
            connectionState,
            signalingState,
            iceGatheringState,
          })
          
          // ✅ 延迟后尝试 ICE Restart（避免短暂抖动）
          if (iceRestartTimeoutRef.current !== null) {
            window.clearTimeout(iceRestartTimeoutRef.current)
          }
          iceRestartTimeoutRef.current = window.setTimeout(() => {
            if (peerConnection.iceConnectionState === 'failed' || 
                peerConnection.iceConnectionState === 'disconnected') {
              console.log('🔄 ICE 连接持续失败，触发 ICE Restart')
              handleIceRestart()
            }
          }, 10000) // 10秒后触发
        } else if (state === 'disconnected') {
          console.warn('⚠️ ICE 连接已断开', {
            connectionState,
            signalingState,
            iceGatheringState,
            timestamp: new Date().toISOString(),
          })
          
          // ✅ 记录断开时间
          if (iceDisconnectedTimeRef.current === null) {
            iceDisconnectedTimeRef.current = Date.now()
          }
          
          // ✅ 如果连接刚建立就断开，可能是网络不稳定或 TURN 服务器问题
          if (connectionState === 'connected' || connectionState === 'connecting') {
            console.warn('⚠️ ICE 断开时连接仍处于活跃状态，可能是网络波动或 TURN 服务器不稳定')
          }
          
          // ✅ 延迟后尝试 ICE Restart（避免短暂抖动，disconnected 持续 > 10秒才触发）
          if (iceRestartTimeoutRef.current !== null) {
            window.clearTimeout(iceRestartTimeoutRef.current)
          }
          iceRestartTimeoutRef.current = window.setTimeout(() => {
            if (peerConnection.iceConnectionState === 'disconnected' || 
                peerConnection.iceConnectionState === 'failed') {
              const disconnectedDuration = iceDisconnectedTimeRef.current ? Date.now() - iceDisconnectedTimeRef.current : 0
              if (disconnectedDuration >= 10000) {
                console.log('🔄 ICE 连接持续断开超过 10 秒，触发 ICE Restart')
                handleIceRestart()
              }
            }
          }, 10000) // 10秒后触发
        } else if (state === 'checking') {
          console.log('🔄 ICE 连接检查中...', {
            connectionState,
            signalingState,
          })
          
          // ✅ 如果正在检查，清除断开计时器
          if (iceRestartTimeoutRef.current !== null) {
            window.clearTimeout(iceRestartTimeoutRef.current)
            iceRestartTimeoutRef.current = null
          }
          iceDisconnectedTimeRef.current = null
        }
      }

      peerConnection.onicegatheringstatechange = () => {
        const state = peerConnection.iceGatheringState
        console.log('🧊 ICE 收集状态变化:', {
          iceGatheringState: state,
          iceConnectionState: peerConnection.iceConnectionState,
        })
      }

      peerConnection.onsignalingstatechange = () => {
        const state = peerConnection.signalingState
        console.log('📡 信令状态变化:', {
          signalingState: state,
          connectionState: peerConnection.connectionState,
          iceConnectionState: peerConnection.iceConnectionState,
        })
      }

      await peerConnection.setRemoteDescription({
        type: 'offer',
        sdp: offerSdp,
      })

      const answer = await peerConnection.createAnswer({
        offerToReceiveAudio: true,
        offerToReceiveVideo: true,
      })

      if (answer.sdp) {
        try {
          const optimizedSdp = optimizeSdpForLowLatency(answer.sdp, {
            preferLanCandidates: isLikelyLan,
          })
          if (optimizedSdp && optimizedSdp.length > 10) {
            answer.sdp = optimizedSdp
          }
        } catch (sdpError) {
          console.warn('SDP 优化出错，使用原始 SDP:', sdpError)
        }
      }

      await peerConnection.setLocalDescription(answer)
      reinforceLatencyHints(peerConnection)

      await streamingService.sendAnswer({
        sessionId: webrtcSessionIdValue,
        sdp: answer.sdp || '',
        type: 'answer',
      })

      // ✅ Answer 设置后，定期获取后端的 ICE candidate（特别是 TURN relay candidate）
      // 后端的 ICE gathering 可能在 Answer 设置后才完成
      let emptyResponseCount = 0
      const MAX_EMPTY_RESPONSES = 3 // 连续 3 次空响应后停止
      const POLL_INTERVAL_MS = 1000 // 1 秒查询一次
      const MAX_POLL_DURATION_MS = 8000 // 最多查询 8 秒
      
      const checkBackendIceCandidates = async (): Promise<boolean> => {
        // 检查连接状态，如果已连接则无需继续查询
        if (peerConnection.iceConnectionState === 'connected' || peerConnection.iceConnectionState === 'completed') {
          console.log('✅ ICE 连接已建立，停止查询后端 Candidate')
          return false
        }
        
        if (peerConnection.connectionState === 'connected') {
          console.log('✅ WebRTC 连接已建立，停止查询后端 Candidate')
          return false
        }
        
        try {
          const response = await streamingService.getPendingIceCandidates(webrtcSessionIdValue)
          if (response.success && response.data) {
            const candidates = response.data.candidates || []
            if (candidates.length > 0) {
              emptyResponseCount = 0 // 重置空响应计数
              console.log('📥 收到后端 ICE Candidate:', candidates.length, '个', {
                candidates: candidates.map((c: { candidate: string; sdpMid: string | null; sdpMLineIndex: number | null }) => ({
                  candidate: c.candidate?.substring(0, 60) + '...',
                  sdpMid: c.sdpMid,
                  sdpMLineIndex: c.sdpMLineIndex,
                })),
              })
              // 使用 Set 去重，避免添加重复的 candidate
              const uniqueCandidates = new Map<string, typeof candidates[0]>()
              for (const candidate of candidates) {
                if (candidate.candidate) {
                  // 使用 candidate 字符串作为唯一键
                  const candidateKey = candidate.candidate.trim()
                  if (!uniqueCandidates.has(candidateKey)) {
                    uniqueCandidates.set(candidateKey, candidate)
                  } else {
                    console.debug('🔍 跳过重复的 candidate:', candidateKey.substring(0, 60) + '...')
                  }
                }
              }

              for (const [, candidate] of uniqueCandidates) {
                try {
                  if (candidate.candidate) {
                    // 检查 candidate 格式是否完整（应该包含 generation 和 ufrag）
                    const candidateStr = candidate.candidate.trim()
                    const hasGeneration = candidateStr.includes('generation')
                    const hasUfrag = candidateStr.includes('ufrag')
                    
                    if (!hasGeneration || !hasUfrag) {
                      console.warn('⚠️ Candidate 格式可能不完整，缺少 generation 或 ufrag:', {
                        candidate: candidateStr.substring(0, 80) + '...',
                        hasGeneration,
                        hasUfrag,
                      })
                      // 继续尝试添加，有些浏览器可能可以处理不完整的 candidate
                    }

                    const candidateObj: RTCIceCandidateInit = {
                      candidate: candidateStr,
                      sdpMid: candidate.sdpMid ?? null,
                      sdpMLineIndex: candidate.sdpMLineIndex ?? null,
                    }

                    await peerConnection.addIceCandidate(candidateObj)
                    console.log('✅ 已添加后端 ICE Candidate:', {
                      candidate: candidateStr.substring(0, 60) + '...',
                      type: candidateStr.includes('typ relay') ? 'relay' : 
                            candidateStr.includes('typ srflx') ? 'srflx' : 
                            candidateStr.includes('typ host') ? 'host' : 'unknown',
                      connectionState: peerConnection.connectionState,
                      iceConnectionState: peerConnection.iceConnectionState,
                    })
                  }
                } catch (error) {
                  // 检查错误是否是重复添加导致的（这是正常的）
                  const errorMessage = error instanceof Error ? error.message : String(error)
                  const isDuplicateError = errorMessage.includes('duplicate') || 
                                          errorMessage.includes('already been added') ||
                                          errorMessage.includes('already present')
                  
                  if (isDuplicateError) {
                    console.debug('ℹ️ Candidate 可能已存在（正常情况）:', candidate.candidate?.substring(0, 60) + '...')
                  } else {
                    console.warn('⚠️ 添加后端 ICE Candidate 失败:', {
                      candidate: candidate.candidate?.substring(0, 80) + '...',
                      error: errorMessage,
                      connectionState: peerConnection.connectionState,
                      iceConnectionState: peerConnection.iceConnectionState,
                      signalingState: peerConnection.signalingState,
                    })
                  }
                }
              }
              return true // 继续查询
            } else {
              emptyResponseCount++
              console.debug('📭 后端暂无待处理的 ICE Candidate', `(${emptyResponseCount}/${MAX_EMPTY_RESPONSES})`)
              
              // 如果连续多次空响应，停止查询
              if (emptyResponseCount >= MAX_EMPTY_RESPONSES) {
                console.log('✅ 连续空响应，停止查询后端 Candidate')
                return false
              }
              return true // 继续查询
            }
          } else {
            console.debug('⚠️ 获取后端 ICE Candidate API 调用失败:', response.errorMessage || response.message)
            return true // API 失败时继续查询
          }
        } catch (error) {
          console.warn('⚠️ 获取后端 ICE Candidate 异常:', error)
          return true // 异常时继续查询
        }
      }

      // 立即检查一次
      console.log('🔍 开始检查后端 ICE Candidate...')
      await checkBackendIceCandidates()

      // 然后每 1 秒检查一次，最多持续 8 秒（最多 8 次）
      let checkCount = 0
      const maxChecks = Math.floor(MAX_POLL_DURATION_MS / POLL_INTERVAL_MS)
      const startTime = Date.now()
      
      const backendCandidateCheckInterval = setInterval(async () => {
        // 检查是否超时
        if (Date.now() - startTime >= MAX_POLL_DURATION_MS) {
          clearInterval(backendCandidateCheckInterval)
          console.log('✅ 查询后端 ICE Candidate 超时，已检查', checkCount, '次')
          return
        }
        
        checkCount++
        console.debug(`🔍 检查后端 ICE Candidate (${checkCount}/${maxChecks})...`)
        const shouldContinue = await checkBackendIceCandidates()
        
        if (!shouldContinue) {
          clearInterval(backendCandidateCheckInterval)
          console.log('✅ 停止检查后端 ICE Candidate（已检查', checkCount, '次）')
        }
      }, POLL_INTERVAL_MS)

      setTimeout(() => {
        clearInterval(backendCandidateCheckInterval)
        console.log('✅ 查询后端 ICE Candidate 超时，已检查', checkCount, '次')
      }, MAX_POLL_DURATION_MS)

      const connectResponse = await streamingService.connectToRemotePlaySession(webrtcSessionIdValue, sessionId)
      if (!connectResponse.success) {
        throw new Error(
          connectResponse.errorMessage ||
            connectResponse.message ||
            t('streaming.connection.errors.connectRemotePlayFailed')
        )
      }

      isStreamBoundRef.current = true
      console.log('🔗 WebRTC 会话已绑定远程流')

      if (hasVideoTrackRef.current && !initialKeyframeRequestedRef.current) {
        console.log('📡 会话绑定完成，补发初始关键帧请求')
        if (requestKeyframe('post-bind-initial-video')) {
          initialKeyframeRequestedRef.current = true
        }
      }

      console.log('🎮 准备连接控制器，Session ID:', sessionId)
      await connectController(sessionId)

      gamepadEnabledRef.current = true
      console.log('✅ 手柄输入已启用')

      setIsConnected(true)
      isConnectedRef.current = true
      setIsConnecting(false)
      setConnectionState(t('streaming.connection.state.connected'))
      console.log('✅ 连接状态已设置为已连接')

      startStickProcessing()
    } catch (error) {
      console.error('连接失败:', error)
      toast({
        title: t('streaming.connection.toast.connectFailedTitle'),
        description: error instanceof Error ? error.message : t('streaming.connection.errors.unknown'),
        variant: 'destructive',
      })
      setConnectionState(t('streaming.connection.state.failed'))
      disconnect()
    } finally {
      setIsConnecting(false)
    }
  }, [
    connectController,
    deviceName,
    disconnect,
    hostId,
    isConnected,
    isConnecting,
    isLikelyLan,
    prepareDevice,
    requestKeyframe,
    reinforceLatencyHints,
    startStickProcessing,
    t,
    toast,
  ])

  useEffect(() => {
    if (!isConnected) {
      tearDownMouseRightStick()
      return
    }

    setupMouseRightStick()
    return () => {
      tearDownMouseRightStick()
    }
  }, [isConnected, setupMouseRightStick, tearDownMouseRightStick])

  useEffect(() => {
    const unsubscribe = controllerService.onRumble((event) => {
      if (!isConnectedRef.current || !gamepadEnabledRef.current || !isGamepadEnabled) {
        return
      }

      const settings = rumbleSettingsRef.current
      if (!settings.enabled || settings.strength <= 0) {
        return
      }

      applyControllerRumbleToGamepads(event, {
        settings,
      })
    })

    return () => {
      unsubscribe()
    }
  }, [isGamepadEnabled])

  useGamepadInput(handleGamepadInput, isConnected && gamepadEnabledRef.current && isGamepadEnabled)

  useEffect(() => {
    webrtcSessionIdRef.current = webrtcSessionId
  }, [webrtcSessionId])

  useEffect(() => {
    remotePlaySessionIdRef.current = remotePlaySessionId
  }, [remotePlaySessionId])

  useEffect(() => {
    if (keyframeMonitorIntervalRef.current !== null) {
      window.clearInterval(keyframeMonitorIntervalRef.current)
      keyframeMonitorIntervalRef.current = null
    }

    if (!isConnected) {
      return
    }

    if (!resolveWebrtcSessionId()) {
      return
    }

    const STALL_THRESHOLD_MS = 1500
    const POSITION_EPSILON = 0.03

    lastVideoActivityRef.current = Date.now()
    lastDecodedFrameCountRef.current = null
    lastPlaybackPositionRef.current = null

    const getDecodedFrameCount = (video: HTMLVideoElement): number | null => {
      try {
        if (typeof video.getVideoPlaybackQuality === 'function') {
          const quality = video.getVideoPlaybackQuality()
          if (quality && typeof quality.totalVideoFrames === 'number' && quality.totalVideoFrames >= 0) {
            return quality.totalVideoFrames
          }
          if (quality && typeof (quality as any).presentedFrames === 'number') {
            return (quality as any).presentedFrames
          }
        }
      } catch (error) {
        console.debug('⚠️ 读取视频播放质量失败:', error)
      }

      const videoAny = video as any
      if (typeof videoAny?.webkitDecodedFrameCount === 'number') {
        return videoAny.webkitDecodedFrameCount
      }
      if (typeof videoAny?.mozParsedFrames === 'number') {
        return videoAny.mozParsedFrames
      }

      return null
    }

    const checkStall = () => {
      const video = videoRef.current
      if (!video || !isConnectedRef.current) {
        return
      }

      const now = Date.now()
      if (video.paused || video.readyState < 2 || !video.srcObject) {
        lastVideoActivityRef.current = now
        lastDecodedFrameCountRef.current = null
        lastPlaybackPositionRef.current = null
        return
      }

      const decodedFrames = getDecodedFrameCount(video)
      if (decodedFrames !== null) {
        if (lastDecodedFrameCountRef.current === null || decodedFrames > lastDecodedFrameCountRef.current) {
          lastDecodedFrameCountRef.current = decodedFrames
          lastVideoActivityRef.current = now
          return
        }
      } else {
        const currentPosition = video.currentTime
        if (
          lastPlaybackPositionRef.current === null ||
          Math.abs(currentPosition - lastPlaybackPositionRef.current) > POSITION_EPSILON
        ) {
          lastPlaybackPositionRef.current = currentPosition
          lastVideoActivityRef.current = now
          return
        }
      }

      const inactivity = now - lastVideoActivityRef.current
      if (inactivity < STALL_THRESHOLD_MS) {
        return
      }

      void handleStreamHealthCheck('monitor-stall', { forceNeutral: true })
    }

    keyframeMonitorIntervalRef.current = window.setInterval(checkStall, 1000)

    return () => {
      if (keyframeMonitorIntervalRef.current !== null) {
        window.clearInterval(keyframeMonitorIntervalRef.current)
        keyframeMonitorIntervalRef.current = null
      }
    }
  }, [handleStreamHealthCheck, isConnected, resolveWebrtcSessionId, videoRef])

  useEffect(() => {
    isStatsEnabledRef.current = isStatsEnabled

    if (!isStatsEnabled) {
      if (statsIntervalRef.current !== null) {
        window.clearInterval(statsIntervalRef.current)
        statsIntervalRef.current = null
      }
      previousStatsRef.current = null
      return
    }

    const tick = () => {
      collectConnectionStats().catch((error) => {
        console.warn('更新 WebRTC 统计信息失败:', error)
      })
    }

    tick()
    statsIntervalRef.current = window.setInterval(tick, 1000)

    return () => {
      if (statsIntervalRef.current !== null) {
        window.clearInterval(statsIntervalRef.current)
        statsIntervalRef.current = null
      }
    }
  }, [collectConnectionStats, isStatsEnabled])

  const disconnectRef = useRef(disconnect)
  useEffect(() => {
    disconnectRef.current = disconnect
  }, [disconnect])

  useEffect(() => {
    hasAttemptedInitialConnectRef.current = false
  }, [hostId])

  useEffect(() => {
    if (hostId && !isConnected && !isConnecting && !hasAttemptedInitialConnectRef.current) {
      hasAttemptedInitialConnectRef.current = true
      const timer = setTimeout(() => {
        connect()
      }, 500)
      return () => clearTimeout(timer)
    }
    return undefined
  }, [connect, hostId, isConnected, isConnecting])

  useEffect(() => {
    return () => {
      disconnectRef.current()
    }
  }, [])

  const setStatsMonitoring = useCallback((enabled: boolean) => {
    setIsStatsEnabled(enabled)
  }, [])

  return {
    isConnected,
    isConnecting,
    connectionState,
    connect,
    disconnect,
    connectionStats,
    isStatsMonitoringEnabled: isStatsEnabled,
    setStatsMonitoring,
    refreshStream,
    webrtcSessionId,
  }
}

