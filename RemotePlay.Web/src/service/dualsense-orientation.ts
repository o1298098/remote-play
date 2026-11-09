// 手柄姿态估计：移植自 chiaki-ng lib/src/orientation.c（Madgwick IMU 算法）
// 输入陀螺仪 rad/s、加速度 g，输出主机 FeedbackState 需要的姿态四元数

import type { DualSenseMotion } from './dualsense-hid.service'

const SIN_1_4_PI = 0.7071067811865475
const SIN_NEG_1_4_PI = -0.7071067811865475
const COS_1_4_PI = 0.7071067811865476
const COS_NEG_1_4_PI = 0.7071067811865476

const WARMUP_SAMPLES_COUNT = 30
const BETA_WARMUP = 20.0
const BETA_DEFAULT = 0.05

const ORIENT_FUZZ = 0.0007
const FUZZ_FILTER_PREV_WEIGHT = 0.75
const FUZZ_FILTER_PREV_WEIGHT_2X = 0.6

interface Quaternion {
  x: number
  y: number
  z: number
  w: number
}

function initialOrientation(): Quaternion {
  // 为 Madgwick 绕 x 轴旋转 90 度
  return { x: SIN_1_4_PI, y: 0, z: 0, w: COS_1_4_PI }
}

// 类似内核输入子系统的抖动过滤
function fuzz(current: number, previous: number): number {
  if (current < previous + ORIENT_FUZZ / 2 && current > previous - ORIENT_FUZZ / 2) {
    return previous
  }
  if (current < previous + ORIENT_FUZZ && current > previous - ORIENT_FUZZ) {
    return FUZZ_FILTER_PREV_WEIGHT * previous + (1 - FUZZ_FILTER_PREV_WEIGHT) * current
  }
  if (current < previous + ORIENT_FUZZ * 2 && current > previous - ORIENT_FUZZ * 2) {
    return FUZZ_FILTER_PREV_WEIGHT_2X * previous + (1 - FUZZ_FILTER_PREV_WEIGHT_2X) * current
  }
  return current
}

function updateOrientation(
  orient: Quaternion,
  gx: number,
  gy: number,
  gz: number,
  ax: number,
  ay: number,
  az: number,
  beta: number,
  timeStepSec: number
) {
  let q0 = orient.w
  let q1 = orient.x
  let q2 = orient.y
  let q3 = orient.z

  // 陀螺仪给出的四元数变化率
  let qDot1 = 0.5 * (-q1 * gx - q2 * gy - q3 * gz)
  let qDot2 = 0.5 * (q0 * gx + q2 * gz - q3 * gy)
  let qDot3 = 0.5 * (q0 * gy - q1 * gz + q3 * gx)
  let qDot4 = 0.5 * (q0 * gz + q1 * gy - q2 * gx)

  // 加速度有效时才做修正（避免归一化出现 NaN）
  if (!(ax === 0 && ay === 0 && az === 0)) {
    let recipNorm = 1 / Math.sqrt(ax * ax + ay * ay + az * az)
    ax *= recipNorm
    ay *= recipNorm
    az *= recipNorm

    const _2q0 = 2 * q0
    const _2q1 = 2 * q1
    const _2q2 = 2 * q2
    const _2q3 = 2 * q3
    const _4q0 = 4 * q0
    const _4q1 = 4 * q1
    const _4q2 = 4 * q2
    const _8q1 = 8 * q1
    const _8q2 = 8 * q2
    const q0q0 = q0 * q0
    const q1q1 = q1 * q1
    const q2q2 = q2 * q2
    const q3q3 = q3 * q3

    // 梯度下降修正步
    let s0 = _4q0 * q2q2 + _2q2 * ax + _4q0 * q1q1 - _2q1 * ay
    let s1 = _4q1 * q3q3 - _2q3 * ax + 4 * q0q0 * q1 - _2q0 * ay - _4q1 + _8q1 * q1q1 + _8q1 * q2q2 + _4q1 * az
    let s2 = 4 * q0q0 * q2 + _2q0 * ax + _4q2 * q3q3 - _2q3 * ay - _4q2 + _8q2 * q1q1 + _8q2 * q2q2 + _4q2 * az
    let s3 = 4 * q1q1 * q3 - _2q1 * ax + 4 * q2q2 * q3 - _2q2 * ay
    recipNorm = s0 * s0 + s1 * s1 + s2 * s2 + s3 * s3
    // 姿态已经完全正确或完全相反时避免 NaN
    if (recipNorm > 0.000001) {
      recipNorm = 1 / Math.sqrt(recipNorm)
      s0 *= recipNorm
      s1 *= recipNorm
      s2 *= recipNorm
      s3 *= recipNorm

      qDot1 -= beta * s0
      qDot2 -= beta * s1
      qDot3 -= beta * s2
      qDot4 -= beta * s3
    }
  }

  q0 += qDot1 * timeStepSec
  q1 += qDot2 * timeStepSec
  q2 += qDot3 * timeStepSec
  q3 += qDot4 * timeStepSec

  const recipNorm = 1 / Math.sqrt(q0 * q0 + q1 * q1 + q2 * q2 + q3 * q3)
  q0 *= recipNorm
  q1 *= recipNorm
  q2 *= recipNorm
  q3 *= recipNorm

  orient.w = fuzz(q0, orient.w)
  orient.x = fuzz(q1, orient.x)
  orient.y = fuzz(q2, orient.y)
  orient.z = fuzz(q3, orient.z)
}

export interface OrientationTracker {
  update(gx: number, gy: number, gz: number, ax: number, ay: number, az: number, deltaSeconds: number): void
  getMotion(): DualSenseMotion
  /** 完全重置（新连接手柄时） */
  reset(): void
  /** 以当前姿态为原点（主机的 motion reset 请求） */
  resetToCurrent(): void
}

export function createOrientationTracker(): OrientationTracker {
  let orient = initialOrientation()
  let gyro = { x: 0, y: 0, z: 0 }
  let accel = { x: 0, y: 1, z: 0 }
  let realAccel = { x: 0, y: 1, z: 0 }
  let accelZero = { x: 0, y: 0, z: 0 }
  let sampleIndex = 0

  const initTracker = () => {
    orient = initialOrientation()
    gyro = { x: 0, y: 0, z: 0 }
    accel = { x: 0, y: 1, z: 0 }
    sampleIndex = 0
  }

  const update = (gx: number, gy: number, gz: number, ax: number, ay: number, az: number, deltaSeconds: number) => {
    realAccel = { x: ax, y: ay, z: az }
    gyro = { x: gx, y: gy, z: gz }
    accel = { x: ax - accelZero.x, y: ay - accelZero.y, z: az - accelZero.z }
    sampleIndex++
    if (sampleIndex <= 1 || deltaSeconds <= 0) {
      return
    }
    updateOrientation(
      orient,
      gx,
      gy,
      gz,
      accel.x,
      accel.y,
      accel.z,
      sampleIndex < WARMUP_SAMPLES_COUNT ? BETA_WARMUP : BETA_DEFAULT,
      deltaSeconds
    )
  }

  return {
    update,
    getMotion() {
      // 撤销 Madgwick 的 90 度旋转（绕 x 轴 -90 度）
      return {
        gyroX: gyro.x,
        gyroY: gyro.y,
        gyroZ: gyro.z,
        accelX: accel.x,
        accelY: accel.y,
        accelZ: accel.z,
        orientW: COS_NEG_1_4_PI * orient.w - SIN_NEG_1_4_PI * orient.x,
        orientX: COS_NEG_1_4_PI * orient.x + SIN_NEG_1_4_PI * orient.w,
        orientY: COS_NEG_1_4_PI * orient.y - SIN_NEG_1_4_PI * orient.z,
        orientZ: COS_NEG_1_4_PI * orient.z + SIN_NEG_1_4_PI * orient.y,
      }
    },
    reset() {
      accelZero = { x: 0, y: 0, z: 0 }
      initTracker()
    },
    resetToCurrent() {
      accelZero = { x: realAccel.x, y: realAccel.y - 1, z: realAccel.z }
      const current = realAccel
      const currentGyro = gyro
      initTracker()
      update(currentGyro.x, currentGyro.y, currentGyro.z, current.x, current.y, current.z, 0)
    },
  }
}
