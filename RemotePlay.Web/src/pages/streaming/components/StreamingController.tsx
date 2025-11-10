import { PS5ControllerLayout } from '@/components/PS5ControllerLayout'
import type { ControllerButton, ButtonMapping } from '@/pages/setting/ControllerMapping'

interface StreamingControllerProps {
  buttonMappings: Record<ControllerButton, ButtonMapping>
  onButtonClick: (button: ControllerButton) => void
}

export function StreamingController({
  buttonMappings,
  onButtonClick,
}: StreamingControllerProps) {
  return (
    <div className="bg-gray-900 rounded-lg p-6">
      <h2 className="text-lg font-bold mb-4">控制器</h2>
      <PS5ControllerLayout
        mappings={buttonMappings}
        onButtonClick={onButtonClick}
        isListening={null}
      />
    </div>
  )
}

