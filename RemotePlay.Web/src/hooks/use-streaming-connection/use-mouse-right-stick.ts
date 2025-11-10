import { useCallback, useRef } from 'react'
import { MOUSE_SENSITIVITY, getTimestamp, clamp } from './constants'

interface UseMouseRightStickOptions {
  videoRef: React.RefObject<HTMLVideoElement>
  onPointerLockChange: (isLocked: boolean) => void
  onMouseMove: (x: number, y: number, timestamp: number) => void
}

export const useMouseRightStick = ({ videoRef, onPointerLockChange, onMouseMove }: UseMouseRightStickOptions) => {
  const cleanupRef = useRef<(() => void) | null>(null)

  const tearDown = useCallback(() => {
    if (cleanupRef.current) {
      cleanupRef.current()
      cleanupRef.current = null
    }
  }, [])

  const setup = useCallback(() => {
    tearDown()

    const videoElement = videoRef.current
    if (!videoElement) {
      console.warn('⚠️ 未找到视频元素，无法启用鼠标控制')
      onPointerLockChange(false)
      return
    }

    const handleMouseDown = (event: MouseEvent) => {
      if (event.button === 0) {
        event.preventDefault()
        videoElement.focus?.()
        if (document.pointerLockElement !== videoElement) {
          videoElement.requestPointerLock?.()
        }
      }
    }

    const handleContextMenu = (event: MouseEvent) => {
      if (document.pointerLockElement === videoElement) {
        event.preventDefault()
      }
    }

    const handlePointerLockChange = () => {
      const isLocked = document.pointerLockElement === videoElement
      onPointerLockChange(isLocked)
    }

    const handlePointerLockError = (event: Event) => {
      console.warn('⚠️ Pointer Lock 启用失败:', event)
      onPointerLockChange(false)
    }

    const handleMouseMove = (event: MouseEvent) => {
      if (document.pointerLockElement !== videoElement) {
        return
      }

      const now = getTimestamp()
      const nextX = clamp(event.movementX * MOUSE_SENSITIVITY)
      const nextY = clamp(-event.movementY * MOUSE_SENSITIVITY)
      onMouseMove(nextX, nextY, now)
    }

    const handleWindowBlur = () => {
      if (document.pointerLockElement === videoElement) {
        document.exitPointerLock?.()
      } else {
        onPointerLockChange(false)
      }
    }

    videoElement.addEventListener('mousedown', handleMouseDown)
    videoElement.addEventListener('contextmenu', handleContextMenu)
    document.addEventListener('pointerlockchange', handlePointerLockChange)
    document.addEventListener('pointerlockerror', handlePointerLockError)
    document.addEventListener('mousemove', handleMouseMove)
    window.addEventListener('blur', handleWindowBlur)

    cleanupRef.current = () => {
      videoElement.removeEventListener('mousedown', handleMouseDown)
      videoElement.removeEventListener('contextmenu', handleContextMenu)
      document.removeEventListener('pointerlockchange', handlePointerLockChange)
      document.removeEventListener('pointerlockerror', handlePointerLockError)
      document.removeEventListener('mousemove', handleMouseMove)
      window.removeEventListener('blur', handleWindowBlur)

      if (document.pointerLockElement === videoElement) {
        document.exitPointerLock?.()
      }

      onPointerLockChange(false)
    }

    onPointerLockChange(false)
    console.log('✅ 鼠标控制已启用（右摇杆模拟）')
  }, [onPointerLockChange, onMouseMove, tearDown, videoRef])

  return {
    setup,
    tearDown,
  }
}

