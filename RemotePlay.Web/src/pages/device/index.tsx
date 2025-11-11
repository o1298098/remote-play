import type { DragEvent } from 'react'
import { useState, useEffect, useCallback, useRef } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { useToast } from '@/hooks/use-toast'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { Edit3, Check } from 'lucide-react'
import { useGamepadNavigation } from '@/hooks/use-gamepad-navigation'
import { BindDeviceDialog } from './components/BindDeviceDialog'
import { DeviceSettingsDialog, type DeviceSettingsDialogProps } from './components/DeviceSettingsDialog'
import { DevicesHeader } from './components/DevicesHeader'
import { DeviceCard } from './components/DeviceCard'
import { AddDeviceCard } from './components/AddDeviceCard'
import { DeviceCardSkeleton } from './components/DeviceCardSkeleton'
import {
  playStationService,
  type UserDevice,
  type DeviceStreamingSettings,
} from '@/service/playstation.service'
import { useDeviceStatus } from '@/hooks/use-device-status'
import type { Console } from '@/types/device'
import { mapDeviceStatus } from '@/utils/device-status'

const DEVICE_ORDER_STORAGE_KEY = 'psrp_device_order'

const resolveDeviceKey = (device: { userDeviceId?: string | null; id: string }) =>
  device.userDeviceId || device.id

export default function Devices() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  type ConsoleWithSettings = Console & { settings?: DeviceStreamingSettings }

  const [consoles, setConsoles] = useState<ConsoleWithSettings[]>([])
  const [isLoading, setIsLoading] = useState(true)
  const [bindDialogOpen, setBindDialogOpen] = useState(false)
  const [settingsDialogOpen, setSettingsDialogOpen] = useState(false)
  const [selectedDeviceId, setSelectedDeviceId] = useState<string | null>(null)
  const [selectedDeviceName, setSelectedDeviceName] = useState<string>('')
  const [selectedDeviceSettings, setSelectedDeviceSettings] = useState<DeviceStreamingSettings | undefined>(undefined)
  const [selectedDeviceForRegister, setSelectedDeviceForRegister] = useState<ConsoleWithSettings | null>(null)
  const [selectedConsole, setSelectedConsole] = useState<ConsoleWithSettings | null>(null)
  const [draggingId, setDraggingId] = useState<string | null>(null)
  const [dragOverId, setDragOverId] = useState<string | null>(null)
  const originalOrderRef = useRef<ConsoleWithSettings[] | null>(null)
  const dropCompletedRef = useRef(false)
  const [isReorderMode, setIsReorderMode] = useState(false)
  const { toast } = useToast()

  // 启用手柄导航（只在没有对话框打开时启用）
  useGamepadNavigation(!bindDialogOpen && !settingsDialogOpen)

  // 将 UserDevice 转换为 Console 的辅助函数
  const mapUserDeviceToConsole = useCallback((device: UserDevice): ConsoleWithSettings => {
    return {
      id: device.deviceId,
      userDeviceId: device.userDeviceId,
      name: device.hostName,
      type: (device.hostType?.toUpperCase() === 'PS5' ? 'PS5' : 'PS4') as 'PS4' | 'PS5',
      status: mapDeviceStatus(device.status),
      statusText: device.status || 'Unknown',
      readyText: device.isRegistered ? 'Ready to play' : 'Not registered',
      ipAddress: device.ipAddress,
      macAddress: device.macAddress,
      hostId: device.hostId,
      isRegistered: device.isRegistered,
      settings: device.settings,
    }
  }, [])

  // 加载设备列表
  const applyStoredOrder = useCallback((devices: ConsoleWithSettings[]) => {
    if (typeof window === 'undefined') {
      return devices
    }

    try {
      const stored = localStorage.getItem(DEVICE_ORDER_STORAGE_KEY)
      if (!stored) {
        return devices
      }

      const order = JSON.parse(stored) as unknown
      if (!Array.isArray(order)) {
        return devices
      }

      const map = new Map(devices.map((device) => [resolveDeviceKey(device), device]))
      const sorted: ConsoleWithSettings[] = []

      for (const identifier of order as string[]) {
        const matched = map.get(identifier)
        if (matched) {
          sorted.push(matched)
          map.delete(identifier)
        }
      }

      if (map.size > 0) {
        sorted.push(...map.values())
      }

      return sorted
    } catch (error) {
      console.warn('加载设备排序信息失败:', error)
      return devices
    }
  }, [])

  const persistOrder = useCallback((devices: ConsoleWithSettings[]) => {
    if (typeof window === 'undefined') {
      return
    }
    const order = devices.map((device) => resolveDeviceKey(device))
    localStorage.setItem(DEVICE_ORDER_STORAGE_KEY, JSON.stringify(order))
  }, [])

  const loadDevices = useCallback(async () => {
    setIsLoading(true)
    try {
      const response = await playStationService.getMyDevices()
      if (response.success && response.result) {
        const mappedDevices: ConsoleWithSettings[] = response.result.map(mapUserDeviceToConsole)
        const orderedDevices = applyStoredOrder(mappedDevices)
        setConsoles(orderedDevices)
        persistOrder(orderedDevices)
      } else {
        setConsoles([])
      }
    } catch (error) {
      console.error('加载设备列表失败:', error)
      toast({
        title: '加载失败',
        description: error instanceof Error ? error.message : '无法加载设备列表',
        variant: 'destructive',
      })
      setConsoles([])
    } finally {
      setIsLoading(false)
    }
  }, [mapUserDeviceToConsole, toast, applyStoredOrder, persistOrder])

  // 处理设备状态更新（来自 SignalR 的已注册设备状态）
  const handleDevicesUpdate = useCallback((devices: UserDevice[]) => {
    
    setConsoles((prevConsoles) => {
      const updatedMap = new Map(devices.map((device) => [device.deviceId, device]))
      const existingIds = new Set(prevConsoles.map(c => c.id))
      
      // 更新现有设备
      const updated = prevConsoles.map((console) => {
        const updatedDevice = updatedMap.get(console.id)
        if (updatedDevice) {
          return mapUserDeviceToConsole(updatedDevice)
        }
        return console
      })
      
      // 添加新设备（如果设备不在列表中）
      const newDevices = devices
        .filter(device => !existingIds.has(device.deviceId))
        .map(mapUserDeviceToConsole)
      
      return [...updated, ...newDevices]
    })
  }, [mapUserDeviceToConsole])

  // 处理设备状态更新（来自 SignalR 的实时更新）
  const handleStatusUpdate = useCallback((updatedDevices: UserDevice[]) => {
    console.log('收到设备状态更新:', {
      count: updatedDevices?.length || 0,
      devices: updatedDevices,
    })
    
    if (!updatedDevices || updatedDevices.length === 0) {
      console.warn('收到设备状态更新，但设备列表为空')
      return
    }
    
    setConsoles((prevConsoles) => {
      const updatedMap = new Map(updatedDevices.map((device) => [device.deviceId, device]))
      const updated = prevConsoles.map((consoleItem) => {
        const updatedDevice = updatedMap.get(consoleItem.id)
        if (updatedDevice) {
          return mapUserDeviceToConsole(updatedDevice)
        }
        return consoleItem
      })
      
      // 检查是否有新设备需要添加
      const existingIds = new Set(prevConsoles.map(c => c.id))
      const newDevices = updatedDevices
        .filter(device => !existingIds.has(device.deviceId))
        .map(mapUserDeviceToConsole)
      
      if (newDevices.length > 0) {
        console.log('添加新设备:', newDevices)
      }
      
      return [...updated, ...newDevices]
    })
  }, [mapUserDeviceToConsole])

  // 使用 SignalR hook 监听设备状态更新
  useDeviceStatus({
    onDevicesUpdate: handleDevicesUpdate,
    onStatusUpdate: handleStatusUpdate,
  })

  // 初始加载设备列表
  useEffect(() => {
    loadDevices()
  }, [loadDevices])

  const handleConnect = (console: ConsoleWithSettings) => {
    if (console.status === 'offline') {
      toast({
        title: t('devices.console.offlineToastTitle'),
        description: t('devices.console.offlineToastDescription'),
        variant: 'destructive',
      })
      return
    }

    if (!console.isRegistered) {
      toast({
        title: '设备未注册',
        description: '请先注册设备后再连接',
        variant: 'destructive',
      })
      return
    }

    if (!console.hostId) {
      toast({
        title: '设备信息不完整',
        description: '缺少设备 Host ID',
        variant: 'destructive',
      })
      return
    }

    // 跳转到串流页面，传递设备信息
    navigate(`/streaming?hostId=${encodeURIComponent(console.hostId)}&deviceName=${encodeURIComponent(console.name)}`)
  }

  const handleAddConsole = () => {
    setBindDialogOpen(true)
  }

  const handleRegister = (console: ConsoleWithSettings) => {
    // 打开注册对话框，预填充设备信息
    setSelectedDeviceForRegister(console)
    setBindDialogOpen(true)
  }

  const handleBindSuccess = () => {
    loadDevices()
    setSelectedDeviceForRegister(null)
  }

  const handleSettings = (deviceId: string, deviceName: string) => {
    setSelectedDeviceId(deviceId)
    setSelectedDeviceName(deviceName)
    const targetConsole = consoles.find((console) => console.id === deviceId)
    setSelectedDeviceSettings(targetConsole?.settings)
    setSelectedConsole(targetConsole ?? null)
    setSettingsDialogOpen(true)
  }

  const reorderDevices = useCallback(
    (sourceId: string, targetId: string, options?: { persist?: boolean }) => {
      setConsoles((prevConsoles) => {
        if (sourceId === targetId) {
          return prevConsoles
        }

        const fromIndex = prevConsoles.findIndex(
          (item) => resolveDeviceKey(item) === sourceId
        )
        const toIndex = prevConsoles.findIndex(
          (item) => resolveDeviceKey(item) === targetId
        )

        if (fromIndex === -1 || toIndex === -1) {
          return prevConsoles
        }

        const updated = [...prevConsoles]
        const [moved] = updated.splice(fromIndex, 1)
        updated.splice(toIndex, 0, moved)

        if (options?.persist !== false) {
          persistOrder(updated)
        }

        return updated
      })
    },
    [persistOrder]
  )

  const handleCardDragStart = (event: DragEvent<HTMLDivElement>, device: ConsoleWithSettings) => {
    if (!isReorderMode) {
      event.preventDefault()
      return
    }
    const identifier = resolveDeviceKey(device)
    setDraggingId(identifier)
    setDragOverId(null)
    dropCompletedRef.current = false
    originalOrderRef.current = [...consoles]
    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = 'move'
      event.dataTransfer.setData('text/plain', identifier)
    }
  }

  const handleCardDragEnter = (_event: DragEvent<HTMLDivElement>, device: ConsoleWithSettings) => {
    if (!isReorderMode) {
      return
    }
    const identifier = resolveDeviceKey(device)
    if (!draggingId || draggingId === identifier) {
      return
    }
    setDragOverId(identifier)
  }

  const handleCardDragOver = (_event: DragEvent<HTMLDivElement>, device: ConsoleWithSettings) => {
    if (!isReorderMode) {
      return
    }
    const identifier = resolveDeviceKey(device)
    if (!draggingId || draggingId === identifier) {
      return
    }
    if (dragOverId !== identifier) {
      setDragOverId(identifier)
      reorderDevices(draggingId, identifier, { persist: false })
    } else {
      reorderDevices(draggingId, identifier, { persist: false })
    }
  }

  const handleCardDragLeave = (_event: DragEvent<HTMLDivElement>, device: ConsoleWithSettings) => {
    if (!isReorderMode) {
      return
    }
    const identifier = resolveDeviceKey(device)
    if (dragOverId === identifier) {
      setDragOverId(null)
    }
  }

  const handleCardDrop = (_event: DragEvent<HTMLDivElement>, device: ConsoleWithSettings) => {
    if (!isReorderMode) {
      return
    }
    const identifier = resolveDeviceKey(device)
    if (draggingId && identifier && draggingId !== identifier) {
      reorderDevices(draggingId, identifier)
      dropCompletedRef.current = true
    } else {
      // 即使未发生序列变化，也需要持久化当前顺序
      setConsoles((prev) => {
        persistOrder(prev)
        return prev
      })
      dropCompletedRef.current = true
    }
    setDraggingId(null)
    setDragOverId(null)
  }

  const handleCardDragEnd = () => {
    if (!isReorderMode) {
      return
    }
    if (!dropCompletedRef.current && originalOrderRef.current) {
      setConsoles(originalOrderRef.current)
    }
    setDraggingId(null)
    setDragOverId(null)
    originalOrderRef.current = null
    dropCompletedRef.current = false
  }

  const handleReorderToggle = useCallback(
    (next: boolean) => {
      if (next && consoles.length <= 1) {
        return false
      }
      setIsReorderMode((prev) => {
        if (prev === next) {
          return prev
        }
        if (next) {
          toast({
            title: t('devices.console.reorderModeEnabledTitle'),
            description: t('devices.console.reorderModeEnabledDescription'),
          })
        } else {
          setDraggingId(null)
          setDragOverId(null)
          originalOrderRef.current = null
          dropCompletedRef.current = false
        }
        return next
      })
      return true
    },
    [consoles.length, toast, t]
  )

  useEffect(() => {
    if (isReorderMode && consoles.length <= 1) {
      setIsReorderMode(false)
    }
  }, [consoles.length, isReorderMode])

  const handleDelete = async (consoleItem: ConsoleWithSettings) => {
    const confirmed = window.confirm(
      t('devices.console.deleteConfirm', {
        name: consoleItem.name,
      })
    )

    if (!confirmed) {
      return
    }

    if (!consoleItem.userDeviceId) {
      toast({
        title: t('devices.console.deleteFailedTitle'),
        description: t('devices.console.deleteInvalidId'),
        variant: 'destructive',
      })
      return
    }

    try {
      const response = await playStationService.unbindDevice(consoleItem.userDeviceId)
      if (response.success) {
        setConsoles((prevConsoles) => {
          const updated = prevConsoles.filter(
            (item) => resolveDeviceKey(item) !== resolveDeviceKey(consoleItem)
          )
          persistOrder(updated)
          return updated
        })

        toast({
          title: t('devices.console.deleteSuccessTitle'),
          description: t('devices.console.deleteSuccessDescription', {
            name: consoleItem.name,
          }),
        })
        if (selectedConsole?.userDeviceId === consoleItem.userDeviceId) {
          setSettingsDialogOpen(false)
          setSelectedDeviceId(null)
          setSelectedDeviceName('')
          setSelectedDeviceSettings(undefined)
          setSelectedConsole(null)
        }
      } else {
        toast({
          title: t('devices.console.deleteFailedTitle'),
          description: response.errorMessage || t('devices.console.deleteFailedDescription'),
          variant: 'destructive',
        })
      }
    } catch (error) {
      console.error('删除设备失败:', error)
      toast({
        title: t('devices.console.deleteFailedTitle'),
        description:
          error instanceof Error
            ? error.message
            : t('devices.console.deleteFailedDescription'),
        variant: 'destructive',
      })
    } finally {
      // no-op
    }
  }

  const deviceSettingsDialogProps: DeviceSettingsDialogProps | null = selectedDeviceId
    ? {
        open: settingsDialogOpen,
        onOpenChange: (openState) => {
          setSettingsDialogOpen(openState)
          if (!openState) {
            setSelectedDeviceSettings(undefined)
            setSelectedConsole(null)
          }
        },
        deviceId: selectedDeviceId,
        deviceName: selectedDeviceName,
        defaultSettings: selectedDeviceSettings,
        onDelete: selectedConsole
          ? () => handleDelete(selectedConsole)
          : undefined,
        onSave: (settings) => {
          setConsoles((prevConsoles) =>
            prevConsoles.map((consoleItem) =>
              consoleItem.id === selectedDeviceId
                ? { ...consoleItem, settings }
                : consoleItem
            )
          )
          setSelectedDeviceSettings(settings)
        },
      }
    : null

  return (
    <div className="min-h-screen bg-white dark:bg-gray-950 relative overflow-hidden">
      {/* 背景装饰 - 蓝色波浪图案 */}
      <div className="absolute inset-0 overflow-hidden pointer-events-none">
        <div className="absolute top-0 right-0 w-[600px] h-[600px] bg-gradient-to-br from-blue-100/30 dark:from-blue-900/20 to-blue-200/20 dark:to-blue-800/10 rounded-full blur-3xl"></div>
        <div className="absolute top-1/4 right-1/4 w-[400px] h-[400px] bg-gradient-to-br from-blue-200/20 dark:from-indigo-900/15 to-indigo-100/30 dark:to-indigo-800/10 rounded-full blur-3xl"></div>
        <div className="absolute bottom-0 left-0 w-[500px] h-[500px] bg-gradient-to-tr from-blue-50/40 dark:from-blue-900/15 to-transparent rounded-full blur-3xl"></div>
      </div>

      {/* Header */}
      <DevicesHeader />

      {/* Main Content */}
      <main className="relative z-10 container mx-auto px-6 py-12">
        {/* 主标题 */}
        <div className="flex items-center gap-3 mb-12">
          <h2 className="text-4xl font-bold text-gray-900 dark:text-white">
            {t('devices.title')}
          </h2>
          {consoles.length > 1 && (
            <Button
              type="button"
              variant="ghost"
              size="icon"
              className={cn(
                'h-10 w-10 rounded-full border transition-all',
                isReorderMode
                  ? 'border-blue-500 bg-blue-600 text-white hover:bg-blue-600/90 hover:text-white'
                  : 'border-transparent text-gray-500 hover:text-blue-600 hover:bg-blue-50 dark:hover:bg-blue-900/20'
              )}
              onClick={() => {
                handleReorderToggle(!isReorderMode)
              }}
              aria-pressed={isReorderMode}
              aria-label={
                isReorderMode
                  ? t('devices.console.disableReorder')
                  : t('devices.console.enableReorder')
              }
            >
              {isReorderMode ? <Check className="h-4 w-4" /> : <Edit3 className="h-4 w-4" />}
              <span className="sr-only">
                {isReorderMode
                  ? t('devices.console.disableReorder')
                  : t('devices.console.enableReorder')}
              </span>
            </Button>
          )}
        </div>

        {/* 主机卡片 */}
        <div className="flex flex-wrap gap-6 items-stretch">
          {isLoading ? (
            <div className="flex gap-6">
              {[1, 2].map((i) => (
                <DeviceCardSkeleton key={i} />
              ))}
            </div>
          ) : (
            <>
              {/* 已有主机卡片 */}
              {consoles.map((consoleItem) => (
                <DeviceCard
                  key={consoleItem.id}
                  consoleItem={consoleItem}
                  onConnect={handleConnect}
                  onRegister={handleRegister}
                  onSettings={handleSettings}
                  onDragStart={consoles.length > 1 ? handleCardDragStart : undefined}
                  onDragEnter={consoles.length > 1 ? handleCardDragEnter : undefined}
                  onDragOver={consoles.length > 1 ? handleCardDragOver : undefined}
                  onDragLeave={consoles.length > 1 ? handleCardDragLeave : undefined}
                  onDrop={consoles.length > 1 ? handleCardDrop : undefined}
                  onDragEnd={consoles.length > 1 ? handleCardDragEnd : undefined}
                  isDragging={
                    consoles.length > 1 &&
                    draggingId === resolveDeviceKey(consoleItem)
                  }
                  isDragOver={
                    consoles.length > 1 &&
                    dragOverId === resolveDeviceKey(consoleItem) &&
                    draggingId !== resolveDeviceKey(consoleItem)
                  }
                  isReorderMode={isReorderMode}
                />
              ))}

              {/* 添加主机卡片 */}
              <AddDeviceCard onClick={handleAddConsole} />
            </>
          )}
        </div>
      </main>

      {/* 绑定设备对话框 */}
      <BindDeviceDialog
        open={bindDialogOpen}
        onOpenChange={(open) => {
          setBindDialogOpen(open)
           if (!open) {
             setSelectedDeviceForRegister(null)
           }
        }}
        onSuccess={handleBindSuccess}
        initialDevice={selectedDeviceForRegister ? {
          hostIp: selectedDeviceForRegister.ipAddress,
          deviceName: selectedDeviceForRegister.name,
          hostId: selectedDeviceForRegister.hostId,
        } : undefined}
      />

      {/* 设备设置对话框 */}
      {deviceSettingsDialogProps && <DeviceSettingsDialog {...deviceSettingsDialogProps} />}
    </div>
  )
}

