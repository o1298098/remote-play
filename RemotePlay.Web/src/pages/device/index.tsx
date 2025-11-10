import { useState, useEffect, useCallback } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { useToast } from '@/hooks/use-toast'
import { useGamepadNavigation } from '@/hooks/use-gamepad-navigation'
import { BindDeviceDialog } from './components/BindDeviceDialog'
import { DeviceSettingsDialog } from './components/DeviceSettingsDialog'
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
  const loadDevices = useCallback(async () => {
    setIsLoading(true)
    try {
      const response = await playStationService.getMyDevices()
      if (response.success && response.result) {
        const mappedDevices: ConsoleWithSettings[] = response.result.map(mapUserDeviceToConsole)
        setConsoles(mappedDevices)
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
  }, [mapUserDeviceToConsole, toast])

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
    setSettingsDialogOpen(true)
  }

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
        <h2 className="text-4xl font-bold text-gray-900 dark:text-white mb-12">
          {t('devices.title')}
        </h2>

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
      {selectedDeviceId && (
        <DeviceSettingsDialog
          open={settingsDialogOpen}
          onOpenChange={(openState) => {
            setSettingsDialogOpen(openState)
            if (!openState) {
              setSelectedDeviceSettings(undefined)
            }
          }}
          deviceId={selectedDeviceId}
          deviceName={selectedDeviceName}
          defaultSettings={selectedDeviceSettings}
          onSave={(settings) => {
            setConsoles((prevConsoles) =>
              prevConsoles.map((consoleItem) =>
                consoleItem.id === selectedDeviceId
                  ? { ...consoleItem, settings }
                  : consoleItem
              )
            )
            setSelectedDeviceSettings(settings)
          }}
        />
      )}
    </div>
  )
}

