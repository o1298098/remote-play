import type { ControllerLightbarEvent, ControllerTriggerEffectsEvent } from '@/types/controller'
import { createOrientationTracker } from './dualsense-orientation'

// 浏览器 Gamepad API 不支持自适应扳机、光条、体感和触摸板，这里通过 WebHID 直接和 DualSense 收发报告。
// 报告格式与 SDL hidapi_ps5 / chiaki-ng 一致。
// 蓝牙下一旦发送输出报告，手柄会切到增强输入模式，Gamepad API 读不到按键，
// 因此蓝牙连接时用 WebHID 读到的数据替换 navigator.getGamepads() 里的 DualSense。

const SONY_VENDOR_ID = 0x054c
const DUALSENSE_PRODUCT_ID = 0x0ce6
const DUALSENSE_EDGE_PRODUCT_ID = 0x0df2
const USB_INPUT_REPORT_ID = 0x01
const USB_OUTPUT_REPORT_ID = 0x02
const BT_INPUT_REPORT_ID = 0x31
const BT_OUTPUT_REPORT_ID = 0x31
const BT_OUTPUT_REPORT_SIZE = 77
const FEATURE_CALIBRATION = 0x05
const FEATURE_FIRMWARE_INFO = 0x20
const EFFECTS_SIZE = 47

// DS5EffectsState_t 字段偏移
const OFFSET_ENABLE_BITS1 = 0
const OFFSET_ENABLE_BITS2 = 1
const OFFSET_RUMBLE_RIGHT = 2
const OFFSET_RUMBLE_LEFT = 3
const OFFSET_RIGHT_TRIGGER = 10
const OFFSET_LEFT_TRIGGER = 21
const OFFSET_INTENSITY = 36
const OFFSET_ENABLE_BITS3 = 38
const OFFSET_PAD_LIGHTS = 43
const OFFSET_LED_RED = 44

const ENABLE1_RUMBLE_EMULATION = 0x01
const ENABLE1_DISABLE_AUDIO_HAPTICS = 0x02
const ENABLE1_RIGHT_TRIGGER = 0x04
const ENABLE1_LEFT_TRIGGER = 0x08
const ENABLE2_LED_COLOR = 0x04
const ENABLE2_PAD_LIGHTS = 0x10
const ENABLE2_INTENSITY = 0x40
const ENABLE3_IMPROVED_RUMBLE = 0x04
// 2.24 及以上固件使用改进的震动模拟
const IMPROVED_RUMBLE_FIRMWARE = 0x0224

const PLAYER_LIGHTS = [0x04, 0x0a, 0x15, 0x1b, 0x1f, 0x11, 0x0e]
const TRIGGER_EFFECT_OFF = 0x05

const GYRO_RES_PER_DEGREE = 1024
const ACCEL_RES_PER_G = 8192
const SENSOR_TICKS_PER_US = 3

const MOTION_EMIT_INTERVAL_MS = 8
const TOUCH_EMIT_INTERVAL_MS = 8
// 超过这个时间没收到输入报告，就不再替换 Gamepad API 的数据
const INPUT_STALE_MS = 500
// 和 chiaki-ng 一样，震动最多保持 5 秒，避免丢了停止事件后一直震
const RUMBLE_SAFETY_STOP_MS = 5000

interface HIDReportInfoLike {
  reportId: number
}

interface HIDCollectionInfoLike {
  outputReports?: HIDReportInfoLike[]
}

interface HIDInputReportEventLike extends Event {
  reportId: number
  data: DataView
}

interface HIDDeviceLike {
  opened: boolean
  vendorId: number
  productId: number
  productName: string
  collections: HIDCollectionInfoLike[]
  open(): Promise<void>
  close(): Promise<void>
  sendReport(reportId: number, data: Uint8Array): Promise<void>
  receiveFeatureReport(reportId: number): Promise<DataView>
  addEventListener(type: 'inputreport', listener: (event: HIDInputReportEventLike) => void): void
  removeEventListener(type: 'inputreport', listener: (event: HIDInputReportEventLike) => void): void
}

interface HIDConnectionEventLike extends Event {
  device: HIDDeviceLike
}

interface HIDLike {
  getDevices(): Promise<HIDDeviceLike[]>
  requestDevice(options: { filters: Array<{ vendorId: number; productId?: number }> }): Promise<HIDDeviceLike[]>
  addEventListener(type: 'connect' | 'disconnect', listener: (event: HIDConnectionEventLike) => void): void
}

export type DualSenseConnection = 'none' | 'usb' | 'bluetooth'

export interface DualSenseStatus {
  supported: boolean
  connection: DualSenseConnection
  productName: string | null
}

export interface DualSenseMotion {
  gyroX: number
  gyroY: number
  gyroZ: number
  accelX: number
  accelY: number
  accelZ: number
  orientX: number
  orientY: number
  orientZ: number
  orientW: number
}

export interface DualSenseTouchSlot {
  down: boolean
  contact: number
  x: number
  y: number
}

interface CalibrationEntry {
  bias: number
  sensitivity: number
}

interface InputSnapshot {
  receivedAt: number
  leftX: number
  leftY: number
  rightX: number
  rightY: number
  l2: number
  r2: number
  buttons0: number
  buttons1: number
  buttons2: number
}

type StatusListener = (status: DualSenseStatus) => void
type MotionListener = (motion: DualSenseMotion) => void
type TouchListener = (slots: DualSenseTouchSlot[]) => void

const DEVICE_FILTERS = [
  { vendorId: SONY_VENDOR_ID, productId: DUALSENSE_PRODUCT_ID },
  { vendorId: SONY_VENDOR_ID, productId: DUALSENSE_EDGE_PRODUCT_ID },
]

let device: HIDDeviceLike | null = null
let connection: DualSenseConnection = 'none'
let isEdge = false
let firmwareVersion = 0
let btOutputSeq = 0
let sendChain: Promise<void> = Promise.resolve()
let lastTriggerEffects: ControllerTriggerEffectsEvent | null = null
let lastLightbar: ControllerLightbarEvent | null = null
let hidListenersRegistered = false
let calibration: CalibrationEntry[] | null = null
let lastInput: InputSnapshot | null = null
let lastSensorTicks: number | null = null
let lastMotionEmit = 0
let lastTouchEmit = 0
let lastTouchSlots: DualSenseTouchSlot[] = [
  { down: false, contact: 0, x: 0, y: 0 },
  { down: false, contact: 0, x: 0, y: 0 },
]
let touchPending = false
let rumbleStopTimer: ReturnType<typeof setTimeout> | null = null
const orientation = createOrientationTracker()

const statusListeners = new Set<StatusListener>()
const motionListeners = new Set<MotionListener>()
const touchListeners = new Set<TouchListener>()

function getHid(): HIDLike | null {
  if (typeof navigator === 'undefined') {
    return null
  }
  return (navigator as Navigator & { hid?: HIDLike }).hid ?? null
}

function isDualSense(candidate: HIDDeviceLike): boolean {
  return (
    candidate.vendorId === SONY_VENDOR_ID &&
    (candidate.productId === DUALSENSE_PRODUCT_ID || candidate.productId === DUALSENSE_EDGE_PRODUCT_ID)
  )
}

function detectConnection(candidate: HIDDeviceLike): DualSenseConnection {
  const isBluetooth = candidate.collections.some((collection) =>
    (collection.outputReports ?? []).some((report) => report.reportId === BT_OUTPUT_REPORT_ID)
  )
  return isBluetooth ? 'bluetooth' : 'usb'
}

export function isWebHidSupported(): boolean {
  return getHid() !== null
}

/**
 * WebHID 不可用的原因：浏览器只在安全上下文（HTTPS 或 localhost）中提供 navigator.hid
 */
export function getWebHidUnavailableReason(): 'insecure-context' | 'unsupported' | null {
  if (isWebHidSupported()) {
    return null
  }
  return typeof window !== 'undefined' && !window.isSecureContext ? 'insecure-context' : 'unsupported'
}

export function isDualSenseHidActive(): boolean {
  return device !== null
}

export function getDualSenseStatus(): DualSenseStatus {
  return {
    supported: isWebHidSupported(),
    connection,
    productName: device?.productName ?? null,
  }
}

function subscribe<T>(set: Set<T>, listener: T): () => void {
  set.add(listener)
  return () => {
    set.delete(listener)
  }
}

export function onDualSenseStatusChange(listener: StatusListener): () => void {
  const unsubscribe = subscribe(statusListeners, listener)
  listener(getDualSenseStatus())
  return unsubscribe
}

export function onDualSenseMotion(listener: MotionListener): () => void {
  return subscribe(motionListeners, listener)
}

export function onDualSenseTouchpad(listener: TouchListener): () => void {
  return subscribe(touchListeners, listener)
}

function notify<T>(set: Set<(value: T) => void>, value: T) {
  set.forEach((listener) => {
    try {
      listener(value)
    } catch (error) {
      console.warn('[dualsense-hid] listener error:', error)
    }
  })
}

function notifyStatus() {
  notify(statusListeners, getDualSenseStatus())
}

function registerHidListeners(hid: HIDLike) {
  if (hidListenersRegistered) {
    return
  }
  hidListenersRegistered = true

  hid.addEventListener('disconnect', (event) => {
    if (event.device === device) {
      releaseDevice()
    }
  })

  // 已授权的手柄重新连上时自动接管
  hid.addEventListener('connect', (event) => {
    if (!device && isDualSense(event.device)) {
      void takeDevice(event.device)
    }
  })
}

function releaseDevice() {
  device?.removeEventListener('inputreport', handleInputReport)
  device = null
  connection = 'none'
  lastInput = null
  lastSensorTicks = null
  calibration = null
  notifyStatus()
}

async function readFeatureReport(target: HIDDeviceLike, reportId: number): Promise<DataView | null> {
  try {
    return await target.receiveFeatureReport(reportId)
  } catch (error) {
    console.warn(`[dualsense-hid] failed to read feature report 0x${reportId.toString(16)}:`, error)
    return null
  }
}

// 返回第 index 个字节（按 SDL 的下标，下标 0 为报告 ID）
function featureByteReader(view: DataView, reportId: number): (index: number) => number {
  const base = view.byteLength > 0 && view.getUint8(0) === reportId ? 0 : -1
  return (index: number) => view.getUint8(index + base)
}

function parseCalibration(view: DataView): CalibrationEntry[] | null {
  if (view.byteLength < 35) {
    return null
  }
  const byte = featureByteReader(view, FEATURE_CALIBRATION)
  const s16 = (index: number) => {
    const value = byte(index) | (byte(index + 1) << 8)
    return value > 0x7fff ? value - 0x10000 : value
  }

  const gyroPitchBias = s16(1)
  const gyroYawBias = s16(3)
  const gyroRollBias = s16(5)
  const gyroPitchPlus = s16(7)
  const gyroPitchMinus = s16(9)
  const gyroYawPlus = s16(11)
  const gyroYawMinus = s16(13)
  const gyroRollPlus = s16(15)
  const gyroRollMinus = s16(17)
  const gyroSpeedPlus = s16(19)
  const gyroSpeedMinus = s16(21)
  const accXPlus = s16(23)
  const accXMinus = s16(25)
  const accYPlus = s16(27)
  const accYMinus = s16(29)
  const accZPlus = s16(31)
  const accZMinus = s16(33)

  const numerator = (gyroSpeedPlus + gyroSpeedMinus) * GYRO_RES_PER_DEGREE
  const accel = (plus: number, minus: number): CalibrationEntry => {
    const range2g = plus - minus
    return { bias: plus - Math.trunc(range2g / 2), sensitivity: (2 * ACCEL_RES_PER_G) / range2g }
  }
  const entries: CalibrationEntry[] = [
    { bias: gyroPitchBias, sensitivity: numerator / (gyroPitchPlus - gyroPitchMinus) },
    { bias: gyroYawBias, sensitivity: numerator / (gyroYawPlus - gyroYawMinus) },
    { bias: gyroRollBias, sensitivity: numerator / (gyroRollPlus - gyroRollMinus) },
    accel(accXPlus, accXMinus),
    accel(accYPlus, accYMinus),
    accel(accZPlus, accZMinus),
  ]

  // 与 SDL 相同的合理性检查：数据异常时退回未校准换算
  for (let i = 0; i < entries.length; i++) {
    const divisor = i < 3 ? 64 : 1
    const { bias, sensitivity } = entries[i]
    if (!Number.isFinite(sensitivity) || Math.abs(bias) > 1024 || Math.abs(1 - sensitivity / divisor) > 0.5) {
      return null
    }
  }
  return entries
}

function applyCalibration(index: number, raw: number): number {
  let value: number
  if (calibration) {
    value = (raw - calibration[index].bias) * calibration[index].sensitivity
  } else {
    value = index < 3 ? raw * 64 : raw
  }
  // 陀螺仪 rad/s，加速度 g（chiaki 把 SDL 的 m/s² 除以重力加速度）
  return index < 3 ? ((value / GYRO_RES_PER_DEGREE) * Math.PI) / 180 : value / ACCEL_RES_PER_G
}

async function takeDevice(candidate: HIDDeviceLike): Promise<boolean> {
  try {
    if (!candidate.opened) {
      await candidate.open()
    }
  } catch (error) {
    console.warn('[dualsense-hid] failed to open DualSense:', error)
    return false
  }

  device = candidate
  connection = detectConnection(candidate)
  isEdge = candidate.productId === DUALSENSE_EDGE_PRODUCT_ID
  btOutputSeq = 0
  orientation.reset()
  candidate.addEventListener('inputreport', handleInputReport)

  // 读取校准数据（蓝牙下同时让手柄切换到完整输入报告）
  const calibrationView = await readFeatureReport(candidate, FEATURE_CALIBRATION)
  calibration = calibrationView ? parseCalibration(calibrationView) : null

  const firmwareView = await readFeatureReport(candidate, FEATURE_FIRMWARE_INFO)
  if (firmwareView && firmwareView.byteLength >= 46) {
    const byte = featureByteReader(firmwareView, FEATURE_FIRMWARE_INFO)
    firmwareVersion = byte(44) | (byte(45) << 8)
  } else {
    firmwareVersion = 0
  }

  if (connection === 'bluetooth') {
    installGamepadShim()
    // 空的效果报告：确保手柄进入增强模式
    void sendEffects(new Uint8Array(EFFECTS_SIZE))
  }

  notifyStatus()

  if (lastLightbar) {
    void applyLightbar(lastLightbar)
  }
  if (lastTriggerEffects) {
    void applyTriggerEffects(lastTriggerEffects)
  }
  return true
}

/**
 * 弹出浏览器的设备选择框（必须在用户点击等手势中调用）
 */
export async function requestDualSense(): Promise<boolean> {
  const hid = getHid()
  if (!hid) {
    return false
  }
  registerHidListeners(hid)

  const devices = await hid.requestDevice({ filters: DEVICE_FILTERS })
  const candidate = devices.find(isDualSense)
  return candidate ? takeDevice(candidate) : false
}

/**
 * 使用之前已授权的 DualSense（不弹窗）
 */
export async function autoConnectDualSense(): Promise<boolean> {
  if (device) {
    return true
  }

  const hid = getHid()
  if (!hid) {
    return false
  }
  registerHidListeners(hid)

  const devices = await hid.getDevices()
  const candidate = devices.find(isDualSense)
  return candidate ? takeDevice(candidate) : false
}

export async function disconnectDualSense(): Promise<void> {
  const current = device
  if (!current) {
    return
  }
  await resetDualSenseEffects()
  await sendChain
  releaseDevice()
  try {
    await current.close()
  } catch {
    // ignore
  }
}

// ---------------------------------------------------------------------------
// 输入报告
// ---------------------------------------------------------------------------

function handleInputReport(event: HIDInputReportEventLike) {
  let offset: number
  if (event.reportId === USB_INPUT_REPORT_ID && connection === 'usb') {
    offset = 0
  } else if (event.reportId === BT_INPUT_REPORT_ID) {
    offset = 1
  } else {
    // 蓝牙简易模式报告不含体感和触摸数据
    return
  }

  const data = event.data
  if (data.byteLength < offset + 40) {
    return
  }

  const u8 = (index: number) => data.getUint8(offset + index)
  const s16 = (index: number) => data.getInt16(offset + index, true)
  const now = performance.now()

  lastInput = {
    receivedAt: now,
    leftX: u8(0),
    leftY: u8(1),
    rightX: u8(2),
    rightY: u8(3),
    l2: u8(4),
    r2: u8(5),
    buttons0: u8(7),
    buttons1: u8(8),
    buttons2: u8(9),
  }

  handleMotion(
    [applyCalibration(0, s16(15)), applyCalibration(1, s16(17)), applyCalibration(2, s16(19))],
    [applyCalibration(3, s16(21)), applyCalibration(4, s16(23)), applyCalibration(5, s16(25))],
    data.getUint32(offset + 27, true),
    now
  )

  handleTouch(
    [readTouchSlot(u8(32), u8(33), u8(34), u8(35)), readTouchSlot(u8(36), u8(37), u8(38), u8(39))],
    now
  )
}

function handleMotion(gyro: number[], accel: number[], sensorTicks: number, now: number) {
  let deltaSeconds = 0
  if (lastSensorTicks !== null) {
    deltaSeconds = ((sensorTicks - lastSensorTicks) >>> 0) / SENSOR_TICKS_PER_US / 1_000_000
  }
  lastSensorTicks = sensorTicks
  orientation.update(gyro[0], gyro[1], gyro[2], accel[0], accel[1], accel[2], deltaSeconds)

  if (motionListeners.size === 0 || now - lastMotionEmit < MOTION_EMIT_INTERVAL_MS) {
    return
  }
  lastMotionEmit = now
  notify(motionListeners, orientation.getMotion())
}

function readTouchSlot(counter: number, b0: number, b1: number, b2: number): DualSenseTouchSlot {
  return {
    down: (counter & 0x80) === 0,
    contact: counter & 0x7f,
    x: b0 | ((b1 & 0x0f) << 8),
    y: (b1 >> 4) | (b2 << 4),
  }
}

function handleTouch(slots: DualSenseTouchSlot[], now: number) {
  let transition = false
  let moved = false
  for (let i = 0; i < slots.length; i++) {
    const before = lastTouchSlots[i]
    const current = slots[i]
    if (before.down !== current.down || (current.down && before.contact !== current.contact)) {
      transition = true
    } else if (current.down && (before.x !== current.x || before.y !== current.y)) {
      moved = true
    }
  }

  if (transition || moved) {
    lastTouchSlots = slots
    touchPending = true
  }

  // 按下/抬起立即发送，移动按间隔合并发送
  if (touchPending && (transition || now - lastTouchEmit >= TOUCH_EMIT_INTERVAL_MS)) {
    touchPending = false
    lastTouchEmit = now
    notify(touchListeners, lastTouchSlots.map((slot) => ({ ...slot })))
  }
}

/**
 * 主机要求把当前姿态设为原点（同 chiaki resetMotionControls）
 */
export function resetDualSenseMotion() {
  orientation.resetToCurrent()
}

// ---------------------------------------------------------------------------
// 蓝牙：用 WebHID 数据替换 Gamepad API 里读不到输入的 DualSense
// ---------------------------------------------------------------------------

let gamepadShimInstalled = false

function isDualSenseGamepadId(id: string): boolean {
  const lower = id.toLowerCase()
  return lower.includes('054c') && (lower.includes('0ce6') || lower.includes('0df2'))
}

function shouldReplaceGamepad(): boolean {
  return (
    connection === 'bluetooth' && lastInput !== null && performance.now() - lastInput.receivedAt < INPUT_STALE_MS
  )
}

function axisValue(raw: number): number {
  return Math.max(-1, Math.min(1, (raw - 128) / 127.5))
}

function buildVirtualGamepad(base: Gamepad, input: InputSnapshot): Gamepad {
  const hat = input.buttons0 & 0x0f
  const dpadUp = hat === 7 || hat === 0 || hat === 1
  const dpadRight = hat >= 1 && hat <= 3
  const dpadDown = hat >= 3 && hat <= 5
  const dpadLeft = hat >= 5 && hat <= 7

  const digital = (pressed: boolean) => ({ pressed, touched: pressed, value: pressed ? 1 : 0 })
  const analog = (pressed: boolean, raw: number) => ({ pressed, touched: raw > 0, value: raw / 255 })

  // 标准映射顺序
  const buttons = [
    digital((input.buttons0 & 0x20) !== 0), // 0 Cross
    digital((input.buttons0 & 0x40) !== 0), // 1 Circle
    digital((input.buttons0 & 0x10) !== 0), // 2 Square
    digital((input.buttons0 & 0x80) !== 0), // 3 Triangle
    digital((input.buttons1 & 0x01) !== 0), // 4 L1
    digital((input.buttons1 & 0x02) !== 0), // 5 R1
    analog((input.buttons1 & 0x04) !== 0, input.l2), // 6 L2
    analog((input.buttons1 & 0x08) !== 0, input.r2), // 7 R2
    digital((input.buttons1 & 0x10) !== 0), // 8 Create
    digital((input.buttons1 & 0x20) !== 0), // 9 Options
    digital((input.buttons1 & 0x40) !== 0), // 10 L3
    digital((input.buttons1 & 0x80) !== 0), // 11 R3
    digital(dpadUp), // 12
    digital(dpadDown), // 13
    digital(dpadLeft), // 14
    digital(dpadRight), // 15
    digital((input.buttons2 & 0x01) !== 0), // 16 PS
    digital((input.buttons2 & 0x02) !== 0), // 17 Touchpad
  ]

  return {
    id: base.id,
    index: base.index,
    connected: true,
    mapping: 'standard',
    timestamp: input.receivedAt,
    axes: [axisValue(input.leftX), axisValue(input.leftY), axisValue(input.rightX), axisValue(input.rightY)],
    buttons,
    vibrationActuator: null,
    hapticActuators: [],
  } as unknown as Gamepad
}

function installGamepadShim() {
  if (gamepadShimInstalled || typeof navigator === 'undefined' || typeof navigator.getGamepads !== 'function') {
    return
  }
  gamepadShimInstalled = true

  const original = navigator.getGamepads.bind(navigator)
  Object.defineProperty(navigator, 'getGamepads', {
    configurable: true,
    writable: true,
    value: () => {
      const pads = original()
      const input = lastInput
      if (!input || !shouldReplaceGamepad()) {
        return pads
      }
      return Array.from(pads, (pad) => (pad && isDualSenseGamepadId(pad.id) ? buildVirtualGamepad(pad, input) : pad))
    },
  })
}

// ---------------------------------------------------------------------------
// 输出报告
// ---------------------------------------------------------------------------

const CRC32_TABLE = (() => {
  const table = new Uint32Array(256)
  for (let i = 0; i < 256; i++) {
    let c = i
    for (let k = 0; k < 8; k++) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    }
    table[i] = c >>> 0
  }
  return table
})()

function crc32(chunks: Uint8Array[]): number {
  let crc = 0xffffffff
  for (const chunk of chunks) {
    for (let i = 0; i < chunk.length; i++) {
      crc = CRC32_TABLE[(crc ^ chunk[i]) & 0xff] ^ (crc >>> 8)
    }
  }
  return (crc ^ 0xffffffff) >>> 0
}

function buildBluetoothReport(effects: Uint8Array): Uint8Array {
  // 报告 ID 不在 data 中：[序号<<4] [0x10] [效果 47 字节] ... [CRC32 小端]
  const report = new Uint8Array(BT_OUTPUT_REPORT_SIZE)
  report[0] = (btOutputSeq << 4) & 0xf0
  btOutputSeq = (btOutputSeq + 1) & 0x0f
  report[1] = 0x10
  report.set(effects, 2)
  // CRC 覆盖 HID 头 0xA2 和报告 ID
  const crc = crc32([new Uint8Array([0xa2, BT_OUTPUT_REPORT_ID]), report.subarray(0, BT_OUTPUT_REPORT_SIZE - 4)])
  new DataView(report.buffer).setUint32(BT_OUTPUT_REPORT_SIZE - 4, crc, true)
  return report
}

function sendEffects(effects: Uint8Array): Promise<void> {
  const target = device
  if (!target) {
    return Promise.resolve()
  }

  const isBluetooth = connection === 'bluetooth'
  // WebHID 不允许同一设备并发写报告，按顺序发送；蓝牙报告的序号也必须按发送顺序生成
  sendChain = sendChain
    .then(() =>
      isBluetooth
        ? target.sendReport(BT_OUTPUT_REPORT_ID, buildBluetoothReport(effects))
        : target.sendReport(USB_OUTPUT_REPORT_ID, effects)
    )
    .catch((error) => {
      console.warn('[dualsense-hid] sendReport failed:', error)
    })
  return sendChain
}

function writeTriggerEffect(effects: Uint8Array, offset: number, type: number, data: number[]) {
  effects[offset] = type & 0xff
  for (let i = 0; i < 10; i++) {
    effects[offset + 1 + i] = (data[i] ?? 0) & 0xff
  }
}

export function applyTriggerEffects(event: ControllerTriggerEffectsEvent): Promise<void> {
  lastTriggerEffects = event

  const effects = new Uint8Array(EFFECTS_SIZE)
  effects[OFFSET_ENABLE_BITS1] = ENABLE1_RIGHT_TRIGGER | ENABLE1_LEFT_TRIGGER
  effects[OFFSET_ENABLE_BITS2] = ENABLE2_INTENSITY
  effects[OFFSET_INTENSITY] = event.intensity & 0xff
  writeTriggerEffect(effects, OFFSET_RIGHT_TRIGGER, event.typeRight, event.right)
  writeTriggerEffect(effects, OFFSET_LEFT_TRIGGER, event.typeLeft, event.left)
  return sendEffects(effects)
}

export function applyLightbar(event: ControllerLightbarEvent): Promise<void> {
  lastLightbar = event

  const effects = new Uint8Array(EFFECTS_SIZE)
  effects[OFFSET_ENABLE_BITS2] = ENABLE2_LED_COLOR | ENABLE2_PAD_LIGHTS
  // 0x20：玩家灯立即切换而不是渐变
  effects[OFFSET_PAD_LIGHTS] = PLAYER_LIGHTS[event.playerIndex % PLAYER_LIGHTS.length] | 0x20
  effects[OFFSET_LED_RED] = event.red & 0xff
  effects[OFFSET_LED_RED + 1] = event.green & 0xff
  effects[OFFSET_LED_RED + 2] = event.blue & 0xff
  return sendEffects(effects)
}

/**
 * 普通震动（left 为低频大电机，right 为高频小电机，0-255）
 */
export function applyDualSenseRumble(left: number, right: number): Promise<void> {
  if (rumbleStopTimer !== null) {
    clearTimeout(rumbleStopTimer)
    rumbleStopTimer = null
  }

  const l = Math.max(0, Math.min(255, Math.round(left)))
  const r = Math.max(0, Math.min(255, Math.round(right)))
  const effects = new Uint8Array(EFFECTS_SIZE)
  effects[OFFSET_ENABLE_BITS1] = ENABLE1_DISABLE_AUDIO_HAPTICS
  // 与 SDL / chiaki-ng 一致：Edge 和读不到固件版本时使用新震动模式
  if (!isEdge && firmwareVersion !== 0 && firmwareVersion < IMPROVED_RUMBLE_FIRMWARE) {
    effects[OFFSET_ENABLE_BITS1] |= ENABLE1_RUMBLE_EMULATION
    effects[OFFSET_RUMBLE_LEFT] = l >> 1
    effects[OFFSET_RUMBLE_RIGHT] = r >> 1
  } else {
    effects[OFFSET_ENABLE_BITS3] = ENABLE3_IMPROVED_RUMBLE
    effects[OFFSET_RUMBLE_LEFT] = l
    effects[OFFSET_RUMBLE_RIGHT] = r
  }

  if (l > 0 || r > 0) {
    rumbleStopTimer = setTimeout(() => {
      rumbleStopTimer = null
      void applyDualSenseRumble(0, 0)
    }, RUMBLE_SAFETY_STOP_MS)
  }
  return sendEffects(effects)
}

/**
 * 串流结束时取消扳机阻力和震动，避免手柄一直保持游戏里的效果
 */
export function resetDualSenseEffects(): Promise<void> {
  lastTriggerEffects = null
  lastLightbar = null

  void applyDualSenseRumble(0, 0)
  const effects = new Uint8Array(EFFECTS_SIZE)
  effects[OFFSET_ENABLE_BITS1] = ENABLE1_RIGHT_TRIGGER | ENABLE1_LEFT_TRIGGER
  writeTriggerEffect(effects, OFFSET_RIGHT_TRIGGER, TRIGGER_EFFECT_OFF, [])
  writeTriggerEffect(effects, OFFSET_LEFT_TRIGGER, TRIGGER_EFFECT_OFF, [])
  return sendEffects(effects)
}

/**
 * 判断 Gamepad API 中的手柄是不是已经由 WebHID 接管的 DualSense
 */
export function isHidManagedGamepad(gamepad: Gamepad | null | undefined): boolean {
  return !!gamepad && device !== null && isDualSenseGamepadId(gamepad.id)
}
