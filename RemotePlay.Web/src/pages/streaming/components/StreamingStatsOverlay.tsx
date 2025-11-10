import type { StreamingMonitorStats } from '@/hooks/use-streaming-connection'
import type { TFunction } from 'i18next'
import { useTranslation } from 'react-i18next'

interface StreamingStatsOverlayProps {
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

const formatResolution = (t: TFunction, stats: StreamingMonitorStats | null) => {
  if (!stats?.resolution) {
    return '--'
  }
  const { width, height } = stats.resolution
  if (!width || !height) {
    return '--'
  }
  return t('streaming.monitor.units.resolution', { width, height })
}

const formatLatency = (t: TFunction, value: number | null) => {
  if (value === null || Number.isNaN(value)) {
    return '--'
  }
  return t('streaming.monitor.units.ms', {
    value: value.toFixed(0),
  })
}

export function StreamingStatsOverlay({ stats }: StreamingStatsOverlayProps) {
  const { t } = useTranslation()

  return (
    <div className="fixed top-20 right-6 z-[9998] w-[240px] rounded-xl border border-white/20 bg-black/60 p-4 text-xs text-white shadow-lg backdrop-blur-md">
      <div className="mb-3 flex items-center justify-between">
        <span className="text-sm font-semibold text-white/90">{t('streaming.monitor.title')}</span>
        <span className="rounded-full border border-white/20 px-2 py-0.5 text-[10px] uppercase tracking-wide text-white/70">
          {t('streaming.monitor.webrtc')}
        </span>
      </div>
      <div className="space-y-2 text-white/80">
        <div className="flex items-center justify-between">
          <span className="text-white/60">{t('streaming.monitor.labels.download')}</span>
          <span>{formatRate(t, stats?.downloadKbps ?? null)}</span>
        </div>
        <div className="flex items-center justify-between">
          <span className="text-white/60">{t('streaming.monitor.labels.upload')}</span>
          <span>{formatRate(t, stats?.uploadKbps ?? null)}</span>
        </div>
        <div className="flex items-center justify-between">
          <span className="text-white/60">{t('streaming.monitor.labels.videoBitrate')}</span>
          <span>{formatRate(t, stats?.videoBitrateKbps ?? null)}</span>
        </div>
        <div className="flex items-center justify-between">
          <span className="text-white/60">{t('streaming.monitor.labels.resolution')}</span>
          <span>{formatResolution(t, stats)}</span>
        </div>
        <div className="flex items-center justify-between">
          <span className="text-white/60">{t('streaming.monitor.labels.latency')}</span>
          <span>{formatLatency(t, stats?.latencyMs ?? null)}</span>
        </div>
      </div>
    </div>
  )
}

