import i18n from '@/i18n/config'

/**
 * 根据错误码获取对应语言的错误消息
 * @param errorCode 错误码（数字或字符串）
 * @param fallbackMessage 如果找不到对应翻译，返回的默认消息
 * @returns 翻译后的错误消息
 */
export function translateErrorCode(
  errorCode: number | string | null | undefined,
  fallbackMessage?: string
): string {
  // 如果没有错误码，返回默认消息或原始消息
  if (errorCode === null || errorCode === undefined) {
    return fallbackMessage || ''
  }

  // 将错误码转换为字符串
  const codeStr = String(errorCode)

  // 尝试从 i18n 中获取翻译
  const translationKey = `common.errorCodes.${codeStr}`
  const translated = i18n.t(translationKey)

  // 如果翻译结果就是 key 本身（说明没有找到翻译），使用默认消息
  if (translated === translationKey) {
    return fallbackMessage || `Error ${codeStr}`
  }

  return translated
}

/**
 * 从 API 错误响应中提取并翻译错误消息
 * @param errorResponse API 错误响应对象
 * @returns 翻译后的错误消息
 */
export function getTranslatedErrorMessage(errorResponse: {
  errorCode?: number | string | null
  errorMessage?: string
}): string {
  // 优先使用错误码翻译
  if (errorResponse.errorCode !== null && errorResponse.errorCode !== undefined) {
    const translated = translateErrorCode(errorResponse.errorCode, errorResponse.errorMessage)
    return translated
  }

  // 如果没有错误码，直接返回原始错误消息
  return errorResponse.errorMessage || ''
}

