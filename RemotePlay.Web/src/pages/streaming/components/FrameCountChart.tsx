import type { FrameCountDataPoint } from '@/hooks/use-streaming-connection'

interface FrameCountChartProps {
  data: FrameCountDataPoint[]
  width?: number
  height?: number
}

export function FrameCountChart({ data, width = 200, height = 40 }: FrameCountChartProps) {
  if (!data || data.length === 0) {
    return (
      <div
        className="flex items-center justify-center text-white/40 text-xs"
        style={{ width, height }}
      >
        --
      </div>
    )
  }

  const now = Date.now()
  const tenMinutesAgo = now - 10 * 60 * 1000
  const timeRange = 10 * 60 * 1000

  const filteredData = data.filter((d) => d.timestamp >= tenMinutesAgo)

  if (filteredData.length === 0) {
    return (
      <div
        className="flex items-center justify-center text-white/40 text-xs"
        style={{ width, height }}
      >
        --
      </div>
    )
  }

  const frameCounts = filteredData.map((d) => d.frameCount)
  const minFrameCount = Math.min(...frameCounts)
  const maxFrameCount = Math.max(...frameCounts)
  const frameRange = maxFrameCount - minFrameCount || 1

  const padding = { top: 4, right: 4, bottom: 4, left: 4 }
  const chartWidth = width - padding.left - padding.right
  const chartHeight = height - padding.top - padding.bottom

  const points = filteredData.map((point) => {
    const x = padding.left + ((point.timestamp - tenMinutesAgo) / timeRange) * chartWidth
    const y =
      padding.top +
      chartHeight -
      ((point.frameCount - minFrameCount) / frameRange) * chartHeight
    return { x, y, frameCount: point.frameCount }
  })

  const pathData = points
    .map((point, index) => {
      if (index === 0) {
        return `M ${point.x} ${point.y}`
      }
      return `L ${point.x} ${point.y}`
    })
    .join(' ')

  return (
    <div className="relative w-full" style={{ height }}>
      <svg width="100%" height={height} className="overflow-hidden" style={{ display: 'block' }} viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none">
        <defs>
          <linearGradient id="frameCountGradient" x1="0%" y1="0%" x2="0%" y2="100%">
            <stop offset="0%" stopColor="rgba(255, 255, 255, 0.08)" />
            <stop offset="50%" stopColor="rgba(255, 255, 255, 0.03)" />
            <stop offset="100%" stopColor="rgba(255, 255, 255, 0)" />
          </linearGradient>
        </defs>

        {points.length > 1 && (
          <path
            d={`${pathData} L ${points[points.length - 1].x} ${height - padding.bottom} L ${points[0].x} ${height - padding.bottom} Z`}
            fill="url(#frameCountGradient)"
          />
        )}

        {points.length > 1 && (
          <path
            d={pathData}
            fill="none"
            stroke="rgba(255, 255, 255, 0.6)"
            strokeWidth="1.5"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        )}
      </svg>
    </div>
  )
}
