/**
 * 将毫秒格式化为 mm:ss，超过 1 小时为 hh:mm:ss，各段补零（例如 01:02、01:05:09）
 */
export function formatStreamingDuration(value: number | null): string {
  if (value === null || Number.isNaN(value) || value < 0) {
    return '--'
  }

  const totalSeconds = Math.floor(value / 1000)
  const hours = Math.floor(totalSeconds / 3600)
  const minutes = Math.floor((totalSeconds % 3600) / 60)
  const seconds = totalSeconds % 60
  const pad = (n: number) => n.toString().padStart(2, '0')

  return hours > 0 ? `${pad(hours)}:${pad(minutes)}:${pad(seconds)}` : `${pad(minutes)}:${pad(seconds)}`
}
