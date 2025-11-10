namespace RemotePlay.Models.Base
{
    /// <summary>
    /// 统一的 API 成功响应模型
    /// </summary>
    /// <typeparam name="T">数据类型</typeparam>
    public class ApiSuccessResponse<T>
    {
        public bool Success { get; set; } = true;
        public T? Data { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 统一的 API 失败响应模型
    /// </summary>
    public class ApiErrorResponse
    {
        public bool Success { get; set; } = false;
        public string ErrorMessage { get; set; } = string.Empty;
    }
}

