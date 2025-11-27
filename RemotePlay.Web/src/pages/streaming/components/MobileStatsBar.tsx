import type { StreamingMonitorStats } from '@/hooks/use-streaming-connection'
import type { TFunction } from 'i18next'
import { useTranslation } from 'react-i18next'

interface MobileStatsBarProps {
  stats: StreamingMonitorStats | null
}

const formatRate = (t: TFunction, value: number | null) => {
  if (value === null || Number.isNaN(value)) {
    return '--'
  }
  if (value >= 1000) {
    return t('streaming.monitor.units.mbps', {
      value: (value / 1000).toFixed(2),
    })
  }
  return t('streaming.monitor.units.kbps', {
    value: value.toFixed(0),
  })
}

const formatResolution = (_t: TFunction, stats: StreamingMonitorStats | null) => {
  if (!stats?.resolution) {
    return '--'
  }
  const { width, height } = stats.resolution
  if (!width || !height) {
    return '--'
  }
  return `${width}×${height}`
}

const formatLatency = (t: TFunction, value: number | null) => {
  if (value === null || Number.isNaN(value)) {
    return '--'
  }
  return t('streaming.monitor.units.ms', {
    value: value.toFixed(0),
  })
}

const formatFps = (t: TFunction, value: number | null) => {
  if (value === null || Number.isNaN(value)) {
    return '--'
  }
  return t('streaming.monitor.units.fps', {
    value: value.toFixed(0),
  })
}

export function MobileStatsBar({ stats }: MobileStatsBarProps) {
  const { t } = useTranslation()

  return (
    <div
      className="fixed top-0 left-0 right-0 z-[100] pointer-events-none"
      style={{
        paddingTop: 'env(safe-area-inset-top, 0px)',
      }}
    >
      <div
        className="px-2 py-1"
        style={{
          backgroundColor: 'rgba(0, 0, 0, 0.05)',
          backdropFilter: 'blur(4px)',
          WebkitBackdropFilter: 'blur(4px)',
        }}
      >
        <div 
          className="flex items-center gap-1.5 text-[9px] text-white mobile-stats-scroll"
          style={{
            overflowX: 'auto',
            scrollbarWidth: 'none', // Firefox
            msOverflowStyle: 'none', // IE/Edge
            WebkitOverflowScrolling: 'touch',
          }}
        >
          {/* 下行速率 */}
          <div className="flex items-center gap-0.5 flex-shrink-0" style={{ width: '75px' }}>
            <span className="text-white/60 whitespace-nowrap text-[8px]" style={{ textShadow: '0 1px 3px rgba(0, 0, 0, 0.8)' }}>{t('streaming.monitor.labels.download')}:</span>
            <span 
              className="font-medium whitespace-nowrap text-right flex-1" 
              style={{ 
                textShadow: '0 1px 3px rgba(0, 0, 0, 0.9), 0 0 4px rgba(0, 0, 0, 0.6)',
                minWidth: '50px'
              }}
            >
              {formatRate(t, stats?.downloadKbps ?? null)}
            </span>
          </div>

          {/* 分隔符 */}
          <span className="text-white/30 flex-shrink-0" style={{ textShadow: '0 1px 2px rgba(0, 0, 0, 0.8)' }}>|</span>

          {/* 上行速率 */}
          <div className="flex items-center gap-0.5 flex-shrink-0" style={{ width: '75px' }}>
            <span className="text-white/60 whitespace-nowrap text-[8px]" style={{ textShadow: '0 1px 3px rgba(0, 0, 0, 0.8)' }}>{t('streaming.monitor.labels.upload')}:</span>
            <span 
              className="font-medium whitespace-nowrap text-right flex-1" 
              style={{ 
                textShadow: '0 1px 3px rgba(0, 0, 0, 0.9), 0 0 4px rgba(0, 0, 0, 0.6)',
                minWidth: '50px'
              }}
            >
              {formatRate(t, stats?.uploadKbps ?? null)}
            </span>
          </div>

          {/* 分隔符 */}
          <span className="text-white/30 flex-shrink-0" style={{ textShadow: '0 1px 2px rgba(0, 0, 0, 0.8)' }}>|</span>

          {/* 视频码率 */}
          <div className="flex items-center gap-0.5 flex-shrink-0" style={{ width: '75px' }}>
            <span className="text-white/60 whitespace-nowrap text-[8px]" style={{ textShadow: '0 1px 3px rgba(0, 0, 0, 0.8)' }}>{t('streaming.monitor.labels.videoBitrate')}:</span>
            <span 
              className="font-medium whitespace-nowrap text-right flex-1" 
              style={{ 
                textShadow: '0 1px 3px rgba(0, 0, 0, 0.9), 0 0 4px rgba(0, 0, 0, 0.6)',
                minWidth: '50px'
              }}
            >
              {formatRate(t, stats?.videoBitrateKbps ?? null)}
            </span>
          </div>

          {/* 分隔符 */}
          <span className="text-white/30 flex-shrink-0" style={{ textShadow: '0 1px 2px rgba(0, 0, 0, 0.8)' }}>|</span>

          {/* 分辨率 */}
          <div className="flex items-center gap-0.5 flex-shrink-0" style={{ width: '85px' }}>
            <span className="text-white/60 whitespace-nowrap text-[8px]" style={{ textShadow: '0 1px 3px rgba(0, 0, 0, 0.8)' }}>{t('streaming.monitor.labels.resolution')}:</span>
            <span 
              className="font-medium whitespace-nowrap text-right flex-1" 
              style={{ 
                textShadow: '0 1px 3px rgba(0, 0, 0, 0.9), 0 0 4px rgba(0, 0, 0, 0.6)',
                minWidth: '58px'
              }}
            >
              {formatResolution(t, stats)}
            </span>
          </div>

          {/* 分隔符 */}
          <span className="text-white/30 flex-shrink-0" style={{ textShadow: '0 1px 2px rgba(0, 0, 0, 0.8)' }}>|</span>

          {/* 延迟 */}
          <div className="flex items-center gap-0.5 flex-shrink-0" style={{ width: '65px' }}>
            <span className="text-white/60 whitespace-nowrap text-[8px]" style={{ textShadow: '0 1px 3px rgba(0, 0, 0, 0.8)' }}>{t('streaming.monitor.labels.latency')}:</span>
            <span 
              className="font-medium whitespace-nowrap text-right flex-1" 
              style={{ 
                textShadow: '0 1px 3px rgba(0, 0, 0, 0.9), 0 0 4px rgba(0, 0, 0, 0.6)',
                minWidth: '40px'
              }}
            >
              {formatLatency(t, stats?.latencyMs ?? null)}
            </span>
          </div>

          {/* 分隔符 */}
          <span className="text-white/30 flex-shrink-0" style={{ textShadow: '0 1px 2px rgba(0, 0, 0, 0.8)' }}>|</span>

          {/* 帧率 */}
          <div className="flex items-center gap-0.5 flex-shrink-0" style={{ width: '60px' }}>
            <span className="text-white/60 whitespace-nowrap text-[8px]" style={{ textShadow: '0 1px 3px rgba(0, 0, 0, 0.8)' }}>{t('streaming.monitor.labels.fps')}:</span>
            <span 
              className="font-medium whitespace-nowrap text-right flex-1" 
              style={{ 
                textShadow: '0 1px 3px rgba(0, 0, 0, 0.9), 0 0 4px rgba(0, 0, 0, 0.6)',
                minWidth: '35px'
              }}
            >
              {formatFps(t, stats?.fps ?? null)}
            </span>
          </div>
        </div>
      </div>
      {/* 隐藏滚动条样式 */}
      <style>{`
        .mobile-stats-scroll::-webkit-scrollbar {
          display: none;
        }
      `}</style>
    </div>
  )
}

