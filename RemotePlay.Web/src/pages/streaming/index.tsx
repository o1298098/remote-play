import { useEffect, useMemo, useRef } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { useToast } from '@/hooks/use-toast'
import { StreamingHeader } from './components/StreamingHeader'
import { StreamingVideoPlayer } from './components/StreamingVideoPlayer'
import { StreamingLoading } from './components/StreamingLoading'
import { StreamingTopBar } from './components/StreamingTopBar'
import { StreamingStatsOverlay } from './components/StreamingStatsOverlay'
import { useStreamingConnection } from '../../hooks/use-streaming-connection'

export default function Streaming() {
  const navigate = useNavigate()
  const [searchParams] = useSearchParams()
  const { toast } = useToast()

  const hostId = searchParams.get('hostId')
  const deviceName = searchParams.get('deviceName') || 'PlayStation'

  const videoRef = useRef<HTMLVideoElement>(null)
  
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
  } = useStreamingConnection({
    hostId,
    deviceName,
    isLikelyLan,
    videoRef,
    toast,
  })

  useEffect(() => {
    if (!hostId) {
      toast({
        title: '参数错误',
        description: '缺少设备信息，正在返回设备列表...',
        variant: 'destructive',
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
          onBack={() => {
            disconnect()
            navigate('/devices')
          }}
          isStatsEnabled={isStatsMonitoringEnabled}
        />
        <StreamingLoading />
      </div>
    )
  }

  return (
    <div className="min-h-screen bg-black text-white">
      {(isConnecting || isConnected) && (
        <StreamingTopBar
          onBack={() => {
            disconnect()
            navigate('/devices')
          }}
          isStatsEnabled={isStatsMonitoringEnabled}
          onStatsToggle={isConnected ? setStatsMonitoring : undefined}
        />
      )}

      {(isConnecting || isConnected) && (
        <div className="fixed inset-0 bg-black" style={{ zIndex: isConnected ? 10 : 0 }}>
          <StreamingVideoPlayer ref={videoRef} isConnected={isConnected} isConnecting={isConnecting} onConnect={connect} />
        </div>
      )}

      {isStatsMonitoringEnabled && isConnected && (
        <StreamingStatsOverlay stats={connectionStats} />
      )}

      {isConnecting && !isConnected && (
        <div className="fixed inset-0 bg-black z-20">
          <StreamingLoading />
        </div>
      )}

      {(!isConnected || isConnecting) && (
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
    </div>
  )
}

