import { useEffect, useRef } from 'react'
import { useGamepad, useGamepadInput } from '@/hooks/use-gamepad'
import { GamepadButton, type GamepadInputEvent } from '@/service/gamepad.service'

/**
 * Hook: 手柄导航功能
 * 允许使用手柄在 UI 中导航和点击
 */
export function useGamepadNavigation(enabled: boolean = true) {
  const { isEnabled } = useGamepad()
  const lastButtonPressRef = useRef<number>(0)

  // 处理手柄输入
  const handleGamepadInput = (event: GamepadInputEvent) => {
    // 如果手柄被禁用，不处理输入
    if (!isEnabled || !enabled) {
      return
    }

    // 防抖：避免重复触发
    const now = Date.now()
    if (now - lastButtonPressRef.current < 200) {
      return
    }
    lastButtonPressRef.current = now

    // 处理按钮输入
    if (event.buttonIndex !== undefined && event.buttonState?.pressed) {
      const buttonIndex = event.buttonIndex

      // A 按钮（确认/点击）
      if (buttonIndex === GamepadButton.A) {
        const activeElement = document.activeElement as HTMLElement
        if (activeElement) {
          // 如果是按钮或可点击元素，触发点击
          if (
            activeElement.tagName === 'BUTTON' ||
            activeElement.getAttribute('role') === 'button' ||
            activeElement.onclick !== null ||
            activeElement.classList.contains('cursor-pointer')
          ) {
            activeElement.click()
          } else {
            // 尝试找到最近的按钮或可点击元素
            const clickable = activeElement.closest('button, [role="button"], .cursor-pointer, [onclick]')
            if (clickable) {
              ;(clickable as HTMLElement).click()
            }
          }
        }
        return
      }

      // B 按钮（取消/返回）
      if (buttonIndex === GamepadButton.B) {
        // 可以用于关闭对话框或返回
        const activeElement = document.activeElement as HTMLElement
        if (activeElement) {
          // 按 ESC 键
          const escEvent = new KeyboardEvent('keydown', {
            key: 'Escape',
            code: 'Escape',
            keyCode: 27,
            bubbles: true,
            cancelable: true,
          })
          activeElement.dispatchEvent(escEvent)
        }
        return
      }

      // D-Pad 导航
      if (buttonIndex === GamepadButton.DPadUp) {
        navigateFocus('up')
        return
      }
      if (buttonIndex === GamepadButton.DPadDown) {
        navigateFocus('down')
        return
      }
      if (buttonIndex === GamepadButton.DPadLeft) {
        navigateFocus('left')
        return
      }
      if (buttonIndex === GamepadButton.DPadRight) {
        navigateFocus('right')
        return
      }
    }

    // 处理摇杆输入（转换为 D-Pad）
    if (event.axisIndex !== undefined && event.axisValue !== undefined) {
      const axisIndex = event.axisIndex
      const axisValue = event.axisValue
      const threshold = 0.5

      // 左摇杆 X 轴（左右）
      if (axisIndex === 0) {
        if (axisValue < -threshold) {
          navigateFocus('left')
        } else if (axisValue > threshold) {
          navigateFocus('right')
        }
        return
      }

      // 左摇杆 Y 轴（上下）
      if (axisIndex === 1) {
        if (axisValue < -threshold) {
          navigateFocus('up')
        } else if (axisValue > threshold) {
          navigateFocus('down')
        }
        return
      }
    }
  }

  // 导航焦点
  const navigateFocus = (direction: 'up' | 'down' | 'left' | 'right') => {
    const focusableSelectors = [
      'button:not([disabled])',
      '[href]',
      'input:not([disabled])',
      'select:not([disabled])',
      'textarea:not([disabled])',
      '[tabindex]:not([tabindex="-1"])',
      '[role="button"]:not([disabled])',
      '[role="menuitem"]',
      '[role="option"]',
    ].join(', ')

    const focusableElements = Array.from(
      document.querySelectorAll<HTMLElement>(focusableSelectors)
    ).filter((el) => {
      const style = window.getComputedStyle(el)
      return style.display !== 'none' && style.visibility !== 'hidden' && style.opacity !== '0'
    })

    if (focusableElements.length === 0) {
      return
    }

    const currentElement = document.activeElement as HTMLElement
    const currentIndex = focusableElements.indexOf(currentElement)

    let nextIndex = currentIndex

    if (direction === 'down' || direction === 'right') {
      nextIndex = currentIndex < focusableElements.length - 1 ? currentIndex + 1 : 0
    } else {
      nextIndex = currentIndex > 0 ? currentIndex - 1 : focusableElements.length - 1
    }

    const nextElement = focusableElements[nextIndex]
    if (nextElement) {
      nextElement.focus()
      // 滚动到可见区域
      nextElement.scrollIntoView({ behavior: 'smooth', block: 'nearest' })
    }
  }

  // 启用手柄输入监听
  useGamepadInput(handleGamepadInput, enabled && isEnabled)

  // 初始化：如果没有焦点元素，聚焦第一个可聚焦元素
  useEffect(() => {
    if (!enabled || !isEnabled) {
      return
    }

    const focusableSelectors = [
      'button:not([disabled])',
      '[href]',
      'input:not([disabled])',
      'select:not([disabled])',
      'textarea:not([disabled])',
      '[tabindex]:not([tabindex="-1"])',
      '[role="button"]:not([disabled])',
    ].join(', ')

    const focusableElements = Array.from(
      document.querySelectorAll<HTMLElement>(focusableSelectors)
    ).filter((el) => {
      const style = window.getComputedStyle(el)
      return style.display !== 'none' && style.visibility !== 'hidden' && style.opacity !== '0'
    })

    // 如果当前没有焦点元素，且页面有可聚焦元素，聚焦第一个
    if (!document.activeElement || document.activeElement === document.body) {
      if (focusableElements.length > 0) {
        // 延迟一点，确保页面已渲染
        setTimeout(() => {
          focusableElements[0]?.focus()
        }, 100)
      }
    }
  }, [enabled, isEnabled])
}

