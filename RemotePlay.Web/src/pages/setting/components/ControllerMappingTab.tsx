// 这个文件将 ControllerMapping 的内容提取为 Tab 组件
// 由于 ControllerMapping.tsx 非常长，我们直接导入并包装它
import { useNavigate } from 'react-router-dom'
import ControllerMapping from '../ControllerMapping'

export default function ControllerMappingTab() {
  // ControllerMapping 组件内部已经有完整的逻辑
  // 我们只需要确保它能正常工作
  return <ControllerMapping />
}

