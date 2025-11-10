import { useTranslation } from 'react-i18next'
import { Card, CardContent, CardHeader } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Settings as SettingsIcon, ChevronDown } from 'lucide-react'
import { PS4Icon } from '@/components/icons/PS4Icon'
import { PS5Icon } from '@/components/icons/PS5Icon'
import type { Console } from '@/types/device'

interface DeviceCardProps {
  consoleItem: Console
  onConnect: (console: Console) => void
  onRegister: (console: Console) => void
  onSettings: (deviceId: string, deviceName: string) => void
}

export function DeviceCard({ consoleItem, onConnect, onRegister, onSettings }: DeviceCardProps) {
  const { t } = useTranslation()
  const isOffline = consoleItem.status === 'offline'
  const isRegistered = consoleItem.isRegistered
  const isActionDisabled = isRegistered && isOffline

  const getConsoleIcon = (type: Console['type']) => {
    return type === 'PS5' ? (
      <PS5Icon className="h-24 w-24 text-blue-600 dark:text-blue-400" />
    ) : (
      <PS4Icon className="h-24 w-24 text-blue-600 dark:text-blue-400" />
    )
  }

  return (
    <Card
      className="group w-[280px] min-h-[420px] h-full bg-white dark:bg-gray-800 hover:bg-gradient-to-br hover:from-blue-50 dark:hover:from-blue-900/20 hover:to-indigo-50 dark:hover:to-indigo-900/20 rounded-2xl border-2 border-gray-200 dark:border-gray-700 hover:border-blue-400 dark:hover:border-blue-600 shadow-lg hover:shadow-2xl transition-all duration-300 overflow-hidden relative flex flex-col"
    >
      {/* 装饰性渐变背景 */}
      <div className="absolute inset-0 bg-gradient-to-br from-blue-500/5 dark:from-blue-500/10 via-transparent to-indigo-500/5 dark:to-indigo-500/10 opacity-0 group-hover:opacity-100 transition-opacity duration-300"></div>
      
      <CardHeader className="pt-8 pb-6 relative z-10">
        {/* 图标区域 - 带渐变背景圆环 */}
        <div className="relative mb-4">
          {/* 设置按钮 - 绝对定位在右上角 */}
          <Button
            variant="ghost"
            size="icon"
            className="absolute top-0 right-0 h-7 w-7 text-gray-400 dark:text-gray-500 hover:text-blue-600 dark:hover:text-blue-400 hover:bg-blue-50 dark:hover:bg-blue-900/30 rounded-full transition-all z-10"
            onClick={(e) => {
              e.stopPropagation()
              onSettings(consoleItem.id, consoleItem.name)
            }}
          >
            <SettingsIcon className="h-4 w-4" />
          </Button>
          
          {/* 图标容器 - 完全居中 */}
          <div className="flex justify-center items-center relative">
            {/* 背景装饰圆环 */}
            <div className="absolute inset-0 flex items-center justify-center">
              <div className="w-32 h-32 rounded-full bg-gradient-to-br from-blue-100 dark:from-blue-900/40 via-indigo-100 dark:via-indigo-900/40 to-purple-100 dark:to-purple-900/40 opacity-60 group-hover:opacity-100 group-hover:scale-110 transition-all duration-300 blur-xl"></div>
            </div>
            {/* 图标 */}
            <div className="relative transform group-hover:scale-110 transition-transform duration-300 flex items-center justify-center w-full">
              {getConsoleIcon(consoleItem.type)}
            </div>
          </div>
        </div>
        
        {/* 状态指示器 - 根据设备状态动态显示 */}
        <div className="flex items-center justify-center gap-2 mt-2">
          {consoleItem.status === 'available' && (
            <div className="flex items-center gap-1.5 px-3 py-1 bg-green-50 dark:bg-green-900/30 rounded-full border border-green-200 dark:border-green-800">
              <span className="w-2 h-2 bg-green-500 dark:bg-green-400 rounded-full animate-pulse"></span>
              <span className="text-green-700 dark:text-green-400 text-xs font-medium">{t('devices.console.status.online')}</span>
            </div>
          )}
          {consoleItem.status === 'standby' && (
            <div className="flex items-center gap-1.5 px-3 py-1 bg-yellow-50 dark:bg-yellow-900/30 rounded-full border border-yellow-200 dark:border-yellow-800">
              <span className="w-2 h-2 bg-yellow-500 dark:bg-yellow-400 rounded-full animate-pulse"></span>
              <span className="text-yellow-700 dark:text-yellow-400 text-xs font-medium">{t('devices.console.status.standby')}</span>
            </div>
          )}
          {consoleItem.status === 'offline' && (
            <div className="flex items-center gap-1.5 px-3 py-1 bg-gray-50 dark:bg-gray-800 rounded-full border border-gray-200 dark:border-gray-700">
              <span className="w-2 h-2 bg-gray-400 dark:bg-gray-500 rounded-full"></span>
              <span className="text-gray-600 dark:text-gray-400 text-xs font-medium">{t('devices.console.status.offline')}</span>
            </div>
          )}
        </div>
      </CardHeader>
      
      <CardContent className="px-6 pb-6 space-y-4 relative z-10 flex flex-col flex-grow">
        <div className="space-y-2 flex-grow">
          <h3 className="text-gray-900 dark:text-white text-lg font-bold group-hover:text-blue-900 dark:group-hover:text-blue-400 transition-colors">
            {consoleItem.name}
          </h3>
          <div className="space-y-1.5">
            {consoleItem.hostId && (
              <p className="text-gray-400 dark:text-gray-500 text-xs font-mono">
                {consoleItem.hostId}
              </p>
            )}
            {consoleItem.macAddress && (
              <p className="text-gray-400 dark:text-gray-500 text-xs font-mono">
                {consoleItem.macAddress}
              </p>
            )}
            {consoleItem.status === 'available' && consoleItem.isRegistered && (
              <p className="text-blue-600 dark:text-blue-400 text-sm font-semibold flex items-center gap-1.5">
                <span className="w-1.5 h-1.5 bg-blue-600 dark:bg-blue-400 rounded-full"></span>
                {t('devices.console.readyToPlay')}
              </p>
            )}
            {!consoleItem.isRegistered && (
              <p className="text-orange-600 dark:text-orange-400 text-sm font-semibold flex items-center gap-1.5">
                <span className="w-1.5 h-1.5 bg-orange-600 dark:bg-orange-400 rounded-full"></span>
                {t('devices.console.notRegistered')}
              </p>
            )}
          </div>
        </div>
        <div className="pt-4 border-t border-gray-200 dark:border-gray-700 group-hover:border-blue-200 dark:group-hover:border-blue-700 transition-colors mt-auto">
          <Button
            className={`w-full shadow-md hover:shadow-lg hover:scale-[1.02] transition-all duration-200 flex items-center justify-center gap-2 font-semibold ${
              isActionDisabled
                ? 'bg-gray-400 dark:bg-gray-600 hover:bg-gray-500 dark:hover:bg-gray-700 text-white cursor-not-allowed'
                : 'bg-gradient-to-r from-blue-600 to-indigo-600 hover:from-blue-700 hover:to-indigo-700 text-white'
            }`}
            variant="default"
            disabled={isActionDisabled}
            onClick={(e) => {
              e.stopPropagation()
              if (isRegistered) {
                onConnect(consoleItem)
              } else {
                onRegister(consoleItem)
              }
            }}
          >
            <span>
              {consoleItem.isRegistered 
                ? t('devices.console.connect') 
                : t('devices.console.register')}
            </span>
            <ChevronDown className="h-3.5 w-3.5 rotate-[-90deg] group-hover:translate-x-1 transition-transform" />
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}

