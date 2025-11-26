// 全局虚拟摇杆状态（非 React Hook，确保所有组件共享同一份状态）

interface StickVector {
  x: number
  y: number
}

interface StickState {
  leftStick: StickVector
  rightStick: StickVector
}

// 全局虚拟摇杆状态
export const virtualStickState: StickState = {
  leftStick: { x: 0, y: 0 },
  rightStick: { x: 0, y: 0 },
}

// 虚拟摇杆是否激活
export const virtualJoystickActiveState: { left: boolean; right: boolean } = {
  left: false,
  right: false,
}

// 设置虚拟摇杆值
export function setVirtualStick(stick: 'left' | 'right', x: number, y: number) {
  const target = stick === 'left' ? virtualStickState.leftStick : virtualStickState.rightStick
  target.x = Math.max(-1, Math.min(1, x))
  target.y = Math.max(-1, Math.min(1, y))
}

// 设置虚拟摇杆激活状态
export function setVirtualStickActive(stick: 'left' | 'right', active: boolean) {
  if (stick === 'left') {
    virtualJoystickActiveState.left = active
  } else {
    virtualJoystickActiveState.right = active
  }
}

// 重置虚拟摇杆状态
export function resetVirtualJoystick() {
  virtualStickState.leftStick.x = 0
  virtualStickState.leftStick.y = 0
  virtualStickState.rightStick.x = 0
  virtualStickState.rightStick.y = 0
  virtualJoystickActiveState.left = false
  virtualJoystickActiveState.right = false
}

