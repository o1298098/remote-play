import { PS5ControllerLayout } from '@/components/PS5ControllerLayout'
import {
  type ControllerButton,
  type ButtonMapping,
  type LeftStickMapping,
  type StickDirection,
  getDefaultLeftStickMapping,
} from '@/types/controller-mapping'

interface StreamingControllerProps {
  buttonMappings: Record<ControllerButton, ButtonMapping>
  onButtonClick: (button: ControllerButton) => void
}

const defaultLeftStickMapping: LeftStickMapping = getDefaultLeftStickMapping()
const noopLeftStickDirectionHandler = (_direction: StickDirection) => {}

export function StreamingController({
  buttonMappings,
  onButtonClick,
}: StreamingControllerProps) {
  return (
    <div className="bg-gray-900 rounded-lg p-6">
      <h2 className="text-lg font-bold mb-4">控制器</h2>
      <PS5ControllerLayout
        mappings={buttonMappings}
        leftStickMapping={defaultLeftStickMapping}
        onButtonClick={onButtonClick}
        onLeftStickDirectionClick={noopLeftStickDirectionHandler}
        onLeftStickDirectionClear={noopLeftStickDirectionHandler}
        isListening={null}
        leftStickListening={null}
      />
    </div>
  )
}
