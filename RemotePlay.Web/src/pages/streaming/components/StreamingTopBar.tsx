import { useState, useRef, useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { ArrowLeft, Activity } from 'lucide-react'

interface StreamingTopBarProps {
  onBack: () => void
  isStatsEnabled?: boolean
  onStatsToggle?: (enabled: boolean) => void
}

export function StreamingTopBar({
  onBack,
  isStatsEnabled = false,
  onStatsToggle,
}: StreamingTopBarProps) {
  const { t } = useTranslation()
  const [isVisible, setIsVisible] = useState(false)
  const timeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const mouseMoveTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const showBar = () => {
    setIsVisible(true)
    // 清除所有隐藏定时器
    if (timeoutRef.current) {
      clearTimeout(timeoutRef.current)
      timeoutRef.current = null
    }
    if (mouseMoveTimeoutRef.current) {
      clearTimeout(mouseMoveTimeoutRef.current)
      mouseMoveTimeoutRef.current = null
    }
  }

  const hideBar = () => {
    if (timeoutRef.current) {
      clearTimeout(timeoutRef.current)
    }
    // 延迟隐藏
    timeoutRef.current = setTimeout(() => {
      setIsVisible(false)
    }, 2000)
  }

  useEffect(() => {
    let lastMouseMoveTime = Date.now()

    const handleMouseMove = () => {
      lastMouseMoveTime = Date.now()
      // 鼠标在任何位置移动时都显示按钮
      showBar()
    }

    // 检测鼠标是否停止移动
    const checkMouseStop = () => {
      const timeSinceLastMove = Date.now() - lastMouseMoveTime
      // 如果鼠标3秒没有移动，隐藏按钮
      if (timeSinceLastMove > 3000 && isVisible) {
        setIsVisible(false)
      }
    }

    // 立即添加鼠标移动监听
    window.addEventListener('mousemove', handleMouseMove, { passive: true })
    
    // 定期检查鼠标是否停止移动
    const mouseStopInterval = setInterval(checkMouseStop, 1000)

    return () => {
      window.removeEventListener('mousemove', handleMouseMove)
      clearInterval(mouseStopInterval)
      if (timeoutRef.current) {
        clearTimeout(timeoutRef.current)
      }
      if (mouseMoveTimeoutRef.current) {
        clearTimeout(mouseMoveTimeoutRef.current)
      }
    }
  }, [isVisible])

  return (
    <div
      className="fixed top-0 left-0 right-0 z-[9999]"
      style={{ background: 'transparent' }}
      onMouseEnter={showBar}
      onMouseLeave={hideBar}
    >
      {/* 顶部检测区域 */}
      <div
        className="fixed top-0 left-0 right-0 h-24"
        onMouseEnter={showBar}
        style={{ pointerEvents: 'auto' }}
      />
      
      {/* 顶部栏内容 - 完全透明 */}
      <div
        className={`transition-all duration-300 ease-in-out ${
          isVisible
            ? 'opacity-100 translate-y-0'
            : 'opacity-0 -translate-y-full'
        }`}
        style={{
          pointerEvents: isVisible ? 'auto' : 'none',
        }}
      >
        <div className="px-6 py-4" style={{ background: 'transparent' }}>
          <div className="flex items-center justify-between gap-4">
            <Button
              variant="ghost"
              size="icon"
              onClick={onBack}
              className="text-white hover:text-white hover:bg-white/20 transition-all border-2 border-white/40 hover:border-white/60 bg-transparent backdrop-blur-none shadow-none rounded-full"
              style={{
                pointerEvents: 'auto',
                width: '48px',
                height: '48px',
                borderRadius: '50%',
                background: 'transparent',
              }}
            >
              <ArrowLeft className="h-6 w-6" />
            </Button>

            {onStatsToggle && (
              <Button
                variant="ghost"
                size="icon"
                onClick={() => onStatsToggle(!isStatsEnabled)}
                className={`rounded-full shadow-none bg-transparent text-white hover:bg-transparent hover:text-white focus-visible:ring-0 focus-visible:ring-offset-0 ${
                  isStatsEnabled ? '' : 'opacity-80 hover:opacity-100'
                }`}
                style={{
                  pointerEvents: 'auto',
                  backdropFilter: 'none',
                  border: 'none',
                  backgroundColor: 'transparent',
                  boxShadow: 'none',
                }}
                aria-label={
                  isStatsEnabled
                    ? t('streaming.monitor.disable')
                    : t('streaming.monitor.enable')
                }
                title={
                  isStatsEnabled
                    ? t('streaming.monitor.disable')
                    : t('streaming.monitor.enable')
                }
              >
                <Activity className="h-5 w-5 text-white" />
              </Button>
            )}
          </div>
        </div>
      </div>
    </div>
  )
}
