import { forwardRef, useRef, useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { useDevice } from '@/hooks/use-device'

interface StreamingVideoPlayerProps {
  isConnected: boolean
  isConnecting: boolean
  onConnect: () => void
}

export const StreamingVideoPlayer = forwardRef<HTMLVideoElement, StreamingVideoPlayerProps>(
  ({ isConnected, isConnecting, onConnect }, ref) => {
    const { t } = useTranslation()
    const { isMobile } = useDevice()
    const containerRef = useRef<HTMLDivElement>(null)
    const videoElementRef = useRef<HTMLVideoElement | null>(null)

    // 合并 ref
    useEffect(() => {
      if (typeof ref === 'function') {
        ref(videoElementRef.current)
      } else if (ref) {
        ref.current = videoElementRef.current
      }
    }, [ref])

    // 在移动端完全禁止全屏功能
    useEffect(() => {
      if (!isMobile) return

      const videoElement = videoElementRef.current
      if (!videoElement) return

      // 退出全屏的函数（只退出视频元素的全屏）
      const exitFullscreen = () => {
        const fullscreenElement =
          document.fullscreenElement ||
          (document as any).webkitFullscreenElement ||
          (document as any).mozFullScreenElement ||
          (document as any).msFullscreenElement

        // 只有当全屏元素是视频元素或容器时才退出
        if (fullscreenElement === videoElement || fullscreenElement === containerRef.current) {
          if (document.fullscreenElement) {
            document.exitFullscreen().catch(() => {})
          } else if ((document as any).webkitFullscreenElement) {
            ;(document as any).webkitExitFullscreen?.()
          } else if ((document as any).mozFullScreenElement) {
            ;(document as any).mozCancelFullScreen?.()
          } else if ((document as any).msFullscreenElement) {
            ;(document as any).msExitFullscreen?.()
          }
        }
      }

      // 检查并退出全屏的函数（只退出视频元素的全屏，不退出页面全屏）
      const checkAndExitFullscreen = () => {
        const fullscreenElement =
          document.fullscreenElement ||
          (document as any).webkitFullscreenElement ||
          (document as any).mozFullScreenElement ||
          (document as any).msFullscreenElement

        // 只有当全屏元素是视频元素本身时才退出（允许页面全屏）
        if (fullscreenElement === videoElement || fullscreenElement === containerRef.current) {
          exitFullscreen()
        }
      }

      // 监听全屏变化事件，如果视频元素进入全屏则立即退出
      const fullscreenChangeHandler = () => {
        checkAndExitFullscreen()
      }

      // 监听所有全屏相关事件
      const events = [
        'fullscreenchange',
        'webkitfullscreenchange',
        'mozfullscreenchange',
        'MSFullscreenChange',
      ]

      events.forEach((event) => {
        document.addEventListener(event, fullscreenChangeHandler)
      })

      // 阻止视频元素的全屏请求
      const preventFullscreen = (e: Event) => {
        e.preventDefault()
        e.stopPropagation()
        exitFullscreen()
        return false
      }

      // 阻止双击全屏
      const preventDoubleClick = (e: MouseEvent) => {
        if (e.detail === 2) {
          e.preventDefault()
          e.stopPropagation()
        }
      }

      videoElement.addEventListener('dblclick', preventDoubleClick)
      videoElement.addEventListener('webkitbeginfullscreen', preventFullscreen as EventListener)
      videoElement.addEventListener('webkitendfullscreen', preventFullscreen as EventListener)

      // 在视频元素上拦截全屏 API 调用
      const originalRequestFullscreen = videoElement.requestFullscreen
      const originalWebkitRequestFullscreen = (videoElement as any).webkitRequestFullscreen
      const originalMozRequestFullScreen = (videoElement as any).mozRequestFullScreen
      const originalMsRequestFullscreen = (videoElement as any).msRequestFullscreen

      videoElement.requestFullscreen = function () {
        return Promise.reject(new Error('Fullscreen is disabled on mobile'))
      }
      ;(videoElement as any).webkitRequestFullscreen = function () {
        return Promise.reject(new Error('Fullscreen is disabled on mobile'))
      }
      ;(videoElement as any).mozRequestFullScreen = function () {
        return Promise.reject(new Error('Fullscreen is disabled on mobile'))
      }
      ;(videoElement as any).msRequestFullscreen = function () {
        return Promise.reject(new Error('Fullscreen is disabled on mobile'))
      }

      // 定期检查全屏状态（防止某些浏览器绕过事件）
      const checkInterval = setInterval(() => {
        checkAndExitFullscreen()
      }, 500)

      return () => {
        events.forEach((event) => {
          document.removeEventListener(event, fullscreenChangeHandler)
        })
        videoElement.removeEventListener('dblclick', preventDoubleClick)
        videoElement.removeEventListener('webkitbeginfullscreen', preventFullscreen as EventListener)
        videoElement.removeEventListener('webkitendfullscreen', preventFullscreen as EventListener)
        clearInterval(checkInterval)

        // 恢复原始 API
        videoElement.requestFullscreen = originalRequestFullscreen
        ;(videoElement as any).webkitRequestFullscreen = originalWebkitRequestFullscreen
        ;(videoElement as any).mozRequestFullScreen = originalMozRequestFullScreen
        ;(videoElement as any).msRequestFullscreen = originalMsRequestFullscreen
      }
    }, [isMobile])

    return (
      <div
        ref={containerRef}
        className="w-full h-full bg-black flex items-center justify-center fullscreen-container"
        style={{
          width: '100%',
          height: '100%',
          // 在移动端禁用全屏相关的样式
          ...(isMobile && {
            position: 'relative',
            overflow: 'hidden',
          }),
        }}
        onDoubleClick={(e) => {
          // 在移动端阻止容器双击全屏
          if (isMobile) {
            e.preventDefault()
            e.stopPropagation()
          }
        }}
      >
        <video
          ref={videoElementRef}
          autoPlay
          playsInline
          muted={false}
          preload="auto"
          controls={false}
          disablePictureInPicture
          className="w-full h-full object-contain bg-black no-select"
          style={{
            width: '100%',
            height: '100%',
            objectFit: 'contain',
            backgroundColor: '#000000',
            background: '#000000',
            touchAction: 'none',
            WebkitTouchCallout: 'none',
            // 禁用全屏相关的 CSS 属性
            ...(isMobile && {
              pointerEvents: 'auto',
              // iOS Safari 特殊样式，防止全屏
              WebkitPlaysinline: 'true',
            }),
          } as React.CSSProperties}
          tabIndex={-1}
          onTouchStart={(e) => {
            // 防止移动端默认行为（如长按显示菜单、双击缩放等）
            if (isMobile) {
              if (e.touches.length > 1) {
                e.preventDefault()
              }
            }
          }}
          onContextMenu={(e) => {
            // 禁用右键菜单，防止显示播放器控制选项
            e.preventDefault()
          }}
          onDoubleClick={(e) => {
            // 在移动端阻止双击全屏
            if (isMobile) {
              e.preventDefault()
              e.stopPropagation()
            }
          }}
          onClick={(e) => {
            // 在移动端阻止点击可能触发的全屏行为
            if (isMobile) {
              // 不阻止点击事件本身，只阻止可能的全屏行为
              e.stopPropagation()
            }
          }}
        />
        {!isConnected && (
          <div className="absolute inset-0 flex items-center justify-center bg-black z-10 pointer-events-none">
            <div className="text-center px-4">
              <p className="text-gray-400 mb-4 text-sm sm:text-base">
                {isConnecting ? t('streaming.videoPlayer.connecting') : t('streaming.videoPlayer.waiting')}
              </p>
              {!isConnecting && (
                <div className="pointer-events-auto">
                  <Button 
                    onClick={onConnect} 
                    className="bg-blue-600 hover:bg-blue-700 min-h-[44px] px-6"
                  >
                    {t('streaming.videoPlayer.start')}
                  </Button>
                </div>
              )}
            </div>
          </div>
        )}
      </div>
    )
  }
)

StreamingVideoPlayer.displayName = 'StreamingVideoPlayer'

