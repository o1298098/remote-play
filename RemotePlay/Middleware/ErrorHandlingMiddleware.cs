using Newtonsoft.Json;

namespace RemotePlay.Utils
{
    public class ErrorHandlingMiddleware
    {
        private readonly RequestDelegate next;

        public ErrorHandlingMiddleware(RequestDelegate next)
        {
            this.next = next;
        }

        public async Task Invoke(HttpContext context)
        {
            try
            {
                await next(context);
            }
            catch (Exception ex)
            {
                // 如果响应已开始，不要覆盖
                if (context.Response.HasStarted)
                {
                    return;
                }

                var statusCode = context.Response.StatusCode;
                if (ex is ArgumentException)
                {
                    statusCode = 200;
                }
                await HandleExceptionAsync(context, statusCode, ex.Message);
                return; // 异常已处理，不需要再处理状态码
            }
            
            // 如果响应已开始（例如静态文件已成功提供），则不处理错误
            if (context.Response.HasStarted)
            {
                return;
            }

            // 只处理 API 请求的错误，不处理静态文件的 404
            var path = context.Request.Path.Value ?? "";
            var isApiRequest = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
            
            if (isApiRequest)
            {
                var statusCode = context.Response.StatusCode;
                var msg = "";
                if (statusCode == 401)
                {
                    msg = "未授权,请重新登录";
                }
                else if (statusCode == 404)
                {
                    msg = "未找到服务";
                }
                else if (statusCode == 502)
                {
                    msg = "请求错误";
                }
                else if (statusCode != 200 && statusCode != 400)
                {
                    msg = "未知错误";
                }
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    await HandleExceptionAsync(context, statusCode, msg);
                }
            }
        }
        private static Task HandleExceptionAsync(HttpContext context, int statusCode, string msg)
        {
            var result = JsonConvert.SerializeObject(new { success = false, Msg = msg, errorMessage = msg, Type = statusCode.ToString() });
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.ContentType = "application/json; charset=utf-8";
            }
            return context.Response.WriteAsync(result);
        }
    }
    public static class ErrorHandlingExtensions
    {
        public static IApplicationBuilder UseErrorHandling(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<ErrorHandlingMiddleware>();
        }
    }
}
