import { useEffect, useMemo, useRef, useCallback } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { useToast } from '@/hooks/use-toast'
import { Wifi, AlertTriangle } from 'lucide-react'
import { StreamingHeader } from './components/StreamingHeader'
import { StreamingVideoPlayer } from './components/StreamingVideoPlayer'
import { StreamingLoading } from './components/StreamingLoading'
import { StreamingTopBar } from './components/StreamingTopBar'
import { StreamingStatsOverlay } from './components/StreamingStatsOverlay'
import { MobileVirtualController } from './components/MobileVirtualController'
import { MobileVirtualControllerPortrait } from './components/MobileVirtualControllerPortrait'
import { MobileStatsBar } from './components/MobileStatsBar'
import { useStreamingConnection } from '../../hooks/use-streaming-connection'
import { useDevice } from '@/hooks/use-device'

export default function Streaming() {
  const navigate = useNavigate()
  const [searchParams] = useSearchParams()
  const { toast } = useToast()
  const { isMobile, isLandscape, isPortrait } = useDevice()

  const hostId = searchParams.get('hostId')
  const deviceName = searchParams.get('deviceName') || 'PlayStation'

  const videoRef = useRef<HTMLVideoElement>(null)
  const containerRef = useRef<HTMLDivElement>(null)
  
  const isLikelyLan = useMemo(() => {
    if (typeof window === 'undefined') {
      return false
    }

    const hostname = window.location.hostname || ''
    if (!hostname) {
      return false
    }

    const lowerHost = hostname.toLowerCase()
    if (hostname === 'localhost' || hostname === '127.0.0.1') {
      return true
    }

    if (hostname.includes(':')) {
      if (lowerHost.startsWith('fe80') || lowerHost.startsWith('fd') || lowerHost.startsWith('fc')) {
        return true
      }
      return false
    }

    if (hostname.startsWith('10.')) return true
    if (hostname.startsWith('192.168.')) return true
    if (hostname.startsWith('169.254.')) return true

    if (hostname.startsWith('172.')) {
      const parts = hostname.split('.')
      if (parts.length >= 2) {
        const second = Number(parts[1])
        if (!Number.isNaN(second) && second >= 16 && second <= 31) {
          return true
        }
      }
    }

    if (lowerHost.endsWith('.local')) {
      return true
    }

    return false
  }, [])

  const {
    isConnected,
    isConnecting,
    connectionState,
    connect,
    disconnect,
    connectionStats,
    isStatsMonitoringEnabled,
    setStatsMonitoring,
    refreshStream,
    forceResetReorderQueue,
    webrtcSessionId,
    isStalling,
  } = useStreamingConnection({
    hostId,
    deviceName,
    isLikelyLan,
    videoRef,
    toast,
    onConnectionError: (reason: string) => {
      // 显示断开原因，使用较短的显示时间，避免长时间遮挡
      toast({
        title: '连接已断开',
        description: reason,
        variant: 'destructive',
        duration: 3000, // 3秒后自动消失
      })
      // 延迟导航，让用户看到提示但不会太久
      setTimeout(() => {
        disconnect()
        navigate('/devices')
      }, 3000)
    },
  })

  // 在移动端开始连接时自动进入页面全屏并锁定横屏
  useEffect(() => {
    if (!isMobile) return

    const requestPageFullscreen = async () => {
      try {
        // 检查是否已经在全屏状态
        const isFullscreen = !!(
          document.fullscreenElement ||
          (document as any).webkitFullscreenElement ||
          (document as any).mozFullScreenElement ||
          (document as any).msFullscreenElement
        )

        if (isFullscreen) {
          return // 已经在全屏状态，不需要再次请求
        }

        // 尝试请求页面全屏
        const doc = document.documentElement
        if (doc.requestFullscreen) {
          await doc.requestFullscreen()
        } else if ((doc as any).webkitRequestFullscreen) {
          await (doc as any).webkitRequestFullscreen()
        } else if ((doc as any).mozRequestFullScreen) {
          await (doc as any).mozRequestFullScreen()
        } else if ((doc as any).msRequestFullscreen) {
          await (doc as any).msRequestFullscreen()
        }
      } catch (error) {
        // 全屏请求可能失败（例如需要用户手势），静默处理
        console.debug('Failed to request fullscreen:', error)
      }
    }

    const exitPageFullscreen = async () => {
      try {
        if (document.fullscreenElement) {
          await document.exitFullscreen()
        } else if ((document as any).webkitFullscreenElement) {
          ;(document as any).webkitExitFullscreen?.()
        } else if ((document as any).mozFullScreenElement) {
          ;(document as any).mozCancelFullScreen?.()
        } else if ((document as any).msFullscreenElement) {
          ;(document as any).msExitFullscreen?.()
        }
      } catch (error) {
        console.debug('Failed to exit fullscreen:', error)
      }
    }

    // 锁定屏幕方向为横屏
    const lockOrientation = async () => {
      try {
        // 使用标准的 Screen Orientation API
        if ('orientation' in screen && 'lock' in (screen as any).orientation) {
          await (screen.orientation as any).lock('landscape')
        }
        // iOS Safari 使用 webkit 前缀
        else if ((screen as any).orientation && (screen as any).orientation.lock) {
          await (screen as any).orientation.lock('landscape')
        }
        // 旧版 API（已废弃但部分浏览器仍支持）
        else if ((screen as any).lockOrientation) {
          ;(screen as any).lockOrientation('landscape')
        }
        // webkit 前缀版本
        else if ((screen as any).webkitLockOrientation) {
          ;(screen as any).webkitLockOrientation('landscape')
        }
        // moz 前缀版本
        else if ((screen as any).mozLockOrientation) {
          ;(screen as any).mozLockOrientation('landscape')
        }
        // ms 前缀版本
        else if ((screen as any).msLockOrientation) {
          ;(screen as any).msLockOrientation('landscape')
        }
      } catch (error) {
        // 屏幕方向锁定可能失败（例如需要用户手势或浏览器不支持），静默处理
        console.debug('Failed to lock orientation:', error)
      }
    }

    // 解锁屏幕方向
    const unlockOrientation = async () => {
      try {
        // 使用标准的 Screen Orientation API
        if ('orientation' in screen && 'unlock' in (screen as any).orientation) {
          ;(screen.orientation as any).unlock()
        }
        // iOS Safari 使用 webkit 前缀
        else if ((screen as any).orientation && (screen as any).orientation.unlock) {
          ;(screen as any).orientation.unlock()
        }
        // 旧版 API（已废弃但部分浏览器仍支持）
        else if ((screen as any).unlockOrientation) {
          ;(screen as any).unlockOrientation()
        }
        // webkit 前缀版本
        else if ((screen as any).webkitUnlockOrientation) {
          ;(screen as any).webkitUnlockOrientation()
        }
        // moz 前缀版本
        else if ((screen as any).mozUnlockOrientation) {
          ;(screen as any).mozUnlockOrientation()
        }
        // ms 前缀版本
        else if ((screen as any).msUnlockOrientation) {
          ;(screen as any).msUnlockOrientation()
        }
      } catch (error) {
        console.debug('Failed to unlock orientation:', error)
      }
    }

    // 锁定屏幕方向为竖屏
    const lockPortraitOrientation = async (retry = false) => {
      try {
        // 使用标准的 Screen Orientation API
        if ('orientation' in screen && 'lock' in (screen as any).orientation) {
          await (screen.orientation as any).lock('portrait')
        }
        // iOS Safari 使用 webkit 前缀
        else if ((screen as any).orientation && (screen as any).orientation.lock) {
          await (screen as any).orientation.lock('portrait')
        }
        // 旧版 API（已废弃但部分浏览器仍支持）
        else if ((screen as any).lockOrientation) {
          ;(screen as any).lockOrientation('portrait')
        }
        // webkit 前缀版本
        else if ((screen as any).webkitLockOrientation) {
          ;(screen as any).webkitLockOrientation('portrait')
        }
        // moz 前缀版本
        else if ((screen as any).mozLockOrientation) {
          ;(screen as any).mozLockOrientation('portrait')
        }
        // ms 前缀版本
        else if ((screen as any).msLockOrientation) {
          ;(screen as any).msLockOrientation('portrait')
        }
        // 如果所有 API 都不支持，先解锁
        else {
          await unlockOrientation()
        }
      } catch (error) {
        // 如果锁定竖屏失败，尝试先解锁再重试一次
        if (!retry) {
          console.debug('Failed to lock portrait orientation, retrying after unlock:', error)
          await unlockOrientation()
          setTimeout(async () => {
            await lockPortraitOrientation(true)
          }, 200)
        } else {
          // 重试后仍然失败，只解锁让用户手动旋转
          console.debug('Failed to lock portrait orientation after retry:', error)
          await unlockOrientation()
        }
      }
    }

    // 在开始连接或已连接时进入全屏，默认尝试锁定横屏（但不强制）
    if (isConnecting || isConnected) {
      // 开始连接时立即请求全屏，延迟一点时间再尝试锁定横屏，确保页面已经渲染完成
      const timer = setTimeout(async () => {
        // 先请求全屏
        await requestPageFullscreen()
        // 在全屏请求后稍等片刻再尝试锁定横屏（某些浏览器需要全屏后才能锁定）
        // 默认尝试锁定横屏，但如果用户旋转到竖屏，允许保持竖屏
        setTimeout(async () => {
          // 检查当前方向，如果是横屏则锁定横屏，如果是竖屏则锁定竖屏
          const currentIsLandscape = window.innerWidth > window.innerHeight
          if (currentIsLandscape) {
          await lockOrientation()
          } else {
            await lockPortraitOrientation()
          }
        }, 300)
      }, 300)

      // 监听屏幕方向变化，根据当前方向锁定
      const orientationChangeHandler = async () => {
        // 延迟一点时间再锁定，避免与系统旋转冲突
        setTimeout(async () => {
          const currentIsLandscape = window.innerWidth > window.innerHeight
          if (currentIsLandscape) {
          await lockOrientation()
          } else {
            await lockPortraitOrientation()
          }
        }, 100)
      }

      window.addEventListener('orientationchange', orientationChangeHandler)
      // 某些浏览器使用 resize 事件来检测方向变化
      window.addEventListener('resize', orientationChangeHandler)

      return () => {
        clearTimeout(timer)
        window.removeEventListener('orientationchange', orientationChangeHandler)
        window.removeEventListener('resize', orientationChangeHandler)
      }
    } else {
      // 断开连接时先退出全屏，然后解锁方向
      exitPageFullscreen()
      // 退出全屏后稍等片刻再解锁方向（某些浏览器需要退出全屏后才能改变方向）
      setTimeout(async () => {
        await unlockOrientation()
      }, 300)
    }
  }, [isMobile, isConnecting, isConnected])

  // 切换横竖屏
  const toggleOrientation = useCallback(async () => {
    try {
      const currentIsLandscape = window.innerWidth > window.innerHeight
      if ('orientation' in screen && 'lock' in (screen as any).orientation) {
        if (currentIsLandscape) {
          // 当前是横屏，切换到竖屏
          await (screen.orientation as any).lock('portrait')
        } else {
          // 当前是竖屏，切换到横屏
          await (screen.orientation as any).lock('landscape')
        }
      } else if ((screen as any).orientation && (screen as any).orientation.lock) {
        if (currentIsLandscape) {
          await (screen as any).orientation.lock('portrait')
        } else {
          await (screen as any).orientation.lock('landscape')
        }
      } else if ((screen as any).lockOrientation) {
        if (currentIsLandscape) {
          ;(screen as any).lockOrientation('portrait')
        } else {
          ;(screen as any).lockOrientation('landscape')
        }
      }
    } catch (error) {
      console.debug('Failed to toggle orientation:', error)
    }
  }, [])

  // 处理返回：移动端时先退出全屏和解锁方向
  const handleBack = useCallback(async () => {
    if (isMobile) {
      // 退出全屏
      try {
        if (document.fullscreenElement) {
          await document.exitFullscreen()
        } else if ((document as any).webkitFullscreenElement) {
          ;(document as any).webkitExitFullscreen?.()
        } else if ((document as any).mozFullScreenElement) {
          ;(document as any).mozCancelFullScreen?.()
        } else if ((document as any).msFullscreenElement) {
          ;(document as any).msExitFullscreen?.()
        }
      } catch (error) {
        console.debug('Failed to exit fullscreen:', error)
      }

      // 解锁屏幕方向
      try {
        if ('orientation' in screen && 'unlock' in (screen as any).orientation) {
          ;(screen.orientation as any).unlock()
        } else if ((screen as any).orientation && (screen as any).orientation.unlock) {
          ;(screen as any).orientation.unlock()
        } else if ((screen as any).unlockOrientation) {
          ;(screen as any).unlockOrientation()
        } else if ((screen as any).webkitUnlockOrientation) {
          ;(screen as any).webkitUnlockOrientation()
        } else if ((screen as any).mozUnlockOrientation) {
          ;(screen as any).mozUnlockOrientation()
        } else if ((screen as any).msUnlockOrientation) {
          ;(screen as any).msUnlockOrientation()
        }
      } catch (error) {
        console.debug('Failed to unlock orientation:', error)
      }

      // 等待一小段时间确保全屏和方向解锁完成
      await new Promise(resolve => setTimeout(resolve, 100))
    }

    // 断开连接并导航
    disconnect()
    navigate('/devices')
  }, [isMobile, disconnect, navigate])

  useEffect(() => {
    if (!hostId) {
      toast({
        title: '参数错误',
        description: '缺少设备信息，正在返回设备列表...',
        variant: 'destructive',
        duration: 2000, // 2秒后自动消失
      })
      const timer = setTimeout(() => {
        disconnect()
        navigate('/devices')
      }, 2000)
      return () => clearTimeout(timer)
    }
    return undefined
  }, [disconnect, hostId, navigate, toast])

  if (!isConnected && !isConnecting) {
    return (
      <div className="fixed inset-0 bg-black">
        <StreamingTopBar
          onBack={handleBack}
          isStatsEnabled={isStatsMonitoringEnabled}
          onRefresh={isConnected ? () => { refreshStream() } : undefined}
          onForceResetQueue={isConnected ? () => { forceResetReorderQueue() } : undefined}
        />
        <StreamingLoading />
      </div>
    )
  }

  return (
    <div 
      ref={containerRef}
      className="min-h-screen bg-black text-white fullscreen-container"
      style={{
        position: 'fixed',
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        width: '100%',
        height: '100%',
        overflow: 'hidden',
        // 在移动端禁用全屏相关的样式
        ...(isMobile && {
          touchAction: 'manipulation',
        }),
      }}
    >
      {(isConnecting || isConnected) && (
        <StreamingTopBar
          onBack={handleBack}
          isStatsEnabled={isStatsMonitoringEnabled}
          onStatsToggle={isConnected ? setStatsMonitoring : undefined}
          onRefresh={isConnected ? () => { refreshStream() } : undefined}
          onForceResetQueue={isConnected ? () => { forceResetReorderQueue() } : undefined}
        />
      )}

      {/* 移动端顶部统计栏 - 只在开启统计时显示 */}
      {isMobile && isStatsMonitoringEnabled && isConnected && (
        <MobileStatsBar stats={connectionStats} />
      )}

      {(isConnecting || isConnected) && (
        <div 
          className="fixed bg-black" 
          style={{ 
            zIndex: isConnected ? 10 : 0,
            // 竖屏模式下，视频宽度100%，高度按16:9比例；横屏模式下，占据全屏
            ...(isMobile && isPortrait ? {
              top: 0,
              left: 0,
              right: 0,
              width: '100%',
              aspectRatio: '16/9',
            } : {
              inset: 0,
            }),
          }}
        >
          <StreamingVideoPlayer ref={videoRef} isConnected={isConnected} isConnecting={isConnecting} onConnect={connect} />
        </div>
      )}

      {/* 桌面端显示统计覆盖层，移动端使用顶部统计栏 */}
      {!isMobile && isStatsMonitoringEnabled && isConnected && (
        <StreamingStatsOverlay stats={connectionStats} />
      )}

      {/* 卡顿提示图标 - 右上角闪烁 */}
      {isConnected && isStalling && (
        <div 
          className="fixed z-[100] pointer-events-none"
          style={{
            // 移动端：如果有统计栏显示，则在其下方，否则在顶部
            top: isMobile 
              ? (isStatsMonitoringEnabled ? '36px' : '12px')
              : '16px',
            right: isMobile ? '12px' : '16px',
          }}
        >
          <div className="relative w-6 h-6 animate-pulse">
            {/* 闪烁背景 */}
            <div className="absolute inset-0 bg-yellow-500/30 rounded-full animate-ping" />
            {/* 网络图标 */}
            <div className="relative bg-black/60 backdrop-blur-sm rounded-full p-1 shadow-lg border border-yellow-500/50">
              <Wifi className="w-4 h-4 text-white" strokeWidth={2} />
              {/* 黄色三角叹号叠加 */}
              <div className="absolute -top-0.5 -right-0.5">
                <AlertTriangle className="w-3 h-3 text-yellow-500 fill-yellow-500" strokeWidth={2} />
              </div>
            </div>
          </div>
        </div>
      )}

      {isConnecting && !isConnected && (
        <div className="fixed inset-0 bg-black z-20">
          <StreamingLoading />
        </div>
      )}

      {/* 移动端连接时隐藏 StreamingHeader，避免与 StreamingTopBar 重复 */}
      {/* 桌面端或未连接时显示 StreamingHeader */}
      {(!isConnected || isConnecting) && (!isMobile || !isConnecting) && (
        <div className="relative z-30">
          <StreamingHeader
            deviceName={deviceName}
            connectionState={connectionState}
            isConnected={isConnected}
            isConnecting={isConnecting}
            onBack={() => {
              disconnect()
              navigate('/devices')
            }}
            onConnect={connect}
            onDisconnect={disconnect}
          />
        </div>
      )}

      {/* 移动端虚拟控制器 - 根据屏幕方向显示不同的控制器 */}
      {isMobile && isConnected && (
        <>
          {isLandscape ? (
        <MobileVirtualController
          sessionId={webrtcSessionId}
          isVisible={isConnected}
          onBack={handleBack}
          onRefresh={isConnected ? () => { refreshStream() } : undefined}
          isStatsEnabled={isStatsMonitoringEnabled}
          onStatsToggle={isConnected ? setStatsMonitoring : undefined}
          onToggleOrientation={toggleOrientation}
        />
          ) : isPortrait ? (
            <MobileVirtualControllerPortrait
              sessionId={webrtcSessionId}
              isVisible={isConnected}
              onBack={() => {
                disconnect()
                navigate('/devices')
              }}
              onRefresh={isConnected ? () => { refreshStream() } : undefined}
              isStatsEnabled={isStatsMonitoringEnabled}
              onStatsToggle={isConnected ? setStatsMonitoring : undefined}
              onToggleOrientation={toggleOrientation}
            />
          ) : null}
        </>
      )}
    </div>
  )
}

