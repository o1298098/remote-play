import {
  CONTROLLER_MAPPING_STORAGE_KEY,
  getKeyboardToButtonMapping,
  getLeftStickKeyVectorMap,
  subscribeControllerMappings,
  type ControllerMappings,
} from '@/types/controller-mapping'

/**
 * 键盘事件处理
 */
type OnButtonPress = (button: string, action: 'press' | 'release') => void | Promise<void>

export type KeyboardHandlerOptions = {
  onLeftStickChange?: (x: number, y: number) => void
}

export function createKeyboardHandler(
  onButtonPress: OnButtonPress,
  options: KeyboardHandlerOptions = {}
): () => void {
  const { onLeftStickChange } = options
  let keyboardMapping = getKeyboardToButtonMapping()
  let leftStickKeyMap = getLeftStickKeyVectorMap()

  const pressedKeys = new Set<string>()
  const activeLeftStickKeys = new Set<string>()

  const refreshKeyboardMapping = (
    mappings?: ControllerMappings | null
  ) => {
    keyboardMapping = getKeyboardToButtonMapping(mappings ?? undefined)
    leftStickKeyMap = getLeftStickKeyVectorMap(mappings ?? undefined)
  }

  const unsubscribeMappingChange = subscribeControllerMappings((mappings) => {
    refreshKeyboardMapping(mappings)
  })

  const handleStorageChange = (event: StorageEvent) => {
    if (event.key && event.key !== CONTROLLER_MAPPING_STORAGE_KEY) {
      return
    }
    refreshKeyboardMapping()
  }

  if (typeof window !== 'undefined') {
    window.addEventListener('storage', handleStorageChange)
  }

  const recomputeLeftStick = () => {
    if (!onLeftStickChange) {
      return
    }

    let x = 0
    let y = 0

    activeLeftStickKeys.forEach((key) => {
      const vector = leftStickKeyMap[key]
      if (vector) {
        x += vector.x
        y += vector.y
      }
    })

    if (x !== 0 || y !== 0) {
      const magnitude = Math.hypot(x, y)
      if (magnitude > 1) {
        x /= magnitude
        y /= magnitude
      }
    }

    onLeftStickChange(x, y)
  }

  const handleKeyDown = (event: KeyboardEvent) => {
    refreshKeyboardMapping()
    
    // 如果焦点在输入框或文本区域，不处理键盘事件（除了 Escape）
    const activeElement = document.activeElement
    const isInputFocused =
      activeElement &&
      (activeElement.tagName === 'INPUT' ||
        activeElement.tagName === 'TEXTAREA' ||
        (activeElement as HTMLElement).isContentEditable)

    // Escape 键始终处理
    if (event.key === 'Escape' || event.code === 'Escape') {
      if (isInputFocused && activeElement instanceof HTMLElement) {
        activeElement.blur()
      }
    } else if (isInputFocused) {
      // 其他键在输入框中时不处理
      return
    }

    // 防止默认行为（某些键）
    const keyCode = event.code || event.key
    if (keyboardMapping[keyCode]) {
      if (
        ['Space', 'Tab', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(
          keyCode
        )
      ) {
        event.preventDefault()
      }
    }

    if (leftStickKeyMap[keyCode]) {
      if (!activeLeftStickKeys.has(keyCode)) {
        activeLeftStickKeys.add(keyCode)
        recomputeLeftStick()
      }
    }

    // 处理按键
    const buttonName = keyboardMapping[keyCode]
    if (buttonName) {
      if (!pressedKeys.has(keyCode)) {
        pressedKeys.add(keyCode)
        console.log('⌨️ 键盘按下:', keyCode, '->', buttonName, 'press')
        void onButtonPress(buttonName, 'press')
      }
    } else {
      // 记录未映射的按键（用于调试）
      console.debug('⌨️ 未映射的按键:', keyCode, event.key)
    }
  }

  const handleKeyUp = (event: KeyboardEvent) => {
    refreshKeyboardMapping()

    const keyCode = event.code || event.key
    const buttonName = keyboardMapping[keyCode]

    if (buttonName && pressedKeys.has(keyCode)) {
      pressedKeys.delete(keyCode)
      console.log('⌨️ 键盘释放:', keyCode, '->', buttonName, 'release')
      void onButtonPress(buttonName, 'release')
    }

    if (leftStickKeyMap[keyCode] && activeLeftStickKeys.has(keyCode)) {
      activeLeftStickKeys.delete(keyCode)
      recomputeLeftStick()
    }
  }

  const handleBlur = () => {
    // 释放所有按下的按钮
    pressedKeys.forEach((keyCode) => {
      const buttonName = keyboardMapping[keyCode]
      if (buttonName) {
        void onButtonPress(buttonName, 'release')
      }
    })
    pressedKeys.clear()

    if (activeLeftStickKeys.size > 0) {
      activeLeftStickKeys.clear()
      recomputeLeftStick()
    }
  }

  // 添加事件监听器（使用 capture 模式确保能捕获所有键盘事件，包括视频元素）
  document.addEventListener('keydown', handleKeyDown, true)
  document.addEventListener('keyup', handleKeyUp, true)
  window.addEventListener('blur', handleBlur)

  // 返回清理函数
  return () => {
    document.removeEventListener('keydown', handleKeyDown, true)
    document.removeEventListener('keyup', handleKeyUp, true)
    window.removeEventListener('blur', handleBlur)
    unsubscribeMappingChange()
    if (typeof window !== 'undefined') {
      window.removeEventListener('storage', handleStorageChange)
    }
    // 释放所有按下的按钮
    pressedKeys.forEach((keyCode) => {
      const buttonName = keyboardMapping[keyCode]
      if (buttonName) {
        void onButtonPress(buttonName, 'release')
      }
    })
    pressedKeys.clear()

    if (activeLeftStickKeys.size > 0) {
      activeLeftStickKeys.clear()
      recomputeLeftStick()
    } else if (onLeftStickChange) {
      onLeftStickChange(0, 0)
    }
  }
}

