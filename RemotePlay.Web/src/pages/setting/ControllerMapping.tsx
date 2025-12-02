import { useState, useEffect, useCallback, useRef, type ChangeEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { useToast } from '@/hooks/use-toast'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Label } from '@/components/ui/label'
import { ArrowLeft, Gamepad2, Keyboard, Save, RotateCcw, Gauge, Vibrate } from 'lucide-react'
import { useAuth } from '@/hooks/use-auth'
import { PS5ControllerLayout } from '@/components/PS5ControllerLayout'
import { useGamepad, useGamepadInput } from '@/hooks/use-gamepad'
import { type GamepadInputEvent } from '@/service/gamepad.service'
import {
  type ControllerButton,
  type ButtonMapping,
  type ControllerMappings,
  type StickDirection,
  getDefaultControllerMappings,
  loadControllerMappings,
  saveControllerMappings,
} from '@/types/controller-mapping'
import {
  getRumbleSettings,
  hasRumbleCapableGamepad,
  onRumbleSettingsChange,
  runRumbleTest,
  updateRumbleSettings,
  type RumbleSettings,
} from '@/service/rumble.service'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'

const BUTTON_INDEX_TO_KEY: Partial<Record<number, ControllerButton>> = {
  0: 'CROSS',
  1: 'CIRCLE',
  2: 'SQUARE',
  3: 'TRIANGLE',
  4: 'L1',
  5: 'R1',
  6: 'L2',
  7: 'R2',
  8: 'SHARE',
  9: 'OPTIONS',
  10: 'L3',
  11: 'R3',
  12: 'DPAD_UP',
  13: 'DPAD_DOWN',
  14: 'DPAD_LEFT',
  15: 'DPAD_RIGHT',
  16: 'PS',
  17: 'TOUCHPAD',
}

const DEFAULT_BUTTON_COUNT = 18

const LEFT_STICK_AXIS = { x: 0, y: 1 }
const RIGHT_STICK_AXIS = { x: 2, y: 3 }

const clampAxis = (value: number) => {
  if (Number.isNaN(value)) return 0
  return Math.max(-1, Math.min(1, value))
}

const clampAnalog = (value: number) => Math.max(0, Math.min(1, Number.isFinite(value) ? value : 0))

const formatAnalogPercentage = (value: number) => {
  const percent = clampAnalog(value) * 100
  const display = percent >= 99.9 ? 100 : percent
  return `${display.toFixed(1)}%`
}

const formatAxisValue = (value: number) => (Number.isFinite(value) ? value.toFixed(2) : '0.00')
export default function ControllerMapping() {
  const { t } = useTranslation()
  const { toast } = useToast()
  const navigate = useNavigate()
  const { isAuthenticated } = useAuth()
  const { isConnected: isGamepadConnected, connectedGamepads } = useGamepad()
  const [mappings, setMappings] = useState<ControllerMappings>(() => getDefaultControllerMappings())
  const [isListening, setIsListening] = useState<ControllerButton | null>(null)
  const [leftStickListening, setLeftStickListening] = useState<StickDirection | null>(null)
  const [isSaving, setIsSaving] = useState(false)
  const [selectedGamepadIndex, setSelectedGamepadIndex] = useState<number | null>(null)
  const [buttonStates, setButtonStates] = useState<Array<{ pressed: boolean; value: number }>>([])
  const [axisStates, setAxisStates] = useState<number[]>([])
  const [rumbleSettings, setRumbleSettingsState] = useState<RumbleSettings>(() => getRumbleSettings())
  const [supportsRumble, setSupportsRumble] = useState<boolean>(false)
  const hasLoadedMappings = useRef(false)

  // 初始化映射
  useEffect(() => {
    if (!isAuthenticated) {
      navigate('/login')
      return
    }

    const loadedMappings = loadControllerMappings()
    setMappings(loadedMappings)
    hasLoadedMappings.current = true
  }, [isAuthenticated, navigate])

  useEffect(() => {
    const unsubscribe = onRumbleSettingsChange((settings) => {
      setRumbleSettingsState(settings)
    })
    return () => {
      unsubscribe()
    }
  }, [])

  useEffect(() => {
    setSupportsRumble(hasRumbleCapableGamepad())
  }, [connectedGamepads, selectedGamepadIndex, isGamepadConnected])

  useEffect(() => {
    if (!isGamepadConnected || connectedGamepads.length === 0) {
      setSelectedGamepadIndex(null)
      setButtonStates([])
      setAxisStates([])
      return
    }

    setSelectedGamepadIndex((current) => {
      if (current !== null && connectedGamepads.some((gamepad) => gamepad.index === current)) {
        return current
      }
      return connectedGamepads[0]?.index ?? null
    })
  }, [isGamepadConnected, connectedGamepads])

  useEffect(() => {
    if (selectedGamepadIndex === null) {
      return
    }
    if (typeof navigator === 'undefined' || typeof navigator.getGamepads !== 'function') {
      return
    }
    const gamepads = navigator.getGamepads()
    const targetGamepad = gamepads[selectedGamepadIndex]
    if (!targetGamepad) {
      return
    }
    setButtonStates(
      targetGamepad.buttons.map((button) => ({
        pressed: button.pressed,
        value: button.value,
      }))
    )
    setAxisStates([...targetGamepad.axes])
  }, [selectedGamepadIndex, connectedGamepads])

  useEffect(() => {
    if (selectedGamepadIndex === null) {
      return
    }
    if (typeof navigator === 'undefined' || typeof navigator.getGamepads !== 'function') {
      return
    }
    let isActive = true
    let frameId: number

    const tick = () => {
      if (!isActive) return
      const gamepads = navigator.getGamepads()
      const targetGamepad = gamepads[selectedGamepadIndex]
      if (targetGamepad) {
        const nextAxes = Array.from(targetGamepad.axes)
        setAxisStates(nextAxes)

        const nextButtons = targetGamepad.buttons.map((button) => ({
          pressed: button.pressed,
          value: button.value,
        }))
        setButtonStates(nextButtons)
      }
      frameId = requestAnimationFrame(tick)
    }

    frameId = requestAnimationFrame(tick)
    return () => {
      isActive = false
      if (frameId) {
        cancelAnimationFrame(frameId)
      }
    }
  }, [selectedGamepadIndex])

  const loadDefaultMappings = () => {
    setMappings(getDefaultControllerMappings())
  }

  const formatKeyDisplay = (code: string, key?: string): string => {
    const keyMap: Record<string, string> = {
      Enter: 'Enter',
      Escape: 'Esc',
      Space: 'Space',
      ArrowUp: '↑',
      ArrowDown: '↓',
      ArrowLeft: '←',
      ArrowRight: '→',
    }
    return keyMap[code] || (key ? keyMap[key] : undefined) || code.replace(/^Key/, '') || key?.toUpperCase() || code
  }

  // 监听映射时的键盘 / 鼠标输入
  useEffect(() => {
    if (!isListening && !leftStickListening) return

    const handleKeyDown = (e: KeyboardEvent) => {
      if (isListening) {
        e.preventDefault()
        e.stopPropagation()

        setMappings((prev) => ({
          ...prev,
          buttons: {
            ...prev.buttons,
            [isListening]: {
              button: isListening,
              key: e.code,
              isMouse: false,
            },
          },
        }))

        setIsListening(null)

        const keyDisplay = formatKeyDisplay(e.code, e.key)
        const buttonName = t(`devices.controllerMapping.buttons.${isListening}`)
        toast({
          title: t('devices.controllerMapping.mappingSet'),
          description: t('devices.controllerMapping.mappingSetDescription', {
            button: buttonName || isListening,
            key: keyDisplay || '?',
          }),
        })
        return
      }

      if (leftStickListening) {
        e.preventDefault()
        e.stopPropagation()

        setMappings((prev) => ({
          ...prev,
          leftStick: {
            ...prev.leftStick,
            [leftStickListening]: e.code,
          },
        }))
        setLeftStickListening(null)

        const keyDisplay = formatKeyDisplay(e.code, e.key)
        const directionName = t(`devices.controllerMapping.leftStick.directions.${leftStickListening}`)
        toast({
          title: t('devices.controllerMapping.leftStick.mappingSet'),
          description: t('devices.controllerMapping.leftStick.mappingSetDescription', {
            direction: directionName || leftStickListening,
            key: keyDisplay || '?',
          }),
        })
      }
    }

    const handleMouseDown = (e: MouseEvent) => {
      if (!isListening) return

      e.preventDefault()
      e.stopPropagation()

      setMappings((prev) => ({
        ...prev,
        buttons: {
          ...prev.buttons,
          [isListening]: {
            button: isListening,
            mouseButton: e.button,
            isMouse: true,
          },
        },
      }))

      setIsListening(null)

      const buttonName = t(`devices.controllerMapping.buttons.${isListening}`)
      const mouseButtonName = t(`devices.controllerMapping.mouseButtons.${e.button}`)
      toast({
        title: t('devices.controllerMapping.mappingSet'),
        description: t('devices.controllerMapping.mappingSetDescription', {
          button: buttonName || isListening,
          key: mouseButtonName || `鼠标按键${e.button}`,
        }),
      })
    }

    window.addEventListener('keydown', handleKeyDown)
    window.addEventListener('mousedown', handleMouseDown)

    return () => {
      window.removeEventListener('keydown', handleKeyDown)
      window.removeEventListener('mousedown', handleMouseDown)
    }
  }, [isListening, leftStickListening, t, toast])

  const handleGamepadInput = useCallback(
    (event: GamepadInputEvent) => {
      if (selectedGamepadIndex === null || event.gamepadIndex !== selectedGamepadIndex) {
        return
      }

      if (typeof event.buttonIndex === 'number' && event.buttonState) {
        const index = event.buttonIndex
        const buttonState = event.buttonState
        setButtonStates((prev) => {
          const next = [...prev]
          next[index] = {
            pressed: buttonState.pressed ?? false,
            value: buttonState.value ?? 0,
          }
          return next
        })
      }

      if (typeof event.axisIndex === 'number' && typeof event.axisValue === 'number') {
        const axisIndex = event.axisIndex
        const axisValue = event.axisValue ?? 0
        setAxisStates((prev) => {
          const next = [...prev]
          next[axisIndex] = axisValue
          return next
        })
      }
    },
    [selectedGamepadIndex]
  )

  useGamepadInput(handleGamepadInput, selectedGamepadIndex !== null)

  const leftStickRawX = axisStates[LEFT_STICK_AXIS.x] ?? 0
  const leftStickRawY = axisStates[LEFT_STICK_AXIS.y] ?? 0
  const rightStickRawX = axisStates[RIGHT_STICK_AXIS.x] ?? 0
  const rightStickRawY = axisStates[RIGHT_STICK_AXIS.y] ?? 0

  const leftStickDisplayX = leftStickRawX
  const leftStickDisplayY = -leftStickRawY
  const rightStickDisplayX = rightStickRawX
  const rightStickDisplayY = -rightStickRawY
  const strengthPercent = Math.round(rumbleSettings.strength * 100)

  const handleToggleRumble = useCallback(() => {
    updateRumbleSettings({ enabled: !rumbleSettings.enabled })
  }, [rumbleSettings.enabled])

  const handleStrengthSliderChange = useCallback(
    (event: ChangeEvent<HTMLInputElement>) => {
      const value = Number(event.target.value)
      if (Number.isNaN(value)) {
        return
      }
      updateRumbleSettings({ strength: value / 100 })
    },
    []
  )

  const handleTestRumble = useCallback(() => {
    const settings = rumbleSettings
    if (!settings.enabled || settings.strength <= 0) {
      toast({
        title: t('devices.controllerMapping.rumble.toastDisabled'),
        variant: 'destructive',
      })
      return
    }

    const success = runRumbleTest(settings)
    setSupportsRumble(hasRumbleCapableGamepad())

    if (success) {
      toast({
        title: t('devices.controllerMapping.rumble.toastTestSuccess'),
      })
    } else {
      toast({
        title: t('devices.controllerMapping.rumble.toastTestFailed'),
        description: t('devices.controllerMapping.rumble.notSupported'),
        variant: 'destructive',
      })
    }
  }, [rumbleSettings, setSupportsRumble, t, toast])

  const handleStartMapping = (button: ControllerButton) => {
    setIsListening(button)
    setLeftStickListening(null)
    toast({
      title: t('devices.controllerMapping.listening'),
      description: t('devices.controllerMapping.listeningDescription', {
          button: t(`devices.controllerMapping.buttons.${button}`)
      }),
      duration: 3000,
    })
  }

  const handleClearMapping = (button: ControllerButton) => {
    setMappings(prev => ({
      ...prev,
      buttons: {
        ...prev.buttons,
        [button]: { button },
      }
    }))
    toast({
      title: t('devices.controllerMapping.mappingCleared'),
      description: t('devices.controllerMapping.mappingClearedDescription', {
          button: t(`devices.controllerMapping.buttons.${button}`)
      }),
    })
  }

  const handleStartLeftStickMapping = (direction: StickDirection) => {
    setIsListening(null)
    setLeftStickListening(direction)
    toast({
      title: t('devices.controllerMapping.leftStick.listening'),
      description: t('devices.controllerMapping.leftStick.listeningDescription', {
        direction: t(`devices.controllerMapping.leftStick.directions.${direction}`),
      }),
      duration: 3000,
    })
  }

  const handleClearLeftStickMapping = (direction: StickDirection) => {
    setLeftStickListening((current) => (current === direction ? null : current))
    setMappings(prev => ({
      ...prev,
      leftStick: {
        ...prev.leftStick,
        [direction]: undefined,
      },
    }))
    toast({
      title: t('devices.controllerMapping.leftStick.mappingCleared'),
      description: t('devices.controllerMapping.leftStick.mappingClearedDescription', {
        direction: t(`devices.controllerMapping.leftStick.directions.${direction}`),
      }),
    })
  }

  const handleSave = () => {
    setIsSaving(true)
    try {
      saveControllerMappings(mappings)
      toast({
        title: t('devices.controllerMapping.saveSuccess'),
        description: t('devices.controllerMapping.saveSuccessDescription'),
      })
    } catch (error) {
      console.error('Failed to save mappings:', error)
      toast({
        title: t('devices.controllerMapping.saveFailed'),
        description: error instanceof Error ? error.message : t('devices.controllerMapping.saveFailedDescription'),
        variant: 'destructive',
      })
    } finally {
      setIsSaving(false)
    }
  }

  const handleReset = () => {
    loadDefaultMappings()
    toast({
      title: t('devices.controllerMapping.reset'),
      description: t('devices.controllerMapping.resetDescription'),
    })
  }

  const getMappingDisplay = (mapping?: ButtonMapping): string => {
    if (!mapping) {
      return ''
    }
    if (mapping.isMouse && mapping.mouseButton !== undefined) {
      return t(`devices.controllerMapping.mouseButtons.${mapping.mouseButton}`)
    }
    if (mapping.key) {
      return formatKeyDisplay(mapping.key)
    }
    return ''
  }

  useEffect(() => {
    if (!hasLoadedMappings.current) {
      return
    }

    const timeoutId = window.setTimeout(() => {
      saveControllerMappings(mappings)
    }, 300)

    return () => {
      window.clearTimeout(timeoutId)
    }
  }, [mappings])

  return (
    <div className="space-y-6">
          {/* Instructions Card */}
          <Card className="bg-blue-50 dark:bg-blue-900/20 border-blue-200 dark:border-blue-800">
            <CardHeader>
              <CardTitle className="text-base flex items-center gap-2">
                <Keyboard className="h-4 w-4 text-blue-600 dark:text-blue-400" />
                {t('devices.controllerMapping.instructions.title')}
              </CardTitle>
            </CardHeader>
            <CardContent>
              <ul className="text-sm text-gray-700 dark:text-gray-300 space-y-2 list-disc list-inside">
                <li>{t('devices.controllerMapping.instructions.step1')}</li>
                <li>{t('devices.controllerMapping.instructions.step2')}</li>
                <li>{t('devices.controllerMapping.instructions.step3')}</li>
              </ul>
            </CardContent>
          </Card>

          {/* Controller Tester */}
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <Gauge className="h-5 w-5 text-blue-600 dark:text-blue-400" />
                {t('devices.controllerMapping.tester.title')}
              </CardTitle>
              <CardDescription>
                {t('devices.controllerMapping.tester.description')}
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              {!isGamepadConnected || connectedGamepads.length === 0 ? (
                <div className="text-sm text-gray-500 dark:text-gray-400">
                  {t('devices.controllerMapping.tester.noGamepad')}
                </div>
              ) : (
                <>
                  <div className="space-y-2">
                    <span className="text-sm font-medium text-gray-700 dark:text-gray-300">
                      {t('devices.controllerMapping.tester.selectLabel')}
                    </span>
                    <Select
                      value={selectedGamepadIndex !== null ? selectedGamepadIndex.toString() : ''}
                      onValueChange={(value) => setSelectedGamepadIndex(Number(value))}
                    >
                      <SelectTrigger className="w-full md:w-72">
                        <SelectValue placeholder={t('devices.controllerMapping.tester.selectPlaceholder')} />
                      </SelectTrigger>
                      <SelectContent>
                        {connectedGamepads.map((gamepad) => (
                          <SelectItem key={gamepad.index} value={gamepad.index.toString()}>
                            {(gamepad.id || t('devices.controllerMapping.tester.unknownDevice')) + ` (#${gamepad.index})`}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </div>

                  <div className="pt-2 lg:pt-0">
                    <div className="flex flex-col lg:flex-row lg:items-start lg:gap-6">
                      <div className="lg:flex-[3]">
                        <h4 className="text-sm font-medium text-gray-700 dark:text-gray-300 mb-3">
                          {t('devices.controllerMapping.tester.stickTitle')}
                        </h4>
                        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 lg:max-w-[520px]">
                          <StickVisualizer
                            label={t('devices.controllerMapping.tester.leftStick')}
                            x={leftStickDisplayX}
                            y={leftStickDisplayY}
                            size={200}
                            info={t('devices.controllerMapping.tester.stickAxes', {
                              x: formatAxisValue(leftStickDisplayX),
                              y: formatAxisValue(leftStickDisplayY),
                            })}
                          />
                          <StickVisualizer
                            label={t('devices.controllerMapping.tester.rightStick')}
                            x={rightStickDisplayX}
                            y={rightStickDisplayY}
                            size={200}
                            info={t('devices.controllerMapping.tester.stickAxes', {
                              x: formatAxisValue(rightStickDisplayX),
                              y: formatAxisValue(rightStickDisplayY),
                            })}
                          />
                        </div>
                      </div>
                      <div className="mt-6 lg:mt-0 lg:flex-[2]">
                        <h4 className="text-sm font-medium text-gray-700 dark:text-gray-300 mb-3">
                          {t('devices.controllerMapping.tester.triggerTitle')}
                        </h4>
                        <div className="flex gap-4">
                          <TriggerVisualizer
                            label={t('devices.controllerMapping.buttons.L2')}
                            value={buttonStates[6]?.value ?? 0}
                            vertical
                            description={t('devices.controllerMapping.tester.analogValue', {
                              value: formatAnalogPercentage(buttonStates[6]?.value ?? 0),
                            })}
                          />
                          <TriggerVisualizer
                            label={t('devices.controllerMapping.buttons.R2')}
                            value={buttonStates[7]?.value ?? 0}
                            vertical
                            description={t('devices.controllerMapping.tester.analogValue', {
                              value: formatAnalogPercentage(buttonStates[7]?.value ?? 0),
                            })}
                          />
                        </div>
                      </div>
                    </div>
                  </div>

                  <div>
                    <h4 className="text-sm font-medium text-gray-700 dark:text-gray-300 mb-3">
                      {t('devices.controllerMapping.tester.buttonTitle')}
                    </h4>
                    <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 gap-2">
                      {Array.from({ length: Math.max(buttonStates.length, DEFAULT_BUTTON_COUNT) }).map((_, index) => {
                        const pressed = buttonStates[index]?.pressed ?? false
                        const value = buttonStates[index]?.value ?? 0
                        const buttonKey = BUTTON_INDEX_TO_KEY[index]
                        const label = buttonKey
                          ? t(`devices.controllerMapping.buttons.${buttonKey}`)
                          : t('devices.controllerMapping.tester.genericButton', { index })
                        return (
                          <div
                            key={index}
                            className={`rounded-lg border p-3 transition-colors ${
                              pressed
                                ? 'border-blue-500 dark:border-blue-400 bg-blue-50 dark:bg-blue-900/30'
                                : 'border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-800'
                            }`}
                          >
                            <div className="flex items-center justify-between text-sm font-medium text-gray-900 dark:text-white">
                              <span className="truncate">{label}</span>
                              <span className="text-xs text-gray-500 dark:text-gray-400">#{index}</span>
                            </div>
                            <div className="mt-2 text-xs text-gray-500 dark:text-gray-400">
                              {t('devices.controllerMapping.tester.analogValue', { value: formatAnalogPercentage(value) })}
                            </div>
                            <div className="mt-1 text-xs font-medium text-gray-700 dark:text-gray-300">
                              {pressed
                                ? t('devices.controllerMapping.tester.buttonPressed')
                                : t('devices.controllerMapping.tester.buttonReleased')}
                            </div>
                          </div>
                        )
                      })}
                    </div>
                  </div>

                  <div className="pt-6 border-t border-gray-200 dark:border-gray-800 space-y-4">
                    <div className="flex items-start gap-3">
                      <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-blue-100 dark:bg-blue-900/30">
                        <Vibrate className="h-5 w-5 text-blue-600 dark:text-blue-400" />
                      </div>
                      <div>
                        <h4 className="text-sm font-medium text-gray-900 dark:text-white">
                          {t('devices.controllerMapping.rumble.title')}
                        </h4>
                        <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                          {t('devices.controllerMapping.rumble.description')}
                        </p>
                      </div>
                    </div>

                    <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                      <span className="text-sm font-medium text-gray-700 dark:text-gray-300">
                        {rumbleSettings.enabled
                          ? t('devices.controllerMapping.rumble.statusOn')
                          : t('devices.controllerMapping.rumble.statusOff')}
                      </span>
                      <Button
                        variant={rumbleSettings.enabled ? 'default' : 'outline'}
                        onClick={handleToggleRumble}
                        className="w-full sm:w-auto"
                      >
                        {rumbleSettings.enabled
                          ? t('devices.controllerMapping.rumble.toggleDisable')
                          : t('devices.controllerMapping.rumble.toggleEnable')}
                      </Button>
                    </div>

                    <div className="space-y-2">
                      <div className="flex items-center justify-between">
                        <Label
                          htmlFor="rumble-strength"
                          className="text-sm font-medium text-gray-700 dark:text-gray-300"
                        >
                          {t('devices.controllerMapping.rumble.strengthLabel')}
                        </Label>
                        <span className="text-xs text-gray-500 dark:text-gray-400">
                          {t('devices.controllerMapping.rumble.strengthValue', { value: strengthPercent })}
                        </span>
                      </div>
                      <input
                        id="rumble-strength"
                        type="range"
                        min={0}
                        max={100}
                        step={5}
                        value={strengthPercent}
                        onChange={handleStrengthSliderChange}
                        disabled={!rumbleSettings.enabled}
                        className="w-full accent-blue-600 dark:accent-blue-500"
                      />
                    </div>

                    <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                      <Button
                        variant="outline"
                        onClick={handleTestRumble}
                        disabled={!rumbleSettings.enabled}
                        className="w-full sm:w-auto"
                      >
                        <Vibrate className="mr-2 h-4 w-4" />
                        {t('devices.controllerMapping.rumble.test')}
                      </Button>
                      {!supportsRumble ? (
                        <p className="text-xs text-gray-500 dark:text-gray-400">
                          {t('devices.controllerMapping.rumble.notSupported')}
                        </p>
                      ) : null}
                    </div>
                  </div>
                </>
              )}
            </CardContent>
          </Card>

          {/* Controller Visualization */}
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <Gamepad2 className="h-5 w-5 text-blue-600 dark:text-blue-400" />
                {t('devices.controllerMapping.gamepad.title')}
              </CardTitle>
              <CardDescription>
                {t('devices.controllerMapping.gamepad.description')}
              </CardDescription>
            </CardHeader>
            <CardContent className="p-6">
              <div className="bg-gray-50 dark:bg-gray-900 rounded-lg p-6">
                <PS5ControllerLayout
                  mappings={mappings.buttons}
                  leftStickMapping={mappings.leftStick}
                  onButtonClick={handleStartMapping}
                  onLeftStickDirectionClick={handleStartLeftStickMapping}
                  onLeftStickDirectionClear={handleClearLeftStickMapping}
                  isListening={isListening}
                  leftStickListening={leftStickListening}
                />
              </div>
            </CardContent>
          </Card>

          {/* Button List (Alternative) */}
          <Card>
            <CardHeader>
              <CardTitle className="text-base">
                {t('devices.controllerMapping.buttonList.title')}
              </CardTitle>
              <CardDescription>
                {t('devices.controllerMapping.buttonList.description')}
              </CardDescription>
            </CardHeader>
            <CardContent>
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3">
                {(Object.keys(mappings.buttons) as ControllerButton[]).map((button) => {
                  const mapping = mappings.buttons[button]
                  const isActive = isListening === button
                  const hasMapping =
                    Boolean(mapping?.key) ||
                    (mapping?.mouseButton !== undefined && mapping?.mouseButton !== null)
                  
                  return (
                    <div
                      key={button}
                      className={`p-3 rounded-lg border transition-all ${
                        isActive
                          ? 'border-blue-500 dark:border-blue-400 bg-blue-50 dark:bg-blue-900/30 animate-pulse'
                          : 'border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-800 hover:border-blue-300 dark:hover:border-blue-700'
                      }`}
                    >
                      <div className="flex items-center justify-between mb-2">
                        <span className="text-sm font-medium text-gray-900 dark:text-white">
                          {t(`devices.controllerMapping.buttons.${button}`)}
                        </span>
                        {mapping.key || mapping.mouseButton !== undefined ? (
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() => handleClearMapping(button)}
                            className="h-5 w-5 p-0 text-xs"
                          >
                            ✕
                          </Button>
                        ) : null}
                      </div>
                      <Button
                        variant={mapping.key || mapping.mouseButton !== undefined ? 'default' : 'outline'}
                        size="sm"
                        onClick={() => handleStartMapping(button)}
                        disabled={isActive}
                        className={`w-full ${
                          isActive
                            ? 'bg-blue-600 dark:bg-blue-600 text-white'
                            : ''
                        }`}
                      >
                        {isActive
                          ? t('devices.controllerMapping.listening')
                          : hasMapping
                          ? getMappingDisplay(mapping)
                          : t('devices.controllerMapping.mapButton')}
                      </Button>
                    </div>
                  )
                })}
              </div>
            </CardContent>
          </Card>

          {/* Action Buttons */}
          <div className="flex justify-end gap-3">
            <Button
              variant="outline"
              onClick={handleReset}
              disabled={isSaving}
            >
              <RotateCcw className="mr-2 h-4 w-4" />
              {t('devices.controllerMapping.reset')}
            </Button>
            <Button
              onClick={handleSave}
              disabled={isSaving}
              className="bg-blue-600 hover:bg-blue-700 dark:bg-blue-600 dark:hover:bg-blue-700 text-white"
            >
              <Save className="mr-2 h-4 w-4" />
              {isSaving ? t('common.loading') : t('common.save')}
            </Button>
          </div>
    </div>
  )
}

interface StickVisualizerProps {
  label: string
  x: number
  y: number
  info: string
  size?: number
}

function StickVisualizer({ label, x, y, info, size = 220 }: StickVisualizerProps) {
  const normalizedX = clampAxis(x)
  const normalizedY = clampAxis(y)
  const radius = size / 2 - 10
  const center = size / 2
  const effectiveRadius = radius - 12
  const pointerX = center + normalizedX * effectiveRadius
  const pointerY = center - normalizedY * effectiveRadius
  const clipPathId = `stickCircleClip-${label.replace(/\s+/g, '-')}`
  return (
    <div className="rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 p-4">
      <div className="flex items-center justify-between text-sm font-medium text-gray-900 dark:text-white">
        <span>{label}</span>
        <span className="text-xs text-gray-500 dark:text-gray-400">{info}</span>
      </div>
      <div className="relative mt-3 flex items-center justify-center">
        <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`}>
          <defs>
            <clipPath id={clipPathId}>
              <circle cx={center} cy={center} r={radius} />
            </clipPath>
          </defs>
          <circle
            cx={center}
            cy={center}
            r={radius}
            strokeDasharray="6 6"
            strokeWidth="1"
            className="fill-gray-100 stroke-gray-300 dark:fill-slate-800 dark:stroke-slate-600"
          />
          <line
            x1={center}
            y1={center - effectiveRadius}
            x2={center}
            y2={center + effectiveRadius}
            strokeWidth="1"
            className="stroke-gray-300 dark:stroke-slate-500"
          />
          <line
            x1={center - effectiveRadius}
            y1={center}
            x2={center + effectiveRadius}
            y2={center}
            strokeWidth="1"
            className="stroke-gray-300 dark:stroke-slate-500"
          />
          <circle
            cx={pointerX}
            cy={pointerY}
            r="10"
            strokeWidth="2"
            clipPath={`url(#${clipPathId})`}
            className="fill-blue-500 stroke-blue-700 dark:fill-blue-400 dark:stroke-blue-500"
          />
        </svg>
      </div>
    </div>
  )
}

interface TriggerVisualizerProps {
  label: string
  value: number
  description: string
  vertical?: boolean
}

function TriggerVisualizer({ label, value, description, vertical = false }: TriggerVisualizerProps) {
  const clampedValue = clampAnalog(value)
  const barStyle = vertical
    ? {
        height: '100%',
        width: '100%',
        transform: `scaleY(${clampedValue || 0})`,
        transformOrigin: 'bottom center',
      }
    : {
        width: '100%',
        transform: `scaleX(${clampedValue || 0})`,
        transformOrigin: 'left center',
      }

  return (
    <div
      className={`rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 p-4 ${
        vertical ? 'flex flex-col items-center gap-3' : ''
      }`}
      style={vertical ? { minWidth: '120px' } : undefined}
    >
      <div
        className={`text-sm font-medium text-gray-900 dark:text-white ${
          vertical ? 'flex flex-col items-center gap-1' : 'flex items-center justify-between w-full'
        }`}
      >
        <span>{label}</span>
        <span className="text-xs text-gray-500 dark:text-gray-400">{description}</span>
      </div>
      <div
         className={`rounded-full bg-gray-200 dark:bg-gray-800 ${
           vertical ? 'w-3 h-[180px] overflow-hidden flex items-end' : 'w-full h-2 overflow-hidden'
         }`}
      >
        <div
          className={`rounded-full bg-blue-500 transition-transform ${vertical ? 'w-full' : 'h-full'}`}
          style={barStyle}
        />
      </div>
    </div>
  )
}

