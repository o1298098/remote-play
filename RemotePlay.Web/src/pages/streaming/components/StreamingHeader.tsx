import { Button } from '@/components/ui/button'
import { ArrowLeft, Power, Gamepad2 } from 'lucide-react'
import { useGamepad } from '@/hooks/use-gamepad'
import { useTranslation } from 'react-i18next'

interface StreamingHeaderProps {
  deviceName: string
  connectionState: string
  isConnected: boolean
  isConnecting: boolean
  onBack: () => void
  onConnect: () => void
  onDisconnect: () => void
}

export function StreamingHeader({
  deviceName,
  connectionState,
  isConnected,
  isConnecting,
  onBack,
  onConnect,
  onDisconnect,
}: StreamingHeaderProps) {
  const { t } = useTranslation()
  const { isConnected: isGamepadConnected, connectedGamepads } = useGamepad()

  return (
    <div className="px-6 py-4 flex items-center justify-between" style={{ background: 'transparent' }}>
      <div className="flex items-center gap-4">
        <Button
          variant="ghost"
          size="icon"
          onClick={onBack}
          className="text-white hover:text-white hover:bg-white/20 transition-all border-2 border-white/40 hover:border-white/60 bg-transparent backdrop-blur-none shadow-none rounded-full"
          style={{ 
            pointerEvents: 'auto',
            width: '48px',
            height: '48px',
            borderRadius: '50%',
            background: 'transparent',
          }}
        >
          <ArrowLeft className="h-5 w-5" />
        </Button>
      </div>
      <div className="flex items-center gap-4 text-right">
        <div className="flex flex-col items-end text-sm text-white/80">
          <span className="font-semibold text-base">
            {deviceName || t('streaming.header.unnamedDevice')}
          </span>
          <span className="text-xs uppercase tracking-wide">
            {connectionState}
          </span>
        </div>
        {/* 手柄连接状态指示器 */}
        {isGamepadConnected && (
          <div className="flex items-center space-x-1 px-2 py-1 rounded-md bg-green-500/20 backdrop-blur-sm text-green-300 text-xs border border-green-400/30 animate-pulse">
            <Gamepad2 className="h-4 w-4" />
            <span>{t('streaming.header.gamepadCount', { count: connectedGamepads.length })}</span>
          </div>
        )}
        {!isConnected && !isConnecting && (
          <Button onClick={onConnect} className="bg-blue-600 hover:bg-blue-700">
            {t('streaming.header.connect')}
          </Button>
        )}
        {isConnected && (
          <Button onClick={onDisconnect} variant="destructive" size="icon">
            <Power className="h-5 w-5" />
          </Button>
        )}
      </div>
    </div>
  )
}

