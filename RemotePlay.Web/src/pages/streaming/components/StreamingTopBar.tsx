import { useState, useRef, useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { ArrowLeft, Activity, RotateCw, ChevronUp, ChevronDown, Zap } from 'lucide-react'
import { useDevice } from '@/hooks/use-device'

interface StreamingTopBarProps {
  onBack: () => void
  isStatsEnabled?: boolean
  onStatsToggle?: (enabled: boolean) => void
  onRefresh?: () => void
  onForceResetQueue?: () => void
}

export function StreamingTopBar({
  onBack,
  isStatsEnabled = false,
  onStatsToggle,
  onRefresh,
  onForceResetQueue,
}: StreamingTopBarProps) {
  const { t } = useTranslation()
  const { isMobile } = useDevice()
  const [isVisible, setIsVisible] = useState(false)
  const [isMobileBarVisible, setIsMobileBarVisible] = useState(false)
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
    // 移动端不显示顶部栏，桌面端根据鼠标移动显示
    if (isMobile) {
      return
    }

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
  }, [isVisible, isMobile])

  // 移动端：底部工具栏
  if (isMobile) {
    return (
      <>
        {/* 触发图标按钮 - 右下角，始终显示 */}
        <button
          onClick={(e) => {
            e.preventDefault()
            e.stopPropagation()
            setIsMobileBarVisible(!isMobileBarVisible)
          }}
          className="fixed z-[99] w-8 h-8 rounded-full bg-black/80 backdrop-blur-md border border-white/40 flex items-center justify-center text-white shadow-lg active:scale-90 transition-all pointer-events-auto"
          style={{
            touchAction: 'manipulation',
            right: '12px',
            bottom: isMobileBarVisible ? '64px' : '12px',
            WebkitTapHighlightColor: 'transparent',
          }}
          aria-label={isMobileBarVisible ? t('streaming.menu.hide', '隐藏菜单') : t('streaming.menu.show', '显示菜单')}
        >
          {isMobileBarVisible ? (
            <ChevronDown className="h-4 w-4" strokeWidth={2} />
          ) : (
            <ChevronUp className="h-4 w-4" strokeWidth={2} />
          )}
        </button>

        {/* 底部工具栏 */}
        <div
          className={`fixed bottom-0 left-0 right-0 z-[99] pointer-events-auto transition-transform duration-300 ease-out ${
            isMobileBarVisible ? 'translate-y-0' : 'translate-y-full'
          }`}
        >
          <div className="bg-black/60 backdrop-blur-sm border-t border-white/20 px-4 py-2">
            <div className="flex items-center justify-center gap-6">
              {/* 返回按钮 */}
              <button
                onClick={onBack}
                className="flex items-center justify-center text-white/80 hover:text-white active:scale-95 transition-all"
                style={{
                  touchAction: 'manipulation',
                  width: '40px',
                  height: '40px',
                }}
                aria-label={t('streaming.menu.back', '返回')}
              >
                <ArrowLeft className="h-5 w-5" />
              </button>

              {/* 刷新按钮 */}
              {onRefresh && (
                <button
                  onClick={() => onRefresh()}
                  className="flex items-center justify-center text-white/80 hover:text-white active:scale-95 transition-all"
                  style={{
                    touchAction: 'manipulation',
                    width: '40px',
                    height: '40px',
                  }}
                  aria-label={t('streaming.refresh.label', '刷新串流')}
                >
                  <RotateCw className="h-5 w-5" />
                </button>
              )}

              {/* 强制重置队列按钮 */}
              {onForceResetQueue && (
                <button
                  onClick={() => onForceResetQueue()}
                  className="flex items-center justify-center text-amber-400/80 hover:text-amber-400 active:scale-95 transition-all"
                  style={{
                    touchAction: 'manipulation',
                    width: '40px',
                    height: '40px',
                  }}
                  aria-label={t('streaming.resetQueue.label', '重置视频队列')}
                >
                  <Zap className="h-5 w-5" />
                </button>
              )}

              {/* 统计开关 */}
              {onStatsToggle && (
                <button
                  onClick={() => onStatsToggle(!isStatsEnabled)}
                  className={`flex items-center justify-center active:scale-95 transition-all ${
                    isStatsEnabled ? 'text-white' : 'text-white/80 hover:text-white'
                  }`}
                  style={{
                    touchAction: 'manipulation',
                    width: '40px',
                    height: '40px',
                  }}
                  aria-label={
                    isStatsEnabled
                      ? t('streaming.monitor.disable', '关闭统计')
                      : t('streaming.monitor.enable', '显示统计')
                  }
                >
                  <Activity className="h-5 w-5" />
                </button>
              )}
            </div>
          </div>
        </div>
      </>
    )
  }

  // 桌面端：保持原有顶部栏行为
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
                minWidth: '48px',
                minHeight: '48px',
                borderRadius: '50%',
                background: 'transparent',
              }}
            >
              <ArrowLeft className="h-6 w-6" />
            </Button>

            <div className="flex items-center gap-2">
              {onRefresh && (
                <Button
                  variant="ghost"
                  size="icon"
                  onClick={() => onRefresh()}
                  className="rounded-full shadow-none bg-transparent text-white hover:bg-transparent hover:text-white focus-visible:ring-0 focus-visible:ring-offset-0 opacity-80 hover:opacity-100"
                  style={{
                    pointerEvents: 'auto',
                    backdropFilter: 'none',
                    border: 'none',
                    backgroundColor: 'transparent',
                    boxShadow: 'none',
                  }}
                  aria-label={t('streaming.refresh.label', '刷新串流')}
                  title={t('streaming.refresh.label', '刷新串流')}
                >
                  <RotateCw className="h-5 w-5" />
                </Button>
              )}

              {onForceResetQueue && (
                <Button
                  variant="ghost"
                  size="icon"
                  onClick={() => onForceResetQueue()}
                  className="rounded-full shadow-none bg-transparent text-amber-400 hover:bg-transparent hover:text-amber-300 focus-visible:ring-0 focus-visible:ring-offset-0 opacity-80 hover:opacity-100"
                  style={{
                    pointerEvents: 'auto',
                    backdropFilter: 'none',
                    border: 'none',
                    backgroundColor: 'transparent',
                    boxShadow: 'none',
                  }}
                  aria-label={t('streaming.resetQueue.label', '重置视频队列')}
                  title={t('streaming.resetQueue.label', '重置视频队列')}
                >
                  <Zap className="h-5 w-5" />
                </Button>
              )}

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
                <Activity className="h-5 w-5" />
              </Button>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
